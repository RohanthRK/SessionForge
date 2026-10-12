using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using SessionForge.App.Services;
using SessionForge.Core.Models;
using SessionForge.Protocols;
using SessionForge.Protocols.Ssh;

namespace SessionForge.App.Workspace;

public enum TunnelState { Stopped, Connecting, Running, Reconnecting, Failed }

/// <summary>A standalone SSH tunnel (no terminal): connects, starts its forwards, and reconnects with backoff.</summary>
public sealed partial class TunnelRuntime : ObservableObject
{
    private SshConnectionContext? _ctx;
    private PortForwardManager? _forwards;
    private CancellationTokenSource? _cts;

    public TunnelRuntime(TunnelProfile profile) => Profile = profile;

    public TunnelProfile Profile { get; private set; }
    public string Name => Profile.Name;

    [ObservableProperty] private TunnelState _state;
    [ObservableProperty] private string _detail = "Stopped";

    public Brush StatusBrush => (Brush)Application.Current.FindResource(State switch
    {
        TunnelState.Running => "Success",
        TunnelState.Connecting or TunnelState.Reconnecting => "Warning",
        TunnelState.Failed => "Danger",
        _ => "Fg.Muted",
    });

    partial void OnStateChanged(TunnelState value) => OnPropertyChanged(nameof(StatusBrush));

    public void Update(TunnelProfile profile)
    {
        Profile = profile;
        OnPropertyChanged(nameof(Name));
    }

    public bool IsActive => State is TunnelState.Connecting or TunnelState.Running or TunnelState.Reconnecting;

    public void Start()
    {
        if (IsActive) return;
        _cts = new CancellationTokenSource();
        _ = RunAsync(_cts.Token);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        int attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            var session = AppServices.Sessions.Get(Profile.SessionId);
            if (session == null) { SetState(TunnelState.Failed, "The SSH session used by this tunnel no longer exists."); return; }
            SetState(attempt == 0 ? TunnelState.Connecting : TunnelState.Reconnecting, $"Connecting to {session.Describe()}…");
            var lost = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                var connector = AppServices.CreateConnector();
                var chain = SshEndpoint.BuildChain(session, AppServices.ResolveSession, AppServices.Settings.UseWindowsSshAgent);
                _ctx = await Task.Run(() => connector.ConnectAsync(chain, session.Ssh.Proxy, $"session:{session.Id}", ct), ct);
                _ctx.ConnectionLost += r => lost.TrySetResult(r);
                _forwards = new PortForwardManager(_ctx.Client);
                _forwards.StartAll(Profile.Forwards);
                var states = _forwards.States;
                int ok = states.Count(s => s.Status == ForwardStatus.Running);
                SetState(ok > 0 ? TunnelState.Running : TunnelState.Failed,
                    ok == states.Count ? string.Join(", ", states.Select(s => s.Definition.ToString())) : string.Join("; ", states.Where(s => s.Status != ForwardStatus.Running).Select(s => $"{s.Definition}: {s.Error}")));
                attempt = 0;
                using (ct.Register(() => lost.TrySetResult("stopped")))
                {
                    var reason = await lost.Task;
                    if (ct.IsCancellationRequested) break;
                    SetState(TunnelState.Reconnecting, "Connection lost: " + reason);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (ConnectionCancelledException ex) { Cleanup(); SetState(TunnelState.Failed, ex.Message); return; }
            catch (Exception ex)
            {
                SetState(TunnelState.Failed, ex.Message);
                Log.Warn($"Tunnel '{Profile.Name}' failed: {ex.Message}");
            }
            Cleanup();
            if (!Profile.AutoReconnect || ct.IsCancellationRequested) break;
            attempt++;
            var delay = TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, Math.Min(attempt, 6))));
            SetState(TunnelState.Reconnecting, $"Retrying in {delay.TotalSeconds:0} s…");
            try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { break; }
        }
        Cleanup();
        if (State != TunnelState.Failed) SetState(TunnelState.Stopped, "Stopped");
    }

    private void SetState(TunnelState s, string detail)
    {
        Application.Current.Dispatcher.BeginInvoke(() => { State = s; Detail = detail; });
    }

    private void Cleanup()
    {
        try { _forwards?.Dispose(); } catch (Exception) { }
        _forwards = null;
        _ctx?.Dispose();
        _ctx = null;
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        Cleanup();
        SetState(TunnelState.Stopped, "Stopped");
    }
}
