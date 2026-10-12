using System.Net;
using Renci.SshNet;
using SessionForge.Core.Models;

namespace SessionForge.Protocols.Ssh;

public enum ForwardStatus { Stopped, Running, Failed }

public sealed class ForwardState
{
    public required PortForward Definition { get; init; }
    public ForwardStatus Status { get; internal set; }
    public string? Error { get; internal set; }
    public uint EffectiveBindPort { get; internal set; }
    internal ForwardedPort? Port { get; set; }

    public override string ToString() => $"{Definition} [{Status}{(Error != null ? ": " + Error : "")}]";
}

/// <summary>Starts/stops local, remote and dynamic (SOCKS) forwards on a connected SSH client and tracks their status.</summary>
public sealed class PortForwardManager : IDisposable
{
    private readonly SshClient _client;
    private readonly List<ForwardState> _states = [];
    private readonly object _lock = new();

    public PortForwardManager(SshClient client) => _client = client;

    public event Action? Changed;
    public event Action<string>? Notice;

    public IReadOnlyList<ForwardState> States { get { lock (_lock) return _states.ToList(); } }

    public ForwardState Start(PortForward def)
    {
        ForwardState state;
        lock (_lock)
        {
            state = _states.FirstOrDefault(s => s.Definition.Id == def.Id) ?? new ForwardState { Definition = def };
            if (!_states.Contains(state)) _states.Add(state);
            if (state.Status == ForwardStatus.Running) return state;
        }
        try
        {
            if (!IsLoopback(def.BindAddress) && def.Kind != ForwardKind.Remote)
                Notice?.Invoke($"Warning: forward {def} listens on {def.BindAddress}, which exposes it to other machines on the network.");
            ForwardedPort port = def.Kind switch
            {
                ForwardKind.Local => new ForwardedPortLocal(def.BindAddress, def.BindPort, def.TargetHost, def.TargetPort),
                ForwardKind.Remote => new ForwardedPortRemote(def.BindAddress, def.BindPort, def.TargetHost, def.TargetPort),
                _ => new ForwardedPortDynamic(def.BindAddress, def.BindPort),
            };
            port.Exception += (_, e) =>
            {
                state.Error = e.Exception.Message;
                Notice?.Invoke($"Forward {def}: {e.Exception.Message}");
                Changed?.Invoke();
            };
            _client.AddForwardedPort(port);
            port.Start();
            state.Port = port;
            state.Status = ForwardStatus.Running;
            state.Error = null;
            state.EffectiveBindPort = port is ForwardedPortLocal l ? l.BoundPort : def.BindPort;
            Notice?.Invoke($"Forwarding started: {def}");
        }
        catch (Exception ex)
        {
            state.Status = ForwardStatus.Failed;
            state.Error = ex.Message;
            Notice?.Invoke($"Could not start forward {def}: {ex.Message}");
        }
        Changed?.Invoke();
        return state;
    }

    public void Stop(Guid forwardId)
    {
        ForwardState? state;
        lock (_lock) state = _states.FirstOrDefault(s => s.Definition.Id == forwardId);
        if (state?.Port == null) return;
        try
        {
            if (state.Port.IsStarted) state.Port.Stop();
            _client.RemoveForwardedPort(state.Port);
            state.Port.Dispose();
        }
        catch (Exception ex) { state.Error = ex.Message; }
        state.Port = null;
        state.Status = ForwardStatus.Stopped;
        Changed?.Invoke();
    }

    public void StartAll(IEnumerable<PortForward> defs)
    {
        foreach (var d in defs.Where(d => d.Enabled)) Start(d);
    }

    private static bool IsLoopback(string addr) =>
        addr is "localhost" || (IPAddress.TryParse(addr, out var ip) && IPAddress.IsLoopback(ip));

    public void Dispose()
    {
        foreach (var s in States) Stop(s.Definition.Id);
    }
}
