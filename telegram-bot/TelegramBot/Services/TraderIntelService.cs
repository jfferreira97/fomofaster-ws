using System.Text.Json;
using System.Text.Json.Serialization;
using TelegramBot.Models;

namespace TelegramBot.Services;

// Serves per-trader intel for the manage page: the live rating from the DB (category and
// follower-outcome stats, kept fresh by TraderCategorizerService) laid over the offline public
// profile data in Intel/trader-intel.json (PnL, followers, links, avatar). The file is
// regenerated out of band, so it is re-read whenever its timestamp changes — dropping in a
// new file needs no restart. A missing or malformed file just means no profile data.
public class TraderIntelService
{
    private const string RelativePath = "Intel/trader-intel.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _path;
    private readonly ILogger<TraderIntelService> _logger;
    private readonly object _lock = new();
    private DateTime _loadedStamp = DateTime.MinValue;
    private TraderIntelFile _file = new();
    private Dictionary<(Platform, string), TraderIntelEntry> _byHandle = new();

    public TraderIntelService(IWebHostEnvironment env, ILogger<TraderIntelService> logger)
    {
        _path = Path.Combine(env.ContentRootPath, RelativePath);
        _logger = logger;
    }

    public TraderIntelFile Current
    {
        get { Refresh(); return _file; }
    }

    public TraderIntelEntry? Get(Platform platform, string handle)
    {
        Refresh();
        return _byHandle.GetValueOrDefault((platform, handle.ToLowerInvariant()));
    }

    // One trader's intel as /manage shows it. Category is the trader's effective one (an admin
    // override wins over the rating); everything computed comes from the rating when there is one.
    public TraderIntelEntry Compose(Trader trader, TraderRating? rating, bool withCalls)
    {
        var profile = Get(trader.Platform, trader.Handle);
        var e = profile?.WithoutCalls() ?? new TraderIntelEntry { Handle = trader.Handle, Platform = trader.Platform };
        e.Style = TraderCategories.Effective(trader.Category ?? rating?.AutoCategory);

        // PnL: pump.fun's own numbers for pump traders (refreshed daily); for FOMO traders only our
        // count of their realized profit since the bot added them.
        static double? Sane(double? usd) => usd is double v && Math.Abs(v) < 1e9 ? v : null;
        if (trader.Platform == Platform.Pump)
        {
            e.Pnl30dUsd = Sane(e.Pnl30dUsd);
            e.Pnl7dUsd = Sane(e.Pnl7dUsd);
            e.PortfolioPnlUsd = Sane(e.PortfolioPnlUsd);
            if (trader.PnlUpdatedAt != null)
            {
                e.Pnl30dUsd = trader.Pnl30dUsd;
                e.Pnl7dUsd = trader.Pnl7dUsd;
                e.PnlRank30d = trader.PnlRank30d;
            }
            if (trader.PortfolioPnlUsd != null) e.PortfolioPnlUsd = trader.PortfolioPnlUsd;
            e.RealizedPnlUsd = null;
        }
        else
        {
            e.Pnl30dUsd = e.Pnl7dUsd = null;
            e.PnlRank30d = null;
            e.PortfolioPnlUsd = null;
            e.RealizedPnlUsd = trader.OwnRealizedUsd;
            e.OwnClosedPositions = trader.OwnClosedPositions;
            e.PnlSince = DateTime.SpecifyKind(trader.OwnPnlSince ?? trader.FirstSeenAt, DateTimeKind.Utc);
        }

        if (rating == null)
        {
            if (withCalls) e.RecentCalls = profile?.RecentCalls;
            return e;
        }

        var s = TraderCategorizerService.ParseStats(rating.StatsJson);
        e.Summary = s.Summary;
        e.Score = rating.Score;
        e.Cut = rating.Cut;
        e.Reasons = s.Reasons.Count > 0 ? s.Reasons : null;
        e.Basis = rating.Basis;
        e.RatedAt = DateTime.SpecifyKind(rating.ComputedAt, DateTimeKind.Utc);
        e.Calls = rating.Calls;
        e.AlertsPerDay = rating.AlertsPerDay;
        e.MedianMcap = s.MedianMcap;
        e.CapTier = s.CapTier;
        e.Speed = s.Speed;
        e.Hit50In15m = s.Hit50In15m;
        e.Hit2xIn24h = s.Hit2xIn24h;
        e.StopOut30 = s.StopOut30;
        e.MedianMinutesToPeak = s.MedianMinutesToPeak;
        e.AvgPerCall = s.AvgPerCall;
        e.ScoreRank = s.ScoreRank;
        e.Hit2xRank = s.Hit2xRank;
        e.Return1hRank = s.Return1hRank;
        e.StopOutRank = s.StopOutRank;
        e.AvgRank = s.AvgRank;
        e.MedianReturn1h = s.MedianReturn1h;
        e.Simulated = rating.Simulated;
        e.BestWindow = s.BestWindow;
        e.PlayEv = s.PlayEv;
        e.PlayCalls = s.PlayCalls;
        e.OwnHoldMinutes = s.OwnHoldMinutes;
        e.PnlRank = s.PnlRank;
        if (withCalls) e.RecentCalls = s.RecentCalls;
        return e;
    }

    private void Refresh()
    {
        var stamp = File.Exists(_path) ? File.GetLastWriteTimeUtc(_path) : DateTime.MinValue;
        if (stamp == _loadedStamp)
            return;

        lock (_lock)
        {
            if (stamp == _loadedStamp)
                return;

            try
            {
                var file = stamp == DateTime.MinValue
                    ? new TraderIntelFile()
                    : JsonSerializer.Deserialize<TraderIntelFile>(File.ReadAllText(_path), JsonOptions) ?? new TraderIntelFile();

                _byHandle = file.Traders
                    .GroupBy(t => (t.Platform, t.Handle.ToLowerInvariant()))
                    .ToDictionary(g => g.Key, g => g.First());
                _file = file;
                _logger.LogInformation("Loaded trader intel: {Traders} traders (generated {GeneratedAt:u})",
                    file.Traders.Count, file.GeneratedAt);
            }
            catch (Exception ex)
            {
                // Keep serving the last good copy; a half-written file shouldn't blank the page.
                _logger.LogError(ex, "Failed to load trader intel from {Path}", _path);
            }

            _loadedStamp = stamp;
        }
    }
}
