using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TelegramBot.Data;
using TelegramBot.Models;

namespace TelegramBot.Services;

// Re-categorizes every tracked trader on a schedule (TraderCategorizerIntervalHours, default
// daily) from the last 30 days of their posts, replayed as a follower would have traded them:
//   1. collect  - each trader's first thesis / first buy (FOMO) and first callout (pump.fun) per
//                 token becomes a pending CallOutcome
//   2. replay   - once a post is 24h old, fetch its 1m candles and store the follower result
//                 (done once per post, so after the first run only a day's worth is fetched)
//   3. rate     - TraderRatingCalculator over the window; pump traders are rated on callouts,
//                 FOMO traders on theses when they have enough, otherwise on first buys
// Traders an admin moved by hand (Trader.IsManuallyRecategorized) keep their category; only
// their TraderRating (what the numbers say) is refreshed.
public class TraderCategorizerService : BackgroundService
{
    private const int WindowDays = 30;
    private const int Parallelism = 24; // pump.fun 503s a lot; most of a fetch is waiting on retries
    private const int MaxAttempts = 3;
    private const string LastRunKey = "TraderCategorizerLastRunAt";
    private static readonly TimeSpan Horizon = TimeSpan.FromHours(24) + TimeSpan.FromMinutes(10);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IServiceProvider _services;
    private readonly CandleClient _candles;
    private readonly PumpProfileClient _pump;
    private readonly TraderIntelService _intel;
    private readonly AppConfigService _config;
    private readonly ILogger<TraderCategorizerService> _logger;
    private readonly SemaphoreSlim _trigger = new(0, 1);
    private readonly object _statusLock = new();
    private CategorizerStatus _status = new(false, "idle", 0, 0, null, null, null);

    public TraderCategorizerService(IServiceProvider services, CandleClient candles, PumpProfileClient pump, TraderIntelService intel,
        AppConfigService config, ILogger<TraderCategorizerService> logger)
    {
        _services = services;
        _candles = candles;
        _pump = pump;
        _intel = intel;
        _config = config;
        _logger = logger;
    }

    public record CategorizerStatus(bool Running, string Phase, int Done, int Total, DateTime? LastRunAt, string? LastResult, DateTime? NextRunAt);

    public CategorizerStatus Status { get { lock (_statusLock) return _status; } }

    // Start a run now (the dashboard's button). False if one is already running or queued.
    public bool TriggerNow()
    {
        if (Status.Running) return false;
        try { _trigger.Release(); return true; }
        catch (SemaphoreFullException) { return false; }
    }

