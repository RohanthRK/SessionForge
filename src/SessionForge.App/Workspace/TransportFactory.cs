using SessionForge.App.Services;
using SessionForge.Core.Models;
using SessionForge.Protocols;
using SessionForge.Protocols.Local;
using SessionForge.Protocols.Serial;
using SessionForge.Protocols.Ssh;
using SessionForge.Protocols.Telnet;

namespace SessionForge.App.Workspace;

public static class TransportFactory
{
    public static ITerminalTransport Create(Session s) => s.Kind switch
    {
        SessionKind.Ssh => new SshTerminalTransport(AppServices.CreateConnector(), s, AppServices.ResolveSession, AppServices.Settings.UseWindowsSshAgent),
        SessionKind.Telnet => new TcpTerminalTransport(s, telnet: true),
        SessionKind.Raw => new TcpTerminalTransport(s, telnet: false),
        SessionKind.Serial => new SerialTerminalTransport(s.Serial),
        SessionKind.LocalShell => new ConPtyTransport(s.Shell),
        _ => throw new NotSupportedException($"{s.Kind} is not a terminal session type."),
    };
}
