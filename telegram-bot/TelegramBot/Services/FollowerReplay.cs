namespace TelegramBot.Services;

public readonly record struct PriceBar(long T, double O, double H, double L, double C);

public record ReplayResult(
    double EntryPrice,
    double R1h, double R24h,
    double Pk15m, double Pk1h, double Pk24h,
    double MinutesToPeak,
    bool Sl30,
    double EvQuick, double EvScalp, double EvFlip, double EvRunner);

// A follower acting on an alert, replayed over 1-minute candles. C# port of the research
// scripts (scripts/trader-audit/lib.js cleanBars, analyze.js simulate) so the live ratings
// mean the same thing as the audit report.
public static class FollowerReplay
{
    public const double Cost = 0.03; // round-trip fees + slippage on small caps
    private const long Min = 60_000, Hour = 60 * Min;

    // The candle service occasionally serves impossible prints on EVM tokens: a single 1m high
    // of 1e28x, or the series switching price units mid-way. Against a rolling median of the
    // last 5 closes: clamp wicks to 6x, and end the series at the first bar whose close is 30x
    // away (memecoins do run 20-30x in a day, but not within one minute).
    public static List<PriceBar> Clean(IEnumerable<PriceBar> bars)
    {
        var output = new List<PriceBar>();
        var last = new List<double>();
        foreach (var b in bars)
        {
            if (!(b.O > 0 && b.C > 0)) continue;
            if (last.Count > 0)
            {
                var sorted = last.OrderBy(x => x).ToList();
                var r = sorted[sorted.Count >> 1];
                if (b.C > r * 30 || b.C < r / 30 || b.O > r * 30 || b.O < r / 30) break;
                output.Add(b with
                {
                    H = Math.Min(b.H, Math.Max(Math.Max(b.O, b.C), r) * 6),
                    L = Math.Max(b.L, Math.Min(Math.Min(b.O, b.C), r) / 6)
                });
            }
            else
            {
                output.Add(b with { H = Math.Min(b.H, Math.Max(b.O, b.C) * 6), L = Math.Max(b.L, Math.Min(b.O, b.C) / 6) });
            }
            last.Add(b.C);
            if (last.Count > 5) last.RemoveAt(0);
        }
        return output;
    }

    // Entry: open of the first bar at or after the post, if one starts within 5 minutes.
    // Returns null when there was no trading near the post (nothing a follower could act on).
    public static ReplayResult? Simulate(IReadOnlyList<PriceBar> bars, long postedAtMs)
    {
        var i0 = -1;
        for (var i = 0; i < bars.Count; i++)
            if (bars[i].T >= postedAtMs) { i0 = i; break; }
        if (i0 < 0 || bars[i0].T - postedAtMs > 5 * Min) return null;

        var tE = bars[i0].T;
        var e = bars[i0].O;
        if (!(e > 0)) return null;

        var path = new List<PriceBar>();
        for (var i = i0; i < bars.Count && bars[i].T <= tE + 24 * Hour; i++) path.Add(bars[i]);

        double CloseAt(long t)
        {
            var p = e;
            foreach (var b in path) { if (b.T > t) break; p = b.C; }
            return p / e - 1;
        }
        double PeakWithin(long dt)
        {
            var m = e;
            foreach (var b in path) { if (b.T > tE + dt) break; m = Math.Max(m, b.H); }
            return m / e;
        }
        // take-profit / stop / time-stop; when one bar spans both, assume the stop hit first
        double Strat(double tp, double sl, long maxT)
        {
            foreach (var b in path)
            {
                if (b.T > tE + maxT) break;
                if (b.L / e - 1 <= -sl) return -sl - Cost;
                if (b.H / e - 1 >= tp) return tp - Cost;
            }
            return CloseAt(tE + maxT) - Cost;
        }

        double peak = e;
        long tPeak = tE;
        foreach (var b in path)
            if (b.H > peak) { peak = b.H; tPeak = b.T; }

        return new ReplayResult(
            EntryPrice: e,
            R1h: CloseAt(tE + Hour),
            R24h: CloseAt(tE + 24 * Hour),
            Pk15m: PeakWithin(15 * Min),
            Pk1h: PeakWithin(Hour),
            Pk24h: peak / e,
            MinutesToPeak: (tPeak - tE) / (double)Min,
            Sl30: path.Any(b => b.T <= tE + 4 * Hour && b.L / e - 1 <= -0.3),
            EvQuick: Strat(0.3, 0.2, 15 * Min),
            EvScalp: Strat(0.5, 0.3, Hour),
            EvFlip: Strat(1.0, 0.3, 4 * Hour),
            EvRunner: Strat(2.0, 0.4, 24 * Hour));
    }

    // Where no candle source served the token: a price path from every trade and callout price
    // captured for it, from any trader. Sparse (highs and lows between prints are missed), so it
    // needs 5+ prints in the 24h after the post to be used at all.
    public static List<PriceBar>? TapeBars(IEnumerable<(long T, double P)> points, long postedAtMs)
    {
        var pts = points.Where(x => x.T > postedAtMs && x.T <= postedAtMs + 24 * Hour).OrderBy(x => x.T).ToList();
        return pts.Count >= 5 ? pts.Select(x => new PriceBar(x.T, x.P, x.P, x.P, x.P)).ToList() : null;
    }
}
