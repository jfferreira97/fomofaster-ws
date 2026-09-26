using TelegramBot.Models;

namespace TelegramBot.Services;

// One trader's replayed calls for the rating window, plus what we know of their own trading.
public record TraderSample(
    int TraderId,
    string? BasisKind,                 // thesis | buy | callout; null = no calls at all
    IReadOnlyList<CallOutcome> Basis,  // every first post of that kind in the window, evaluated or not
    IReadOnlyList<CallOutcome> Buys,   // FOMO first buys (dump-on-followers check); empty for pump
    double AlertsPerDay,
    OwnTrading? Own = null,            // FOMO: their own round trips in the window (Holders)
    double? Pnl30dUsd = null);         // pump.fun's public 30-day PnL for their wallet

// A FOMO trader's own closed positions in the window, rebuilt from their buys and sells.
public record OwnTrading(int Closed, double MedianHoldMinutes, double RealizedUsd, double WinRate);

public record TraderVerdict(
    int TraderId,
    string Category,
    string? Basis,
    double? Score,
    string? Cut,
    int Calls,
    int Simulated,
    double AlertsPerDay,
    TraderRatingStats Stats);

// Display-only numbers behind a verdict (TraderRating.StatsJson).
public class TraderRatingStats
{
    public string? Summary { get; set; }
    public List<string> Reasons { get; set; } = new();
    public double? MedianMcap { get; set; }
    public string? CapTier { get; set; }
    public string? Speed { get; set; }
    public double? Hit50In15m { get; set; }
    public double? Hit2xIn24h { get; set; }
    public double? StopOut30 { get; set; }
    public double? MedianMinutesToPeak { get; set; }
    public double? AvgPerCall { get; set; }

    // What users sort by on /manage. ScoreRank: the composite score as a percentile among rated
    // traders (80 = better than 80% of them). The *Rank fields place each ingredient the same
    // way, so the score's hover breakdown can say where a trader stands on each.
    public int? ScoreRank { get; set; }
    public int? Hit2xRank { get; set; }
    public int? Return1hRank { get; set; }
    public int? StopOutRank { get; set; }       // higher = fewer stop-outs
    public int? AvgRank { get; set; }
    public int? PnlRank { get; set; }           // their own public 30d PnL
    public double? MedianReturn1h { get; set; } // typical price move 1h after the alert
    public string? BestWindow { get; set; }     // 15m | 1h | 4h | 24h
    public double? PlayEv { get; set; }         // avg result per alert playing that window's exit
    public int? PlayCalls { get; set; }
    public double? OwnHoldMinutes { get; set; } // FOMO: median hold of their own positions
    public double? OwnRealizedUsd { get; set; } // FOMO: realized on their own closed positions, window
    public List<RecentCall> RecentCalls { get; set; } = new();
}

// Turns replayed calls into a category per trader. Rules from the trader audit
// (scripts/trader-audit/build_intel.js), with categories by take-profit window:
//   - fewer than 8 replayable calls: unrated
//   - four exits are replayed per call, from the moment of the call: +30% / -20% within 15m,
//     +50% / -30% within 1h, 2x / -30% within 4h, 3x / -40% within 24h. Small samples are shrunk
//     toward the population so one lucky 10x doesn't decide anything
//   - composite score = z(2x rate) + z(median 1h return) - z(-30% stop-outs) + z(EV over the
//     four exits) + z(their public 30d PnL); the bottom 30% that also lose money on average,
//     anything losing 6%+ per call, and loud traders with no edge are cut (−EV)
//   - everyone else goes to the window that paid best: 15-min, 1-hour, 4-hour flips or day holds
//   - Holders (FOMO only): hold their own buys 12h+ typically, 5+ closed, net profitable. Their game
//     is longer than the 24h replay, so this wins over the replay's verdict
public static class TraderRatingCalculator
{
    public const int MinCalls = 8;
    private const double K = 12;
    private const double HolderMinHoldMinutes = 12 * 60;
    private const int HolderMinClosed = 5;

