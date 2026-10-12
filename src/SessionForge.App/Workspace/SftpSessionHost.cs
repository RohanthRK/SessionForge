using System.Windows;
using System.Windows.Controls;
using SessionForge.App.Controls;
using SessionForge.App.Services;
using SessionForge.Core.Models;
using SessionForge.Protocols.Ssh;

namespace SessionForge.App.Workspace;

/// <summary>Standalone SFTP session (file browser only, no shell).</summary>
public sealed class SftpSessionHost : SessionHost
{
    private readonly Grid _root = new();
    private readonly SftpBrowser _browser = new();
    private readonly TextBlock _status = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(30) };
    private SshConnectionContext? _ctx;

    public SftpSessionHost(Session session) : base(session)
    {
        _root.SetResourceReference(Panel.BackgroundProperty, "Bg.Panel");
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Fg.Secondary");
        _root.Children.Add(_browser);
        _root.Children.Add(_status);
        _browser.Notice += m => Notice?.Invoke(m);
    }

    public event Action<string>? Notice;
    public override FrameworkElement View => _root;
    public override string StatusText => $"SFTP  {Session.Describe()}{(_browser.IsConnected ? "   •   " + _browser.CurrentPath : string.Empty)}";

    public override async Task ConnectAsync()
    {
        State = HostState.Connecting;
        _status.Text = $"Connecting to {Session.Host}…";
        _status.Visibility = Visibility.Visible;
        await DisposeAsync();
        try
        {
            var connector = AppServices.CreateConnector();
            connector.Progress += m => _root.Dispatcher.BeginInvoke(() => _status.Text = m);
            var chain = SshEndpoint.BuildChain(Session, AppServices.ResolveSession, AppServices.Settings.UseWindowsSshAgent);
            _ctx = await Task.Run(() => connector.ConnectAsync(chain, Session.Ssh.Proxy, $"session:{Session.Id}", CancellationToken.None));
            _ctx.ConnectionLost += reason => _root.Dispatcher.BeginInvoke(() =>
            {
                State = HostState.Failed;
                _status.Text = "Connection lost: " + reason;
                _status.Visibility = Visibility.Visible;
            });
            await _browser.ConnectAsync(_ctx, Session.Ssh.StartupDirectory);
            _status.Visibility = Visibility.Collapsed;
            State = HostState.Connected;
            AppServices.Sessions.MarkConnected(Session.Id);
            RaiseStatusChanged();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            State = HostState.Failed;
            _status.Text = "Could not connect: " + ex.Message;
            _status.Visibility = Visibility.Visible;
        }
    }

    public override async ValueTask DisposeAsync()
    {
        await _browser.DisposeAsync();
        _ctx?.Dispose();
        _ctx = null;
    }
}
