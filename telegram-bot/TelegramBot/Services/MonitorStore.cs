using System.Collections.Concurrent;

namespace TelegramBot.Services;

public record AlertRecord(DateTime At, string Platform, string Type, string? Trader, string? Ticker, int Recipients, int Failed, int FanOutMs);

public record ProbeRecord(DateTime At, int Status, int Ms, string? Error);

// In-memory state behind the dashboard's Monitoring tab: what the sidecars last said, how recent alerts went out
// and how the website probes did. It resets with the process; nothing here is persisted or needed to run alerts.
public class MonitorStore
{
    public DateTime StartedAt { get; } = DateTime.UtcNow;

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
