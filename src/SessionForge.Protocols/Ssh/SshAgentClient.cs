using System.Buffers.Binary;
using System.ComponentModel;
using System.IO.MemoryMappedFiles;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Renci.SshNet;
using Renci.SshNet.Security;

namespace SessionForge.Protocols.Ssh;

public sealed record AgentIdentity(byte[] KeyBlob, string Comment)
{
    public string KeyType => new SshReader(KeyBlob).String();
    public string Fingerprint => SessionForge.Core.Security.KnownHostsStore.Fingerprint(KeyBlob);
}

/// <summary>
/// SSH agent protocol client (draft-miller-ssh-agent). Talks to the Windows OpenSSH agent
/// (named pipe "openssh-ssh-agent") or PuTTY's Pageant (WM_COPYDATA shared memory protocol).
/// </summary>
public sealed class SshAgentClient
{
    private const byte RequestIdentities = 11, IdentitiesAnswer = 12, SignRequest = 13, SignResponse = 14, Failure = 5;
    public const uint FlagRsaSha256 = 2, FlagRsaSha512 = 4;
    private const int MaxMessage = 256 * 1024;

    public enum Backend { WindowsOpenSsh, Pageant }

    public SshAgentClient(Backend backend) => Kind = backend;

    public Backend Kind { get; }

    /// <summary>Returns the agents that appear to be running.</summary>
    public static IReadOnlyList<SshAgentClient> Detect()
    {
        var list = new List<SshAgentClient>();
        if (File.Exists(@"\\.\pipe\openssh-ssh-agent")) list.Add(new SshAgentClient(Backend.WindowsOpenSsh));
        if (FindWindow("Pageant", "Pageant") != IntPtr.Zero) list.Add(new SshAgentClient(Backend.Pageant));
        return list;
    }

    public IReadOnlyList<AgentIdentity> ListIdentities()
    {
        var resp = Transact([RequestIdentities]);
        var r = new SshReader(resp);
        var type = r.Byte();
        if (type != IdentitiesAnswer) throw new InvalidOperationException($"Agent refused identity listing (type {type}).");
        var n = r.UInt32();
        var list = new List<AgentIdentity>();
        for (uint i = 0; i < n && !r.AtEnd; i++) list.Add(new AgentIdentity(r.Bytes(), r.String()));
        return list;
    }

    /// <summary>Asks the agent to sign data. Returns the SSH signature blob (string algorithm, string signature).</summary>
    public byte[] Sign(byte[] keyBlob, byte[] data, uint flags)
    {
        var req = new SshWriter().Byte(SignRequest).String(keyBlob).String(data).UInt32(flags).ToArray();
        var resp = Transact(req);
        var r = new SshReader(resp);
        var type = r.Byte();
        if (type == Failure) throw new InvalidOperationException("The SSH agent refused to sign (key locked, confirmation denied, or removed).");
        if (type != SignResponse) throw new InvalidOperationException($"Unexpected agent response {type}.");
        return r.Bytes();
    }

    private byte[] Transact(byte[] payload)
    {
        var framed = new byte[payload.Length + 4];
        BinaryPrimitives.WriteUInt32BigEndian(framed, (uint)payload.Length);
        payload.CopyTo(framed, 4);
        return Kind == Backend.WindowsOpenSsh ? TransactPipe(framed) : TransactPageant(framed);
    }

    private static byte[] TransactPipe(byte[] framed)
    {
        using var pipe = new NamedPipeClientStream(".", "openssh-ssh-agent", PipeDirection.InOut);
        pipe.Connect(3000);
        pipe.Write(framed);
        pipe.Flush();
        Span<byte> lenBuf = stackalloc byte[4];
        pipe.ReadExactly(lenBuf);
        var len = BinaryPrimitives.ReadUInt32BigEndian(lenBuf);
        if (len == 0 || len > MaxMessage) throw new InvalidDataException("Invalid agent response length.");
        var resp = new byte[len];
        pipe.ReadExactly(resp);
        return resp;
    }

    #region Pageant (WM_COPYDATA)

    private const uint AgentCopyDataId = 0x804e50ba;
    private const int WmCopyData = 0x004A;