    private static readonly string[] Windows = { "15m", "1h", "4h", "24h" };
    private static readonly Dictionary<string, string> WindowCategory = new()
    {
        ["15m"] = TraderCategories.Scalper, ["1h"] = TraderCategories.Hourly,
        ["4h"] = TraderCategories.Flipper, ["24h"] = TraderCategories.Runner,
    };

    private static readonly Dictionary<int, string> ChainNames = new()
    {
        [1399811149] = "SOL", [4663] = "ROBINHOOD", [56] = "BNB", [8453] = "BASE", [1] = "ETH", [5042] = "ARC", [143] = "MONAD"
    };

    private static double? Exit(CallOutcome c, string window) => window switch
    {
        "15m" => c.EvQuick, "1h" => c.EvScalp, "4h" => c.EvFlip, _ => c.EvRunner
    };

    private class Work
    {
        public TraderSample S = null!;
        public List<CallOutcome> Sims = new();
        public int N => Sims.Count;
        public Dictionary<string, (double Ev, int N)> Ev = new();
        public double Hit2x, Sl30, Hit50In15m;
        public int? ScoreRank, Hit2xRank, Return1hRank, StopOutRank, AvgRank, PnlRank;
        public double? TPeakMed, R1hMed, McapMed, EntryVsCallMed, FastDumpShare, DumpFollowerLoss;
        public string Style = TraderCategories.Unrated;
        public string BestWindow = "1h";
        public double Best;
        public double? Score, EvAll;
        public string? Cut;
        public List<string> Reasons = new();
        public bool IsHolder => S.Own is { Closed: >= HolderMinClosed } o && o.MedianHoldMinutes >= HolderMinHoldMinutes && o.RealizedUsd > 0;
    }

