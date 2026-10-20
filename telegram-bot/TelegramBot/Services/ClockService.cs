using System.Net.Sockets;

namespace TelegramBot.Services;

// Asks public NTP servers how far this PC's clock is from real time, every 10 minutes. Windows' own time service
// was found stopped (the clock had drifted 1.4 s), and the speed numbers on the Monitoring tab compare this PC's clock
// with timestamps made by pump.fun, so they must be corrected. Read-only: it never sets the system clock.
public class ClockService : BackgroundService
{
    private static readonly string[] Servers = { "time.cloudflare.com", "time.google.com", "pool.ntp.org" };
    private static readonly DateTime NtpEpoch = new(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly MonitorStore _store;
    private readonly ILogger<ClockService> _logger;

    public ClockService(MonitorStore store, ILogger<ClockService> logger)
    {
        _store = store;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(5), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            await MeasureAsync(ct);
            try { await Task.Delay(TimeSpan.FromMinutes(10), ct); } catch (OperationCanceledException) { break; }
        }
    }

    private async Task MeasureAsync(CancellationToken ct)
    {
        var samples = new List<double>();
        foreach (var host in Servers)
        {
            for (var i = 0; i < 2; i++)
            {
                var o = await QueryAsync(host, ct);
                if (o.HasValue) samples.Add(o.Value);
            }
            if (samples.Count >= 3) break;
        }
        if (samples.Count == 0) { _logger.LogWarning("Clock check: no NTP server answered"); return; }
        samples.Sort();
        _store.ClockOffsetMs = samples[samples.Count / 2];
        _store.ClockCheckedAt = DateTime.UtcNow;
        _logger.LogInformation("Clock check: this PC is {Dir} real time by {Ms:0} ms ({N} samples)", _store.ClockOffsetMs >= 0 ? "BEHIND" : "AHEAD of", Math.Abs(_store.ClockOffsetMs), samples.Count);
    }

    // Standard SNTP: offset = ((T2 - T1) + (T3 - T4)) / 2; positive when the server is ahead of this PC.
    private static async Task<double?> QueryAsync(string host, CancellationToken ct)
    {
        try
        {
            using var udp = new UdpClient();
            udp.Connect(host, 123);
            var req = new byte[48];
            req[0] = 0x1B;
            var t1 = DateTime.UtcNow;
            await udp.SendAsync(req, req.Length);
            var recvTask = udp.ReceiveAsync(ct).AsTask();
            if (await Task.WhenAny(recvTask, Task.Delay(3000, ct)) != recvTask) return null;
            var t4 = DateTime.UtcNow;
            var r = recvTask.Result.Buffer;
            if (r.Length < 48) return null;
            var t2 = ToTime(r, 32);
            var t3 = ToTime(r, 40);
            return ((t2 - t1).TotalMilliseconds + (t3 - t4).TotalMilliseconds) / 2.0;
        }
        catch { return null; }
    }

    private static DateTime ToTime(byte[] b, int offset)
    {
        ulong sec = 0, frac = 0;
        for (var i = 0; i < 4; i++) sec = (sec << 8) | b[offset + i];
        for (var i = 4; i < 8; i++) frac = (frac << 8) | b[offset + i];
        return NtpEpoch.AddSeconds(sec + frac / 4294967296.0);
    }
}