    private void SetStatus(Func<CategorizerStatus, CategorizerStatus> f) { lock (_statusLock) _status = f(_status); }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(30), ct); } catch (OperationCanceledException) { return; }

        try { await SeedFromIntelFileAsync(ct); }
        catch (Exception ex) { _logger.LogError(ex, "Seeding trader categories from the intel file failed"); }

        while (!ct.IsCancellationRequested)
        {
            var hours = double.TryParse(await _config.GetAsync("TraderCategorizerIntervalHours"), NumberStyles.Float, CultureInfo.InvariantCulture, out var h) ? h : 24;
            var lastRun = DateTime.TryParse(await _config.GetAsync(LastRunKey), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var lr) ? lr : (DateTime?)null;
            DateTime? next = hours > 0 ? lastRun?.AddHours(hours) ?? DateTime.UtcNow : null;
            SetStatus(s => s with { LastRunAt = lastRun, NextRunAt = next });

            var wait = next is DateTime n ? n - DateTime.UtcNow : TimeSpan.FromHours(1); // interval 0: only runs when triggered; re-read the setting hourly
            if (wait > TimeSpan.Zero)
            {
                bool triggered;
                try { triggered = await _trigger.WaitAsync(wait < TimeSpan.FromHours(1) ? wait : TimeSpan.FromHours(1), ct); }
                catch (OperationCanceledException) { return; }
                if (!triggered && (next == null || DateTime.UtcNow < next)) continue;
            }

            SetStatus(s => s with { Running = true, Phase = "starting", Done = 0, Total = 0 });
            string result;
            try
            {
                result = await RunAsync(ct);
                await _config.SetAsync(LastRunKey, DateTime.UtcNow.ToString("o"), "Set by the trader categorizer after each run");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Trader categorizer run failed");
                result = "failed: " + ex.Message;
                // don't hammer a failing run every loop: count it as a run
                await _config.SetAsync(LastRunKey, DateTime.UtcNow.ToString("o"), "Set by the trader categorizer after each run");
            }
            SetStatus(s => s with { Running = false, Phase = "idle", LastResult = result });
        }
    }

    public async Task<string> RunAsync(CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        var since = started.AddDays(-(WindowDays + 1));
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Database.SetCommandTimeout(300);

        SetStatus(s => s with { Phase = "collecting posts" });
        var traders = await db.Traders.AsNoTracking().ToListAsync(ct);
        var traderIds = traders.GroupBy(t => (t.Platform, t.Handle.ToLowerInvariant())).ToDictionary(g => g.Key, g => g.First().Id);
        var feed = await LoadFeedAsync(db, since, ct);
        var added = await CollectAsync(db, feed, traderIds, ct);

        // PnL first: it's quick and shows on /manage straight away, while the replay can take a while
        var own = await RebuildOwnTradingAsync(db, traders, started, ct);
        await RefreshPumpPnlAsync(db, ct);
        traders = await db.Traders.AsNoTracking().ToListAsync(ct);

        // Callouts and theses first: they're the calls. Then buys, but only where they matter:
        // traders without enough theses are rated on first buys, and for everyone else only the
        // buys they dumped within 10 minutes are needed (the dumps-on-followers check). The rest
        // stay pending and cost nothing unless the trader later drops below enough theses.
        var (replayed, noData) = await ReplayAsync(db, feed, started, "replaying callouts & theses", c => c.Kind != "buy", ct);
        var thesisRated = await ThesisRatedTradersAsync(db, started, ct);
        var (replayedBuys, noDataBuys) = await ReplayAsync(db, feed, started, "replaying buys",
            c => c.Kind == "buy" && (!thesisRated.Contains(c.TraderId) || c.SoldIn10mFrac >= 0.5), ct);
        replayed += replayedBuys;
        noData += noDataBuys;

        SetStatus(s => s with { Phase = "rating", Done = 0, Total = 0 });
        var (rated, moved) = await RateAsync(db, feed, traders, traderIds, own, started, ct);

        await db.CallOutcomes.Where(c => c.CalledAt < started.AddDays(-60)).ExecuteDeleteAsync(ct);

        var result = $"{added} new posts, {replayed} replayed ({noData} without price data), {rated} traders rated, {moved} changed category, {(DateTime.UtcNow - started).TotalMinutes:0} min";
        _logger.LogInformation("Trader categorizer: {Result}", result);
        return result;
    }

    // ---------- 1. the feed: everything posted in the window, straight from the stored events ----------

    private class FeedRow
    {
        public string? Handle { get; set; }
        public string Type { get; set; } = "";
        public string? TokenAddress { get; set; }
        public int? NetworkId { get; set; }
        public string? Ticker { get; set; }
        public string? At { get; set; }
        public double? Price { get; set; }
        public double? MarketCap { get; set; }
        public double? Usd { get; set; }
    }

    private record Post(Platform Platform, string Handle, string Kind, int Net, string Token, string? Ticker, DateTime At, double? Price, double? Mcap, double? Usd);

    private class Feed
    {
        public List<Post> Posts = new();   // theses, buys, sells, callouts; oldest first
        public Dictionary<(int, string), List<(long T, double P)>> Tape = new();
        public Dictionary<(int, string), double> Supply = new();
    }

    private static async Task<Feed> LoadFeedAsync(AppDbContext db, DateTime since, CancellationToken ct)
    {
        // Raw JSON createdAt, not the CreatedAt column: rows before 2026-09-16 were stored with a
        // local-time offset; the feed's own timestamp is always UTC.
        var fomo = await db.Database.SqlQuery<FeedRow>($@"
            select UserHandle as Handle, Type, TokenAddress, NetworkId, Ticker,
                   coalesce(json_extract(RawJson,'$.createdAt'), CreatedAt) as At,
                   case when Type = 'thesis' then cast(json_extract(RawJson,'$.comment.priceUsdAtCreation') as real) else cast(Price as real) end as Price,
                   case when Type = 'thesis' then cast(json_extract(RawJson,'$.comment.marketCapAtCreation') as real) else cast(MarketCap as real) end as MarketCap,
                   cast(UsdAmount as real) as Usd
            from WsEvents
            where Type in ('thesis','swap_buy','swap_sell') and ReceivedAt >= {since}").ToListAsync(ct);
        var pump = await db.Database.SqlQuery<FeedRow>($@"
            select ActorHandle as Handle, 'callout' as Type, CoinMint as TokenAddress, ChainId as NetworkId, Symbol as Ticker,
                   coalesce(json_extract(RawJson,'$.createdAt'), CreatedAt) as At,
                   cast(json_extract(RawJson,'$.callout.calloutPrice') as real) as Price,
                   cast(json_extract(RawJson,'$.callout.calledOutAtMcap') as real) as MarketCap,
                   null as Usd
            from PumpEvents
            where Kind = 'callout' and json_extract(RawJson,'$.callout.quotedCalloutId') is null and ReceivedAt >= {since}").ToListAsync(ct);

        var feed = new Feed();
        foreach (var r in fomo.Concat(pump))
        {
            if (string.IsNullOrEmpty(r.Handle) || string.IsNullOrEmpty(r.TokenAddress) || r.NetworkId is not int net) continue;
            if (!DateTime.TryParse(r.At, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at) || at < since) continue;
            var kind = r.Type switch { "swap_buy" => "buy", "swap_sell" => "sell", _ => r.Type };
            var platform = kind == "callout" ? Platform.Pump : Platform.Fomo;
            feed.Posts.Add(new Post(platform, r.Handle, kind, net, r.TokenAddress, r.Ticker, at, r.Price > 0 ? r.Price : null, r.MarketCap > 0 ? r.MarketCap : null, r.Usd));
        }
        feed.Posts.Sort((a, b) => a.At.CompareTo(b.At));

        var ratios = new Dictionary<(int, string), List<double>>();
        foreach (var p in feed.Posts)
        {
            if (p.Price is not double price || p.Kind == "thesis") continue;
            var k = (p.Net, p.Token);
            if (!feed.Tape.TryGetValue(k, out var tape)) feed.Tape[k] = tape = new();
            tape.Add((new DateTimeOffset(p.At).ToUnixTimeMilliseconds(), price));
            // mcap / price = supply, so a thesis (which carries no mcap) gets one from its entry price
            if (p.Mcap is double m)
            {
                if (!ratios.TryGetValue(k, out var list)) ratios[k] = list = new();
                list.Add(m / price);
            }
        }
        foreach (var (k, v) in ratios) { v.Sort(); feed.Supply[k] = v[v.Count / 2]; }
        return feed;
    }

    // ---------- 2. collect new first-posts ----------

    private async Task<int> CollectAsync(AppDbContext db, Feed feed, Dictionary<(Platform, string), int> traderIds, CancellationToken ct)
    {
        var existing = (await db.CallOutcomes.AsNoTracking()
                .Select(c => new { c.TraderId, c.Kind, c.NetworkId, c.TokenAddress }).ToListAsync(ct))
            .Select(c => (c.TraderId, c.Kind, c.NetworkId, c.TokenAddress)).ToHashSet();

        // sells per trader+token, for "how much of the bag went within 10 minutes"
        var sells = feed.Posts.Where(p => p.Kind == "sell").ToLookup(p => (p.Handle.ToLowerInvariant(), p.Net, p.Token));

        var fresh = new List<CallOutcome>();
        foreach (var p in feed.Posts)
        {
            if (p.Kind == "sell") continue;
            if (!traderIds.TryGetValue((p.Platform, p.Handle.ToLowerInvariant()), out var traderId)) continue;
            var key = (traderId, p.Kind, p.Net, p.Token);
            if (!existing.Add(key)) continue;

            double? soldFrac = null;
            if (p.Kind == "buy" && p.Usd > 0 && p.Price is double bp)
            {
                var qty = p.Usd.Value / bp;
                var sold = sells[(p.Handle.ToLowerInvariant(), p.Net, p.Token)]
                    .Where(s => s.At >= p.At && s.At <= p.At.AddMinutes(10) && s.Price > 0 && s.Usd > 0)
                    .Sum(s => s.Usd!.Value / s.Price!.Value);
                soldFrac = Math.Min(1, sold / qty);
            }

            fresh.Add(new CallOutcome
            {
                TraderId = traderId, Kind = p.Kind, NetworkId = p.Net, TokenAddress = p.Token,
                Ticker = p.Ticker?.Length > 50 ? p.Ticker[..50] : p.Ticker,
                CalledAt = p.At, CallPrice = p.Price, McapAtCall = p.Mcap, SoldIn10mFrac = soldFrac,
                Status = CallOutcomeStatus.Pending
            });
        }

        foreach (var chunk in fresh.Chunk(2000))
        {
            db.CallOutcomes.AddRange(chunk);
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }
        return fresh.Count;
    }

    // ---------- 3. replay pending posts whose 24h is complete ----------

    // FOMO traders with enough replayed theses in the rating window to be rated on them.
    private static async Task<HashSet<int>> ThesisRatedTradersAsync(AppDbContext db, DateTime now, CancellationToken ct)
    {
        var end = now - Horizon;
        var start = end.AddDays(-WindowDays);
        return (await db.CallOutcomes
            .Where(c => c.Kind == "thesis" && c.Status == CallOutcomeStatus.Ok && c.CalledAt >= start && c.CalledAt < end)
            .GroupBy(c => c.TraderId)
            .Where(g => g.Count() >= TraderRatingCalculator.MinCalls)
            .Select(g => g.Key)
            .ToListAsync(ct)).ToHashSet();
    }

    private async Task<(int Replayed, int NoData)> ReplayAsync(AppDbContext db, Feed feed, DateTime now, string phase,
        Func<CallOutcome, bool> include, CancellationToken ct)
    {
        var cutoff = now - Horizon;
        var windowStart = cutoff.AddDays(-WindowDays);
        // pending posts, plus replays in the rating window stored before the 15-minute exit existed
        var pending = (await db.CallOutcomes
                .Where(c => (c.Status == CallOutcomeStatus.Pending && c.CalledAt <= cutoff)
                         || (c.Status == CallOutcomeStatus.Ok && c.EvQuick == null && c.CalledAt >= windowStart && c.CalledAt <= cutoff))
                .ToListAsync(ct))
            .Where(include).ToList();

        // one candle fetch per token per day: posts within 24h of the group's first share it
        var groups = new List<List<CallOutcome>>();
        foreach (var byToken in pending.GroupBy(c => (c.NetworkId, c.TokenAddress)))
        {
            List<CallOutcome>? g = null;
            foreach (var c in byToken.OrderBy(c => c.CalledAt))
            {
                if (g == null || c.CalledAt - g[0].CalledAt > TimeSpan.FromHours(24)) groups.Add(g = new());
                g.Add(c);
            }
        }

        SetStatus(s => s with { Phase = phase, Done = 0, Total = pending.Count });
        int replayed = 0, noData = 0, done = 0;

        // Fetches stream through a queue so one slow token never holds up the rest; results are
        // applied and saved here on this thread (the DbContext isn't thread-safe) every few seconds.
        var results = new ConcurrentQueue<(CallOutcome C, ReplayResult? R, string? Source, bool Failed)>();
        var fetching = Parallel.ForEachAsync(groups, new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct }, async (g, token) =>
        {
            var first = g[0];
            var from = new DateTimeOffset(DateTime.SpecifyKind(first.CalledAt, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
            var to = new DateTimeOffset(DateTime.SpecifyKind(g[^1].CalledAt, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
            var raw = await _candles.FetchAsync(first.NetworkId, first.TokenAddress, from - 5 * 60_000, to + 24 * 3_600_000, token);
            var bars = raw == null ? null : FollowerReplay.Clean(raw);
            feed.Tape.TryGetValue((first.NetworkId, first.TokenAddress), out var tape);
            foreach (var c in g)
            {
                var t = new DateTimeOffset(DateTime.SpecifyKind(c.CalledAt, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
                var r = bars is { Count: > 0 } ? FollowerReplay.Simulate(bars, t) : null;
                var source = r != null ? "candles" : null;
                if (r == null && tape != null && FollowerReplay.TapeBars(tape, t) is { } tb)
                {
                    r = FollowerReplay.Simulate(tb, t);
                    if (r != null) source = "tape";
                }
                results.Enqueue((c, r, source, raw == null && r == null));
            }
        });

        async Task DrainAsync()
        {
            var any = false;
            while (results.TryDequeue(out var x))
            {
                any = true;
                var (c, r, source, failed) = x;
                done++;
                if (r == null && c.Status == CallOutcomeStatus.Ok) continue; // a re-run that failed keeps its old replay
                c.Attempts++;
                if (r != null)
                {
                    c.Status = CallOutcomeStatus.Ok;
                    c.Source = source;
                    c.EntryVsCall = c.CallPrice > 0 ? r.EntryPrice / c.CallPrice.Value - 1 : null;
                    c.R1h = r.R1h; c.R24h = r.R24h; c.Pk15m = r.Pk15m; c.Pk1h = r.Pk1h; c.Pk24h = r.Pk24h;
                    c.MinutesToPeak = r.MinutesToPeak; c.Sl30 = r.Sl30;
                    c.EvQuick = r.EvQuick; c.EvScalp = r.EvScalp; c.EvFlip = r.EvFlip; c.EvRunner = r.EvRunner;
                    if (c.McapAtCall == null && feed.Supply.TryGetValue((c.NetworkId, c.TokenAddress), out var supply))
                        c.McapAtCall = supply * r.EntryPrice;
                    c.EvaluatedAt = DateTime.UtcNow;
                    replayed++;
                }
                else if (!failed || c.Attempts >= MaxAttempts)
                {
                    c.Status = CallOutcomeStatus.NoData;
                    c.EvaluatedAt = DateTime.UtcNow;
                    noData++;
                }
                // else: the candle source kept failing; stays pending for the next run
            }
            if (!any) return;
            await db.SaveChangesAsync(ct);
            SetStatus(s => s with { Done = done });
        }

        while (!fetching.IsCompleted)
        {
            await Task.WhenAny(fetching, Task.Delay(TimeSpan.FromSeconds(15), ct));
            await DrainAsync();
        }
        await fetching; // surfaces a fetch-side exception or cancellation
        await DrainAsync();
        db.ChangeTracker.Clear();
        return (replayed, noData);
    }

    // ---------- 4. rate and categorize ----------

    private async Task<(int Rated, int Moved)> RateAsync(AppDbContext db, Feed feed, List<Trader> traders,
        Dictionary<(Platform, string), int> traderIds, Dictionary<int, OwnTrading> own, DateTime now, CancellationToken ct)
    {
        // posts need their full 24h to count, so the window is the 30 days ending yesterday
        var end = now - Horizon;
        var start = end.AddDays(-WindowDays);
        var outcomes = await db.CallOutcomes.AsNoTracking().Where(c => c.CalledAt >= start && c.CalledAt < end).ToListAsync(ct);
        var byTrader = outcomes.ToLookup(c => c.TraderId);

        // alert volume: what a follower of this trader actually receives (FOMO buys + sells, pump callouts)
        var alertCounts = feed.Posts
            .Where(p => p.At >= start && p.At < end && p.Kind is "buy" or "sell" or "callout")
            .Select(p => traderIds.TryGetValue((p.Platform, p.Handle.ToLowerInvariant()), out var id) ? id : 0)
            .Where(id => id != 0)
            .GroupBy(id => id).ToDictionary(g => g.Key, g => g.Count());

        var samples = traders.Select(t =>
        {
            var mine = byTrader[t.Id].ToList();
            var perDay = alertCounts.GetValueOrDefault(t.Id) / (double)WindowDays;
            if (t.Platform == Platform.Pump)
            {
                var callouts = mine.Where(c => c.Kind == "callout").ToList();
                return new TraderSample(t.Id, callouts.Count > 0 ? "callout" : null, callouts, Array.Empty<CallOutcome>(), perDay,
                    Pnl30dUsd: t.Pnl30dUsd);
            }
            var theses = mine.Where(c => c.Kind == "thesis").ToList();
            var buys = mine.Where(c => c.Kind == "buy").ToList();
            // theses are the call; first buys only stand in for traders who rarely write one
            var useTheses = theses.Count(c => c.Status == CallOutcomeStatus.Ok) >= TraderRatingCalculator.MinCalls || buys.Count == 0;
            var basis = useTheses ? theses : buys;
            // FOMO has no public PnL we can read: their own realized profit over the window stands in
            var o = own.GetValueOrDefault(t.Id);
            return new TraderSample(t.Id, basis.Count > 0 ? (useTheses ? "thesis" : "buy") : null, basis, buys, perDay,
                Own: o, Pnl30dUsd: o?.RealizedUsd);
        }).ToList();

        var verdicts = TraderRatingCalculator.Rate(samples);

        var ratings = await db.TraderRatings.ToDictionaryAsync(r => r.TraderId, ct);
        var tracked = await db.Traders.ToDictionaryAsync(t => t.Id, ct);
        var moved = 0;
        foreach (var v in verdicts)
        {
            if (!ratings.TryGetValue(v.TraderId, out var rating))
                db.TraderRatings.Add(rating = new TraderRating { TraderId = v.TraderId });
            rating.AutoCategory = v.Category;
            rating.Basis = v.Basis;
            rating.Score = v.Score;
            rating.Cut = v.Cut;
            rating.Calls = v.Calls;
            rating.Simulated = v.Simulated;
            rating.AlertsPerDay = v.AlertsPerDay;
            rating.StatsJson = JsonSerializer.Serialize(v.Stats, Json);
            rating.ComputedAt = now;

            var trader = tracked[v.TraderId];
            if (!trader.IsManuallyRecategorized && trader.Category != v.Category)
            {
                if (trader.Category != null)
                    _logger.LogInformation("Trader {Handle} ({Platform}): {From} -> {To}", trader.Handle, trader.Platform, trader.Category, v.Category);
                trader.Category = v.Category;
                trader.CategorizedAt = now;
                moved++;
            }
        }
        await db.SaveChangesAsync(ct);
        return (verdicts.Count(v => v.Category != TraderCategories.Unrated), moved);
    }

    // ---------- own trading (FOMO) ----------

    private class Position
    {
        public int TraderId;
        public DateTime OpenedAt;
        public DateTime? ClosedAt;
        public double Qty, Peak, CostUsd, SoldUsd;
    }

    // Rebuilds every FOMO trader's positions from the buys and sells we've captured since they were
    // added: a position opens on a buy and closes once sold down to 5% of its peak size. Saves their
    // realized profit since added on the trader, and returns the last 30 days' stats (Holders).
    private async Task<Dictionary<int, OwnTrading>> RebuildOwnTradingAsync(AppDbContext db, List<Trader> traders, DateTime now, CancellationToken ct)
    {
        SetStatus(s => s with { Phase = "rebuilding FOMO trades", Done = 0, Total = 0 });
        var fomo = traders.Where(t => t.Platform == Platform.Fomo).ToList();
        if (fomo.Count == 0) return new();
        var byHandle = fomo.GroupBy(t => t.Handle.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First());
        // only trades from FomoRealizedPnlStartDate on (or from when the trader was added, if later):
        // full history is too big to rebuild every day
        var floor = DateTime.TryParse(await _config.GetAsync("FomoRealizedPnlStartDate"), CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var f) ? f : new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        DateTime StartFor(Trader t) => t.FirstSeenAt > floor ? t.FirstSeenAt : floor;
        var since = fomo.Min(StartFor);

        var rows = await db.Database.SqlQuery<FeedRow>($@"
            select UserHandle as Handle, Type, TokenAddress, NetworkId, Ticker,
                   coalesce(json_extract(RawJson,'$.createdAt'), CreatedAt) as At,
                   cast(Price as real) as Price, null as MarketCap, cast(UsdAmount as real) as Usd
            from WsEvents
            where Type in ('swap_buy','swap_sell') and ReceivedAt >= {since}").ToListAsync(ct);

        var trades = new List<(Trader T, bool Buy, string Key, DateTime At, double Price, double Usd)>();
        foreach (var r in rows)
        {
            if (r.Handle == null || r.TokenAddress == null || !(r.Price > 0) || !(r.Usd > 0)) continue;
            if (!byHandle.TryGetValue(r.Handle.ToLowerInvariant(), out var t)) continue;
            if (!DateTime.TryParse(r.At, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at)) continue;
            if (at < StartFor(t)) continue;
            trades.Add((t, r.Type == "swap_buy", $"{t.Id}|{r.NetworkId}|{r.TokenAddress}", at, r.Price!.Value, r.Usd!.Value));
        }
        trades.Sort((a, b) => a.At.CompareTo(b.At));

        var open = new Dictionary<string, Position>();
        var positions = new List<Position>();
        foreach (var x in trades)
        {
            var qty = x.Usd / x.Price;
            open.TryGetValue(x.Key, out var p);
            if (x.Buy)
            {
                if (p == null || p.ClosedAt != null)
                {
                    open[x.Key] = p = new Position { TraderId = x.T.Id, OpenedAt = x.At };
                    positions.Add(p);
                }
                p.Qty += qty; p.Peak = Math.Max(p.Peak, p.Qty); p.CostUsd += x.Usd;
            }
            else if (p != null && p.ClosedAt == null)
            {
                var q = Math.Min(qty, p.Qty);
                p.SoldUsd += q * x.Price;
                p.Qty -= q;
                if (p.Qty <= p.Peak * 0.05) p.ClosedAt = x.At;
            }
        }

        var windowStart = now.AddDays(-WindowDays);
        var closed = positions.Where(p => p.ClosedAt != null).ToLookup(p => p.TraderId);
        var tracked = await db.Traders.Where(t => t.Platform == Platform.Fomo).ToDictionaryAsync(t => t.Id, ct);
        var result = new Dictionary<int, OwnTrading>();
        foreach (var t in fomo)
        {
            var all = closed[t.Id].ToList();
            tracked[t.Id].OwnRealizedUsd = all.Count > 0 ? Math.Round(all.Sum(p => p.SoldUsd - p.CostUsd)) : null;
            tracked[t.Id].OwnClosedPositions = all.Count;
            tracked[t.Id].OwnPnlSince = StartFor(t);
            var recent = all.Where(p => p.OpenedAt >= windowStart).ToList();
            if (recent.Count == 0) continue;
            var holds = recent.Select(p => (p.ClosedAt!.Value - p.OpenedAt).TotalMinutes).OrderBy(m => m).ToList();
            result[t.Id] = new OwnTrading(recent.Count, holds[holds.Count / 2], recent.Sum(p => p.SoldUsd - p.CostUsd),
                recent.Count(p => p.SoldUsd > p.CostUsd) / (double)recent.Count);
        }
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        return result;
    }

    // ---------- public PnL (pump) ----------

    // pump.fun's own numbers for every pump trader: 30d / 7d PnL and rank from its PnL leaderboard,
    // all-time portfolio PnL. The wallet and user id are looked up once from their handle.
    private async Task RefreshPumpPnlAsync(AppDbContext db, CancellationToken ct)
    {
        var pump = await db.Traders.Where(t => t.Platform == Platform.Pump).ToListAsync(ct);
        SetStatus(s => s with { Phase = "refreshing pump.fun PnL", Done = 0, Total = pump.Count });
        var done = 0;
        await Parallel.ForEachAsync(pump, new ParallelOptions { MaxDegreeOfParallelism = 5, CancellationToken = ct }, async (t, token) =>
        {
            try
            {
                if (t.Wallet == null || t.PumpUserId == null)
                {
                    var user = await _pump.GetUserAsync(t.Handle, token);
                    if (user is { } u) { t.PumpUserId = u.UserId; t.Wallet = u.Wallet; }
                }
                if (t.Wallet != null)
                {
                    var pnl = await _pump.GetWalletPnlAsync(t.Wallet, token);
                    if (pnl != null)
                    {
                        t.Pnl30dUsd = pnl.Pnl30dUsd is double m ? Math.Round(m) : null;
                        t.Pnl7dUsd = pnl.Pnl7dUsd is double w ? Math.Round(w) : null;
                        t.PnlRank30d = pnl.Rank30d;
                        t.PnlUpdatedAt = DateTime.UtcNow;
                    }
                }
                if (t.PumpUserId != null && await _pump.GetPortfolioPnlAsync(t.PumpUserId, token) is double all)
                    t.PortfolioPnlUsd = Math.Round(all);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "PnL refresh failed for {Handle}", t.Handle);
            }
            var n = Interlocked.Increment(ref done);
            if (n % 50 == 0) SetStatus(s => s with { Done = n });
        });
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
    }

    // First start after deploy: take categories and stats from the audit's intel file so
    // categories work immediately, rather than after the first full replay (a few hours).
    private async Task SeedFromIntelFileAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (await db.TraderRatings.AnyAsync(ct)) return;

        var file = _intel.Current;
        var traders = await db.Traders.ToListAsync(ct);
        var seeded = 0;
        // the file predates score ranks: derive the headline one from its composite scores
        var scored = file.Traders.Where(x => x.Score != null).ToList();
        var ranks = TraderRatingCalculator.Ranks(scored, x => x.Score!.Value);
        foreach (var t in traders)
        {
            var e = _intel.Get(t.Platform, t.Handle);
            var category = TraderCategories.Effective(e?.Style);
            db.TraderRatings.Add(new TraderRating
            {
                TraderId = t.Id,
                AutoCategory = category,
                Basis = e == null ? null : t.Platform == Platform.Pump ? "callout" : "buy",
                Score = e?.Score,
                Cut = e?.Cut,
                Calls = e?.Calls ?? 0,
                AlertsPerDay = e?.AlertsPerDay ?? 0,
                StatsJson = JsonSerializer.Serialize(new TraderRatingStats
                {
                    Summary = e?.Summary, MedianMcap = e?.MedianMcap, CapTier = e?.CapTier, Speed = e?.Speed,
                    Hit50In15m = e?.Hit50In15m, Hit2xIn24h = e?.Hit2xIn24h, StopOut30 = e?.StopOut30,
                    MedianMinutesToPeak = e?.MedianMinutesToPeak, AvgPerCall = e?.AvgPerCall, RecentCalls = e?.RecentCalls ?? new(),
                    ScoreRank = e != null && ranks.TryGetValue(e, out var rank) ? rank : null
                }, Json),
                ComputedAt = file.GeneratedAt == default ? DateTime.UtcNow : file.GeneratedAt
            });
            if (t.Category == null && !t.IsManuallyRecategorized)
            {
                t.Category = category;
                t.CategorizedAt = DateTime.UtcNow;
            }
            if (e != null) seeded++;
        }
        await db.SaveChangesAsync(ct);
        _logger.LogInformation("Seeded trader categories from the intel file: {Seeded} of {Total} traders", seeded, traders.Count);
    }

    public static TraderRatingStats ParseStats(string? json)
    {
        if (string.IsNullOrEmpty(json)) return new TraderRatingStats();
        try { return JsonSerializer.Deserialize<TraderRatingStats>(json, Json) ?? new TraderRatingStats(); }
        catch (JsonException) { return new TraderRatingStats(); }
    }
}