    public static List<TraderVerdict> Rate(IEnumerable<TraderSample> samples)
    {
        var all = samples.Select(s =>
        {
            var w = new Work { S = s, Sims = s.Basis.Where(c => c.Status == CallOutcomeStatus.Ok).ToList() };
            var sims = w.Sims;
            if (sims.Count > 0)
            {
                foreach (var win in Windows)
                {
                    // the 15m exit is newer than some stored replays; average over the calls that have it
                    var vals = sims.Select(c => Exit(c, win)).Where(v => v != null).Select(v => v!.Value).ToList();
                    if (vals.Count > 0) w.Ev[win] = (vals.Average(), vals.Count);
                }
                w.Hit2x = Share(sims, c => c.Pk24h >= 2);
                w.Sl30 = Share(sims, c => c.Sl30 == true);
                w.Hit50In15m = Share(sims, c => c.Pk15m >= 1.5);
                w.TPeakMed = Q(sims.Where(c => c.Pk24h >= 1.3).Select(c => c.MinutesToPeak!.Value), 0.5);
                w.R1hMed = Q(sims.Select(c => c.R1h!.Value), 0.5);
                w.EntryVsCallMed = Q(sims.Where(c => c.EntryVsCall != null).Select(c => c.EntryVsCall!.Value), 0.5);
            }
            // over $20B is a broken reading (bad supply or price print), not a real call
            w.McapMed = Q(s.Basis.Where(c => c.McapAtCall > 0 && c.McapAtCall < 2e10).Select(c => c.McapAtCall!.Value), 0.5);
            if (s.Buys.Count > 0)
            {
                w.FastDumpShare = Share(s.Buys, c => c.SoldIn10mFrac >= 0.5);
                var dumps = s.Buys.Where(c => c.Status == CallOutcomeStatus.Ok && c.SoldIn10mFrac >= 0.5).ToList();
                w.DumpFollowerLoss = dumps.Count > 0 ? Share(dumps, c => c.EvScalp < 0) : null;
            }
            return w;
        }).ToList();

        var rated = all.Where(w => w.N >= MinCalls).ToList();
        var pop = Windows.ToDictionary(win => win, win =>
        {
            var xs = rated.Where(w => w.Ev.ContainsKey(win)).Select(w => w.Ev[win].Ev).ToList();
            return xs.Count > 0 ? xs.Average() : 0;
        });
        var popHit2x = rated.Count > 0 ? rated.Average(w => w.Hit2x) : 0;
        var popSl30 = rated.Count > 0 ? rated.Average(w => w.Sl30) : 0;
        static double Sh(double v, int n, double prior, double k) => (v * n + prior * k) / (n + k);
        double Shrunk(Work w, string win, double k) => w.Ev.TryGetValue(win, out var e) ? Sh(e.Ev, e.N, pop[win], k) : pop[win];

        foreach (var w in rated)
        {
            foreach (var win in Windows)
            {
                var v = Shrunk(w, win, K);
                if (win == Windows[0] || v > w.Best) { w.Best = v; w.BestWindow = win; }
            }
            var alertsPerDay = w.S.AlertsPerDay;

            // noise / exclusion reasons: the follower's view, not the trader's
            if (w.Best < -0.05) w.Reasons.Add($"followers lose {-w.Best * 100:0}% per call even with the best exit");
            else if (w.Best < -0.02) w.Reasons.Add($"followers lose {-w.Best * 100:0.0}% per call even with the best exit");
            if (alertsPerDay >= 20 && w.Best < 0.02) w.Reasons.Add($"{alertsPerDay:0} alerts/day with no edge");
            if (w.Sl30 >= 0.6 && w.Hit2x < 0.15) w.Reasons.Add($"{w.Sl30 * 100:0}% of calls dump 30% within 4h");
            if (w.FastDumpShare >= 0.35 && w.DumpFollowerLoss >= 0.6)
                w.Reasons.Add($"sells {w.FastDumpShare * 100:0}% of their buys within 10 min; followers lost on {w.DumpFollowerLoss * 100:0}% of those");
            if (w.EntryVsCallMed > 0.15) w.Reasons.Add($"price already +{w.EntryVsCallMed * 100:0}% by the time followers get in");

            var noisy = w.Best < -0.02 || (alertsPerDay >= 20 && w.Best < 0.02)
                || (w.FastDumpShare >= 0.35 && w.DumpFollowerLoss >= 0.6 && w.Best < 0.02);
            w.Style = noisy ? TraderCategories.Noise : WindowCategory[w.BestWindow];
        }

        // composite score (the audit's, validated out of sample, plus their public PnL): everything
        // shrunk, because past EV alone barely persists while hit and stop-out rates do
        if (rated.Count > 0)
        {
            var h2 = new Dictionary<Work, double>();
            var sl = new Dictionary<Work, double>();
            foreach (var w in rated)
            {
                w.EvAll = Windows.Average(win => Shrunk(w, win, 30));
                h2[w] = Sh(w.Hit2x, w.N, popHit2x, 20);
                sl[w] = Sh(w.Sl30, w.N, popSl30, 20);
            }
            // PnL on a signed log scale ($1K and $1M shouldn't be 1000x apart); unknown = average
            static double LogUsd(double v) => Math.Sign(v) * Math.Log10(1 + Math.Abs(v));
            var known = rated.Where(w => w.S.Pnl30dUsd != null).Select(w => LogUsd(w.S.Pnl30dUsd!.Value)).ToList();
            var pnlMean = known.Count > 0 ? known.Average() : 0;
            double PnlOf(Work w) => w.S.Pnl30dUsd is double p ? LogUsd(p) : pnlMean;

            var zh = Z(rated, w => h2[w]);
            var zr = Z(rated, w => w.R1hMed ?? 0);
            var zs = Z(rated, w => sl[w]);
            var ze = Z(rated, w => w.EvAll!.Value);
            var zp = Z(rated, PnlOf);
            foreach (var w in rated) w.Score = zh(w) + zr(w) - zs(w) + ze(w) + zp(w);

            // percentiles among rated traders, for display: the score and each ingredient
            var scoreRank = Ranks(rated, w => w.Score!.Value);
            var h2Rank = Ranks(rated, w => h2[w]);
            var r1Rank = Ranks(rated, w => w.R1hMed ?? 0);
            var slRank = Ranks(rated, w => -sl[w]);
            var evRank = Ranks(rated, w => w.EvAll!.Value);
            var withPnl = rated.Where(w => w.S.Pnl30dUsd != null).ToList();
            var pnlRank = Ranks(withPnl, w => w.S.Pnl30dUsd!.Value);
            foreach (var w in rated)
            {
                w.ScoreRank = scoreRank[w]; w.Hit2xRank = h2Rank[w]; w.Return1hRank = r1Rank[w];
                w.StopOutRank = slRank[w]; w.AvgRank = evRank[w];
                w.PnlRank = pnlRank.TryGetValue(w, out var pr) ? pr : null;
            }

            var sorted = rated.Select(w => w.Score!.Value).OrderBy(x => x).ToList();
            var p30 = sorted[(int)Math.Floor(sorted.Count * 0.3)];
            var p20 = sorted[(int)Math.Floor(sorted.Count * 0.2)];
            foreach (var w in rated)
            {
                var ev = w.EvAll!.Value;
                var loud = w.S.AlertsPerDay >= 20;
                var cut = (w.Score <= p30 && ev < 0) || ev < -0.06 || (loud && ev < 0.005);
                w.Cut = cut ? ((w.Score <= p20 || ev < -0.08 || (loud && ev < 0)) ? "hard" : "soft") : null;
                if (cut) w.Style = TraderCategories.Noise;
                // flagged by the single-EV rule but the robust score disagrees: back to their window
                else if (w.Style == TraderCategories.Noise) w.Style = WindowCategory[w.BestWindow];
                if (cut && w.Score <= p30 && w.Reasons.Count == 0) w.Reasons.Add("bottom 30% on the combined score");
                if (cut && loud && ev < 0.005 && !w.Reasons.Any(r => r.Contains("alerts/day"))) w.Reasons.Add($"{w.S.AlertsPerDay:0} alerts/day with no edge");
            }
        }

        foreach (var w in all.Where(w => w.IsHolder)) w.Style = TraderCategories.Holder;

        return all.Select(w => new TraderVerdict(
            w.S.TraderId, w.Style, w.S.BasisKind, Round(w.Score), w.Cut,
            Calls: w.S.Basis.Count, Simulated: w.N, AlertsPerDay: Math.Round(w.S.AlertsPerDay, 2),
            Stats: StatsFor(w))).ToList();
    }

