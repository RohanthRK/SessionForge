using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using SessionForge.Core.Models;

namespace SessionForge.Protocols.Local;

/// <summary>
/// Local shell (cmd, PowerShell, WSL, any console program) hosted in a Windows pseudo console (ConPTY, Windows 10 1809+).
/// </summary>
public sealed class ConPtyTransport : ITerminalTransport
{
    private readonly LocalShellOptions _o;
    private IntPtr _hPC;
    private SafeFileHandle? _inputWrite, _outputRead;
    private FileStream? _input, _output;
    private Native.PROCESS_INFORMATION _pi;
    private int _closed;
    private readonly object _writeLock = new();

    public ConPtyTransport(LocalShellOptions options)
    {
        _o = options;
        DisplayName = options.CommandLine;
    }

    public string DisplayName { get; }
    public TransportState State { get; private set; }
    public int ProcessId => _pi.dwProcessId;
    public event Action<ReadOnlyMemory<byte>>? DataReceived;
    public event Action<string?>? Closed;
    public event Action<string>? Notice;

    public static bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763);

    public Task ConnectAsync(int columns, int rows, CancellationToken cancellationToken)
    {
        if (!IsSupported) throw new PlatformNotSupportedException("Local terminals require Windows 10 1809 or later (ConPTY).");
        State = TransportState.Connecting;

        if (!Native.CreatePipe(out var inputRead, out _inputWrite, IntPtr.Zero, 0)) throw new Win32Exception();
        if (!Native.CreatePipe(out _outputRead, out var outputWrite, IntPtr.Zero, 0)) throw new Win32Exception();

        var size = new Native.COORD { X = (short)Math.Clamp(columns, 1, short.MaxValue), Y = (short)Math.Clamp(rows, 1, short.MaxValue) };
        int hr = Native.CreatePseudoConsole(size, inputRead, outputWrite, 0, out _hPC);
        // The pseudo console duplicates these; close our copies so EOF propagates when the shell exits.
        inputRead.Dispose();
        outputWrite.Dispose();
        if (hr != 0) throw new Win32Exception(hr, "CreatePseudoConsole failed.");

        var si = new Native.STARTUPINFOEX();
        si.StartupInfo.cb = Marshal.SizeOf<Native.STARTUPINFOEX>();
        IntPtr attrSize = IntPtr.Zero;
        Native.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attrSize);
        si.lpAttributeList = Marshal.AllocHGlobal(attrSize);
        try
        {
            if (!Native.InitializeProcThreadAttributeList(si.lpAttributeList, 1, 0, ref attrSize)) throw new Win32Exception();
            if (!Native.UpdateProcThreadAttribute(si.lpAttributeList, 0, (IntPtr)Native.PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, _hPC, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception();

            var env = BuildEnvironmentBlock(_o.Environment);
            var cwd = string.IsNullOrWhiteSpace(_o.WorkingDirectory)
                ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                : Environment.ExpandEnvironmentVariables(_o.WorkingDirectory);
            if (!Directory.Exists(cwd)) cwd = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            var cmd = new StringBuilder(Environment.ExpandEnvironmentVariables(_o.CommandLine));
            var envPtr = Marshal.StringToHGlobalUni(env);
            try
            {
                if (!Native.CreateProcessW(null, cmd, IntPtr.Zero, IntPtr.Zero, false,
                        Native.EXTENDED_STARTUPINFO_PRESENT | Native.CREATE_UNICODE_ENVIRONMENT, envPtr, cwd, ref si, out _pi))
                {
                    var err = Marshal.GetLastWin32Error();
                    throw new Win32Exception(err, $"Could not start '{_o.CommandLine}': {new Win32Exception(err).Message}");
                }
            }
            finally { Marshal.FreeHGlobal(envPtr); }
        }
        catch
        {
            State = TransportState.Failed;
            Cleanup();
            throw;
        }
        finally
        {
            Native.DeleteProcThreadAttributeList(si.lpAttributeList);
            Marshal.FreeHGlobal(si.lpAttributeList);
        }

        _input = new FileStream(_inputWrite!, FileAccess.Write, 1, false);
        _output = new FileStream(_outputRead!, FileAccess.Read, 1, false);
        State = TransportState.Connected;
        Notice?.Invoke($"Started {_o.CommandLine} (PID {_pi.dwProcessId})");

        new Thread(ReadLoop) { IsBackground = true, Name = "conpty-read" }.Start();
        new Thread(WaitForExit) { IsBackground = true, Name = "conpty-wait" }.Start();
        return Task.CompletedTask;
    }

    private static string BuildEnvironmentBlock(Dictionary<string, string> extra)
    {
        var vars = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables()) vars[(string)e.Key] = (string?)e.Value ?? string.Empty;
        foreach (var (k, v) in extra) vars[k] = v;
        vars["TERM_PROGRAM"] = "SessionForge";
        var sb = new StringBuilder();
        foreach (var (k, v) in vars) sb.Append(k).Append('=').Append(v).Append('\0');
        sb.Append('\0');
        return sb.ToString();
    }

    private void ReadLoop()
    {
        var buf = new byte[16 * 1024];
        try
        {
            while (true)
            {
                int n = _output!.Read(buf, 0, buf.Length);
                if (n <= 0) break;
                DataReceived?.Invoke(buf.AsMemory(0, n));
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        // EOF only happens after the pseudo console is closed; the exit watcher reports the outcome.
    }

    private void WaitForExit()
    {
        var hProcess = _pi.hProcess;
        Native.WaitForSingleObject(hProcess, Native.INFINITE);
        Native.GetExitCodeProcess(hProcess, out uint code);
        // Give the read loop a moment to drain final output, then close the pseudo console to signal EOF.
        Thread.Sleep(150);
        ClosePseudoConsoleOnce();
        Close(code == 0 ? null : $"Process exited with code {code} (0x{code:X}).");
    }

    private void ClosePseudoConsoleOnce()
    {
        var h = Interlocked.Exchange(ref _hPC, IntPtr.Zero);
        if (h != IntPtr.Zero) Native.ClosePseudoConsole(h);
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        if (State != TransportState.Connected) return;
        try { lock (_writeLock) { _input!.Write(data); _input.Flush(); } }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { Close(ex.Message); }
    }

    public void Resize(int columns, int rows, int pixelWidth, int pixelHeight)
    {
        if (_hPC == IntPtr.Zero) return;
        Native.ResizePseudoConsole(_hPC, new Native.COORD { X = (short)Math.Clamp(columns, 1, short.MaxValue), Y = (short)Math.Clamp(rows, 1, short.MaxValue) });
    }

    private void Close(string? reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) == 1) return;
        State = reason == null ? TransportState.Closed : TransportState.Failed;
        Closed?.Invoke(reason);
    }

    private void Cleanup()
    {
        ClosePseudoConsoleOnce();
        _input?.Dispose();
        _output?.Dispose();
        _inputWrite?.Dispose();
        _outputRead?.Dispose();
        if (_pi.hProcess != IntPtr.Zero) { Native.CloseHandle(_pi.hProcess); _pi.hProcess = IntPtr.Zero; }
        if (_pi.hThread != IntPtr.Zero) { Native.CloseHandle(_pi.hThread); _pi.hThread = IntPtr.Zero; }
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _closed, 1);
        if (_pi.hProcess != IntPtr.Zero && Native.WaitForSingleObject(_pi.hProcess, 0) != 0)
        {
            // Closing the pseudo console sends CTRL_CLOSE_EVENT to attached processes; terminate as a fallback.
            ClosePseudoConsoleOnce();
            if (Native.WaitForSingleObject(_pi.hProcess, 1500) != 0) Native.TerminateProcess(_pi.hProcess, 1);
        }
        Cleanup();
        State = TransportState.Closed;
        return ValueTask.CompletedTask;
    }

    private static class Native
    {
        public const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
        public const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
        public const int PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;
        public const uint INFINITE = 0xFFFFFFFF;

        [StructLayout(LayoutKind.Sequential)]
        public struct COORD { public short X; public short Y; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct STARTUPINFO
        {
            public int cb;
            public string? lpReserved, lpDesktop, lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct STARTUPINFOEX { public STARTUPINFO StartupInfo; public IntPtr lpAttributeList; }

        [StructLayout(LayoutKind.Sequential)]
        public struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, IntPtr lpPipeAttributes, int nSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern int CreatePseudoConsole(COORD size, SafeFileHandle hInput, SafeFileHandle hOutput, uint dwFlags, out IntPtr phPC);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern void ClosePseudoConsole(IntPtr hPC);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool CreateProcessW(string? lpApplicationName, StringBuilder lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
            bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string? lpCurrentDirectory, ref STARTUPINFOEX lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr hObject);
    }
}
