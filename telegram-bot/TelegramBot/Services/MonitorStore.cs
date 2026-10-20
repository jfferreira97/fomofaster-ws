using System.Collections.Concurrent;

namespace TelegramBot.Services;

// One fan-out, with a millisecond stamp for every stage (all taken on THIS machine's clock, so the gaps between them
// are exact whatever the clock's offset from real time is). Only SourceCreatedMs comes from pump.fun / FOMO.
public record AlertRecord(
    DateTime At, string Platform, string Type, string? Trader, string? Ticker, int Recipients, int Failed, int FanOutMs,
    string? Via = null,               // "stream" | "rest" (pump trades) — how the sidecar learned about it
    long? SourceCreatedMs = null,     // when the source says it happened (whole seconds, source clock)
    long? SidecarRecvMs = null,       // sidecar received it
    long ArrivedMs = 0,               // backend received the sidecar's request
    long PublishedMs = 0,             // the web feed got it
    long? FirstAckMs = null,          // first Telegram message accepted by Telegram
    long? PaidLastAckMs = null,       // last message to a paying / trial / RN4L user accepted
    long? LastAckMs = null);          // last message to anybody accepted

// What a sidecar knew about an alert before the backend saw it (passed through so the stages line up).
public record AlertTrace(string? Via, long? SourceCreatedMs, long? SidecarRecvMs, long ArrivedMs);

public record ProbeRecord(DateTime At, int Status, int Ms, string? Error);

// In-memory state behind the dashboard's Monitoring tab: what the sidecars last said, how recent alerts went out
// and how the website probes did. It resets with the process; nothing here is persisted or needed to run alerts.
public class MonitorStore
{
    public DateTime StartedAt { get; } = DateTime.UtcNow;

    // How far this PC's clock is from real time, measured by NTP (positive = this PC is BEHIND). Used only to put
    // the source's timestamps on the same clock for the speed numbers; alerts themselves never depend on it.
    public double ClockOffsetMs { get; set; }
    public DateTime? ClockCheckedAt { get; set; }

    private readonly ConcurrentDictionary<string, (DateTime Last, long Count)> _beats = new();
    private readonly object _lock = new();
    private readonly LinkedList<AlertRecord> _alerts = new();
    private readonly Dictionary<string, LinkedList<ProbeRecord>> _probes = new();

    private const int MaxAlerts = 20_000;
    private const int MaxProbesPerName = 60;

    public void Beat(string source) =>
        _beats.AddOrUpdate(source, _ => (DateTime.UtcNow, 1), (_, old) => (DateTime.UtcNow, old.Count + 1));

    public (DateTime Last, long Count)? GetBeat(string source) =>
        _beats.TryGetValue(source, out var b) ? b : null;

    public void RecordAlert(AlertRecord r)
    {
        lock (_lock)
        {
            _alerts.AddLast(r);
            var cutoff = DateTime.UtcNow.AddHours(-24);
            while (_alerts.Count > MaxAlerts || (_alerts.First != null && _alerts.First.Value.At < cutoff))
                _alerts.RemoveFirst();
        }
    }

    public List<AlertRecord> Alerts(TimeSpan window)
    {
        var since = DateTime.UtcNow - window;
        lock (_lock) return _alerts.Where(a => a.At >= since).ToList();
    }

    public void RecordProbe(string name, ProbeRecord p)
    {
        lock (_lock)
        {
            if (!_probes.TryGetValue(name, out var list)) _probes[name] = list = new LinkedList<ProbeRecord>();
            list.AddLast(p);
            while (list.Count > MaxProbesPerName) list.RemoveFirst();
        }
    }

    public List<ProbeRecord> Probes(string name)
    {
        lock (_lock) return _probes.TryGetValue(name, out var list) ? list.ToList() : new List<ProbeRecord>();
    }
}