    private static TraderRatingStats StatsFor(Work w)
    {
        var stats = new TraderRatingStats
        {
            Reasons = w.Style == TraderCategories.Noise ? w.Reasons : new List<string>(),
            MedianMcap = w.McapMed is double m ? Math.Round(m) : null,
            CapTier = w.McapMed switch { null => null, < 50e3 => "micro", < 500e3 => "small", < 5e6 => "mid", _ => "large" },
            Speed = w.TPeakMed switch { null => null, <= 20 => "minutes", <= 240 => "hours", _ => "day" },
            OwnHoldMinutes = w.S.Own?.MedianHoldMinutes is double h ? Math.Round(h) : null,
            OwnRealizedUsd = w.S.Own?.RealizedUsd is double r ? Math.Round(r) : null,
            RecentCalls = w.Sims.OrderByDescending(c => c.CalledAt).Take(12).Select(c => new RecentCall
            {
                T = DateTime.SpecifyKind(c.CalledAt, DateTimeKind.Utc),
                Ticker = c.Ticker?.Trim(),
                Chain = ChainNames.GetValueOrDefault(c.NetworkId),
                Mcap = c.McapAtCall is double mc ? Math.Round(mc) : null,
                Peak24h = Math.Round(c.Pk24h!.Value, 2),
                R1h = Math.Round(c.R1h!.Value, 3),
                MinutesToPeak = (int)Math.Round(c.MinutesToPeak!.Value)
            }).ToList()
        };
        if (w.Style == TraderCategories.Holder && w.S.Own is { } own)
            stats.Summary = $"Holds their own buys ~{own.MedianHoldMinutes / 60:0}h typically and made {Usd(own.RealizedUsd)} on {own.Closed} closed positions in the last 30 days.";
        if (w.N < MinCalls) return stats;

        stats.Hit50In15m = Round(w.Hit50In15m);
        stats.Hit2xIn24h = Round(w.Hit2x);
        stats.StopOut30 = Round(w.Sl30);
        stats.MedianMinutesToPeak = w.TPeakMed is double t ? Math.Round(t) : null;
        stats.AvgPerCall = Round(w.EvAll);
        stats.ScoreRank = w.ScoreRank;
        stats.Hit2xRank = w.Hit2xRank;
        stats.Return1hRank = w.Return1hRank;
        stats.StopOutRank = w.StopOutRank;
        stats.AvgRank = w.AvgRank;
        stats.PnlRank = w.PnlRank;
        stats.MedianReturn1h = Round(w.R1hMed);
        stats.BestWindow = w.BestWindow;
        if (w.Style != TraderCategories.Holder && w.Ev.TryGetValue(w.BestWindow, out var bestPlay))
        {
            stats.PlayEv = Round(bestPlay.Ev);
            stats.PlayCalls = bestPlay.N;
        }
        if (w.Style != TraderCategories.Holder)
        {
            var play = w.BestWindow switch
            {
                "15m" => "taking +30% within 15 minutes of the call",
                "1h" => "taking +50% within the hour",
                "4h" => "aiming for 2x within 4 hours",
                _ => "holding for 3x through the day"
            };
            stats.Summary = $"Best played by {play}. {Pct(w.Hit50In15m)} of calls ran +50% within 15 min, {Pct(w.Hit2x)} hit 2x within 24h. Median entry {Usd(w.McapMed)}.";
        }
        return stats;
    }

