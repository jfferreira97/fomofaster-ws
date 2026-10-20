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

// One alert while it is being sent (and for a while after): how many of its paying / free recipients Telegram has
// accepted so far. This is what the dashboard's Queue tab draws. Updated from the fan-out's parallel sends, hence the lock.
public class LiveAlert
{
    private readonly object _gate = new();
    public long Seq { get; init; }
    public DateTime StartedAt { get; init; }
    public string Platform { get; init; } = "";
    public string Type { get; init; } = "";
    public string? Trader { get; init; }
    public string? Ticker { get; init; }
    public string? Text { get; init; }
    public double? UsdAmount { get; init; }
    public int PaidTotal { get; init; }
    public int FreeTotal { get; init; }
    public long PublishedMs { get; init; }
    public int PaidSent { get; private set; }
    public int FreeSent { get; private set; }
    public int PaidFailed { get; private set; }
    public int FreeFailed { get; private set; }
    public DateTime? PaidDoneAt { get; private set; }
    public DateTime? DoneAt { get; private set; }

    public void Finish(bool paid, bool ok)
    {
        lock (_gate)
        {
            if (paid) { if (ok) PaidSent++; else PaidFailed++; } else { if (ok) FreeSent++; else FreeFailed++; }
            var now = DateTime.UtcNow;
            if (PaidDoneAt == null && PaidSent + PaidFailed >= PaidTotal) PaidDoneAt = now;
            if (DoneAt == null && PaidSent + PaidFailed + FreeSent + FreeFailed >= PaidTotal + FreeTotal) DoneAt = now;
        }
    }

    // A consistent copy for the dashboard.
    public LiveAlert Snapshot()
    {
        lock (_gate) return (LiveAlert)MemberwiseClone();
    }
}

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

    // ---- live fan-outs (the dashboard's Queue tab) ----
    private readonly LinkedList<LiveAlert> _live = new();
    private long _liveSeq;
    private const int MaxLive = 60;

    public LiveAlert BeginLive(string platform, string type, string? trader, string? ticker, string? text, double? usdAmount,
        int paidTotal, int freeTotal, long publishedMs)
    {
        var a = new LiveAlert
        {
            Seq = Interlocked.Increment(ref _liveSeq), StartedAt = DateTime.UtcNow, Platform = platform, Type = type, Trader = trader,
            Ticker = ticker, Text = text, UsdAmount = usdAmount, PaidTotal = paidTotal, FreeTotal = freeTotal, PublishedMs = publishedMs,
        };
        lock (_lock)
        {
            _live.AddFirst(a);
            while (_live.Count > MaxLive) _live.RemoveLast();
        }
        return a;
    }

    public List<LiveAlert> Live()
    {
        lock (_lock) return _live.Select(a => a.Snapshot()).ToList();
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
