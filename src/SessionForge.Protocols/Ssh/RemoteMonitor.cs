using System.Globalization;

namespace SessionForge.Protocols.Ssh;

public sealed record RemoteStats(
    double? CpuPercent,
    long? MemTotalKb,
    long? MemAvailableKb,
    int? DiskUsePercent,
    string? DiskUsed,
    string? DiskSize,
    double? Load1,
    TimeSpan? Uptime,
    int? Users)
{
    public string Format()
    {
        var parts = new List<string>();
        if (CpuPercent is { } c) parts.Add($"CPU {c:0}%");
        if (MemTotalKb is { } t && MemAvailableKb is { } a && t > 0)
            parts.Add($"RAM {(t - a) / 1048576.0:0.0}/{t / 1048576.0:0.0} GB");
        if (DiskUsePercent is { } d) parts.Add($"/ {d}%{(DiskSize != null ? $" of {DiskSize}" : string.Empty)}");
        if (Load1 is { } l) parts.Add($"load {l.ToString("0.00", CultureInfo.InvariantCulture)}");
        if (Uptime is { } u) parts.Add($"up {(u.TotalDays >= 1 ? $"{(int)u.TotalDays}d {u.Hours}h" : $"{u.Hours}h {u.Minutes}m")}");
        if (Users is { } n) parts.Add($"{n} user{(n == 1 ? "" : "s")}");
        return string.Join("   •   ", parts);
    }
}

/// <summary>
/// Polls Linux hosts for load/CPU/memory/disk/uptime over an exec channel (no agent required).
/// Parsing is separated from transport so it can be unit tested.
/// </summary>
public sealed class RemoteMonitor : IAsyncDisposable
{
    public const string Command =
        "LC_ALL=C; cat /proc/loadavg; head -1 /proc/stat; grep -E '^(MemTotal|MemAvailable):' /proc/meminfo; df -Ph / | tail -1; cat /proc/uptime; who | wc -l";

    private readonly SshConnectionContext _ctx;
    private readonly TimeSpan _interval;
    private CancellationTokenSource? _cts;
    private long[]? _prevCpu;

    public RemoteMonitor(SshConnectionContext ctx, TimeSpan interval)
    {
        _ctx = ctx;
        _interval = interval;
    }

    public event Action<RemoteStats>? Updated;
    public event Action<string>? Failed;

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => LoopAsync(_cts.Token));
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        int failures = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!_ctx.IsConnected) return;
                var (code, output, _) = await _ctx.RunCommandAsync(Command, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
                var stats = Parse(output, ref _prevCpu);
                if (code != 0 && stats.Load1 == null)
                {
                    Failed?.Invoke("Remote monitoring needs a Linux host with /proc.");
                    return;
                }
                failures = 0;
                Updated?.Invoke(stats);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                if (++failures >= 3) { Failed?.Invoke("Remote monitoring stopped: " + ex.Message); return; }
            }
            try { await Task.Delay(_interval, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Parses the output of <see cref="Command"/>. CPU usage needs two samples (previous totals in <paramref name="prevCpu"/>).</summary>
    public static RemoteStats Parse(string output, ref long[]? prevCpu)
    {
        double? load = null, cpu = null;
        long? memTotal = null, memAvail = null;
        int? diskPct = null, users = null;
        string? diskUsed = null, diskSize = null;
        TimeSpan? uptime = null;
        var lines = output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var f = line.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (f.Length == 0) continue;
            if (f[0] == "cpu" && f.Length >= 5)
            {
                var vals = f.Skip(1).Select(v => long.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var x) ? x : 0).ToArray();
                if (prevCpu != null && prevCpu.Length == vals.Length)
                {
                    long total = vals.Sum() - prevCpu.Sum();
                    long idle = (vals[3] + (vals.Length > 4 ? vals[4] : 0)) - (prevCpu[3] + (prevCpu.Length > 4 ? prevCpu[4] : 0));
                    if (total > 0) cpu = Math.Clamp(100.0 * (total - idle) / total, 0, 100);
                }
                prevCpu = vals;
            }
            else if (f[0] == "MemTotal:" && f.Length >= 2 && long.TryParse(f[1], out var mt)) memTotal = mt;
            else if (f[0] == "MemAvailable:" && f.Length >= 2 && long.TryParse(f[1], out var ma)) memAvail = ma;
            else if (f.Length == 5 && f[3].Contains('/') && double.TryParse(f[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var l1)) load = l1; // loadavg
            else if (f.Length >= 6 && f[^2].EndsWith('%') && f[^1] == "/")
            {
                if (int.TryParse(f[^2].TrimEnd('%'), out var pct)) diskPct = pct;
                diskSize = f[1];
                diskUsed = f[2];
            }
            else if (f.Length == 2 && double.TryParse(f[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var up) && double.TryParse(f[1], NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                uptime = TimeSpan.FromSeconds(up);
            else if (f.Length == 1 && int.TryParse(f[0], out var u)) users = u;
        }
        return new RemoteStats(cpu, memTotal, memAvail, diskPct, diskUsed, diskSize, load, uptime, users);
    }

    public ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        return ValueTask.CompletedTask;
    }
}