    // 0-100 percentile of each trader on f (ties share the lower rank; best = 100)
    public static Dictionary<T, int> Ranks<T>(IReadOnlyList<T> xs, Func<T, double> f) where T : notnull
    {
        var sorted = xs.Select(f).OrderBy(v => v).ToList();
        return xs.ToDictionary(x => x, x =>
        {
            if (sorted.Count <= 1) return 50;
            var below = LowerBound(sorted, f(x));
            return (int)Math.Round(100.0 * below / (sorted.Count - 1));
        });
    }

    private static int LowerBound(List<double> sorted, double v)
    {
        int lo = 0, hi = sorted.Count;
        while (lo < hi) { var m = (lo + hi) / 2; if (sorted[m] < v) lo = m + 1; else hi = m; }
        return lo;
    }

    private static Func<Work, double> Z(List<Work> ws, Func<Work, double> f)
    {
        var v = ws.Select(f).ToList();
        var mean = v.Average();
        var sd = Math.Sqrt(v.Average(x => (x - mean) * (x - mean)));
        if (sd == 0) sd = 1;
        return w => (f(w) - mean) / sd;
    }

    private static double Share<T>(IReadOnlyCollection<T> xs, Func<T, bool> f) => xs.Count == 0 ? 0 : xs.Count(f) / (double)xs.Count;

    private static double? Q(IEnumerable<double> xs, double p)
    {
        var s = xs.OrderBy(x => x).ToList();
        return s.Count == 0 ? null : s[Math.Min(s.Count - 1, (int)Math.Floor(p * s.Count))];
    }

    private static double? Round(double? v) => v is double d && !double.IsNaN(d) ? Math.Round(d, 3) : null;
    private static string Pct(double v) => $"{Math.Round(v * 100)}%";
    private static string Usd(double? v) => v switch
    {
        null => "?",
        <= -1e6 or >= 1e6 => $"${v / 1e6:0.0}M",
        <= -1e3 or >= 1e3 => $"${v / 1e3:0}K",
        _ => $"${v:0}"
    };
}