    [StructLayout(LayoutKind.Sequential)]
    private struct CopyDataStruct
    {
        public IntPtr dwData;
        public int cbData;
        public IntPtr lpData;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref CopyDataStruct lParam);

    private static byte[] TransactPageant(byte[] framed)
    {
        var hwnd = FindWindow("Pageant", "Pageant");
        if (hwnd == IntPtr.Zero) throw new InvalidOperationException("Pageant is not running.");
        var mapName = $"PageantRequest{Environment.CurrentManagedThreadId:x8}{Guid.NewGuid():N}";
        using var mmf = MemoryMappedFile.CreateNew(mapName, MaxMessage);
        using var view = mmf.CreateViewAccessor(0, MaxMessage);
        view.WriteArray(0, framed, 0, framed.Length);
        var nameBytes = System.Text.Encoding.ASCII.GetBytes(mapName + "\0");
        var ptr = Marshal.AllocHGlobal(nameBytes.Length);
        try
        {
            Marshal.Copy(nameBytes, 0, ptr, nameBytes.Length);
            // ULONG_PTR, zero-extended (PuTTY compares against 0x804e50ba as an unsigned value).
            var cds = new CopyDataStruct { dwData = unchecked((nint)(nuint)AgentCopyDataId), cbData = nameBytes.Length, lpData = ptr };
            var result = SendMessage(hwnd, WmCopyData, IntPtr.Zero, ref cds);
            if (result == IntPtr.Zero) throw new Win32Exception("Pageant did not accept the request.");
        }
        finally { Marshal.FreeHGlobal(ptr); }
        var lenBuf = new byte[4];
        view.ReadArray(0, lenBuf, 0, 4);
        var len = BinaryPrimitives.ReadUInt32BigEndian(lenBuf);
        if (len == 0 || len > MaxMessage - 4) throw new InvalidDataException("Invalid Pageant response length.");
        var resp = new byte[len];
        view.ReadArray(4, resp, 0, (int)len);
        return resp;
    }

    #endregion
}

/// <summary>Adapts an agent-held key to SSH.NET public key authentication.</summary>
public sealed class AgentHostAlgorithm : HostAlgorithm
{
    private readonly SshAgentClient _agent;
    private readonly AgentIdentity _identity;
    private readonly uint _flags;

    public AgentHostAlgorithm(string algorithmName, SshAgentClient agent, AgentIdentity identity, uint flags) : base(algorithmName)
    {
        _agent = agent;
        _identity = identity;
        _flags = flags;
    }

    public override byte[] Data => _identity.KeyBlob;

    public override byte[] Sign(byte[] data) => _agent.Sign(_identity.KeyBlob, data, _flags);

    public override bool VerifySignature(byte[] data, byte[] signature) => throw new NotSupportedException();
}

/// <summary>Exposes every key of every detected agent as an SSH.NET key source.</summary>
public sealed class AgentKeySource : IPrivateKeySource
{
    public AgentKeySource(IEnumerable<SshAgentClient> agents)
    {
        var list = new List<HostAlgorithm>();
        foreach (var agent in agents)
        {
            IReadOnlyList<AgentIdentity> ids;
            try { ids = agent.ListIdentities(); }
            catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException or InvalidDataException or Win32Exception) { continue; }
            foreach (var id in ids)
            {
                if (id.KeyType == "ssh-rsa")
                {
                    // Prefer SHA-2 signatures; ssh-rsa (SHA-1) is disabled on modern servers.
                    list.Add(new AgentHostAlgorithm("rsa-sha2-512", agent, id, SshAgentClient.FlagRsaSha512));
                    list.Add(new AgentHostAlgorithm("rsa-sha2-256", agent, id, SshAgentClient.FlagRsaSha256));
                }
                else list.Add(new AgentHostAlgorithm(id.KeyType, agent, id, 0));
            }
        }
        HostKeyAlgorithms = list;
    }

    public IReadOnlyCollection<HostAlgorithm> HostKeyAlgorithms { get; }

    public bool IsEmpty => HostKeyAlgorithms.Count == 0;
}
