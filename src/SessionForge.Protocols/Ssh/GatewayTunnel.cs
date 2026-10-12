using Renci.SshNet;
using Session = SessionForge.Core.Models.Session;

namespace SessionForge.Protocols.Ssh;

/// <summary>
/// Tunnels a TCP service (RDP, VNC, …) through a session's SSH jump-host chain: connects the chain and opens a
/// loopback-only local forward on the last hop to the service. Connect the client to 127.0.0.1:<see cref="LocalPort"/>.
/// </summary>
public sealed class GatewayTunnel : IDisposable
{
    private readonly SshConnectionContext _ctx;
    private readonly ForwardedPortLocal _forward;

    private GatewayTunnel(SshConnectionContext ctx, ForwardedPortLocal forward)
    {
        _ctx = ctx;
        _forward = forward;
    }

    public int LocalPort => (int)_forward.BoundPort;
    public string Description => $"127.0.0.1:{LocalPort} → {_forward.Host}:{_forward.Port} via {_ctx.Endpoint.Display}";
    public bool IsConnected => _ctx.IsConnected && _forward.IsStarted;

    public static async Task<GatewayTunnel> OpenAsync(SshConnector connector, Session session, Func<Guid, Session?> resolve,
        string targetHost, int targetPort, bool useAgent, CancellationToken ct)
    {
        if (session.Ssh.JumpHosts.Count == 0) throw new InvalidOperationException("This session has no SSH gateway (jump host) configured.");
        var chain = SshEndpoint.BuildChain(session, resolve, useAgent);
        chain.RemoveAt(chain.Count - 1); // the session itself is not an SSH host; its jump hosts are the gateway chain
        var ctx = await connector.ConnectAsync(chain, session.Ssh.Proxy, $"session:{session.Id}", ct).ConfigureAwait(false);
        try
        {
            var fwd = new ForwardedPortLocal("127.0.0.1", 0, targetHost, (uint)targetPort);
            ctx.Client.AddForwardedPort(fwd);
            fwd.Start();
            return new GatewayTunnel(ctx, fwd);
        }
        catch
        {
            ctx.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        try { _forward.Stop(); } catch (Exception) { }
        _ctx.Dispose();
    }
}
