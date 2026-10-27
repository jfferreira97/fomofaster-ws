using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using TelegramBot.Models;

namespace TelegramBot.Services;

// Candles for the dashboard's token chart (Accumulation tab), one page at a time like the charts on pump.fun, Axiom or DexScreener:
// the newest page first (a single request, so the chart shows at once), then older pages as the chart is scrolled left. Sources:
//   1. pump.fun's own candle route (CandleClient, through the local candle service that is past Cloudflare), which also gives the
//      supply pump.fun uses for the market cap and the token's creation time;
//   2. GeckoTerminal's public OHLCV (free, no key): the token's most active pool, found by searching the address;
//   3. our own tracked trades, bucketed into bars, when nobody else has the token (newest page only).
// Older pages never change, so they are kept for 6 hours; the newest page for 30 seconds; the pool of a token for an hour
// (GeckoTerminal allows ~30 calls a minute).
public sealed record ChartBar(long T, double O, double H, double L, double C, double V);
public sealed record ChartCandles(string Source, List<ChartBar> Bars, double? Supply, long? CreatedMs, bool HasOlder = false);

public class ChartCandleService
{
    private const int PageBars = 300;
    private readonly CandleClient _pump;
    private readonly ILogger<ChartCandleService> _logger;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private readonly ConcurrentDictionary<string, (DateTime Until, ChartCandles Result)> _cache = new();
    private readonly ConcurrentDictionary<string, (DateTime At, string? Network, string? Pool)> _pools = new();
    private DateTime _pumpSkipUntil = DateTime.MinValue;
    private DateTime _geckoSkipUntil = DateTime.MinValue;

    public ChartCandleService(CandleClient pump, ILogger<ChartCandleService> logger)
    {
        _pump = pump;
        _logger = logger;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    // One page of up to 300 bars of `resMinutes` (1, 5, 15, 60 or 240) ending before `beforeSec` (null = the newest page).
    public async Task<ChartCandles> PageAsync(Chain chain, string address, int resMinutes, long? beforeSec, Func<Task<List<TradePoint>>> ourTrades)
    {
        var key = $"{chain}:{address}:{resMinutes}:{beforeSec}";
        if (_cache.TryGetValue(key, out var hit) && DateTime.UtcNow < hit.Until) return hit.Result;

        var endMs = beforeSec is long b ? b * 1000 - 1 : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        ChartCandles result = new("none", new(), null, null);

        // 1) pump.fun
        try
        {
            var netId = ChainInfo.NetworkIdOf(chain);
            if (CandleClient.IsServed(netId) && DateTime.UtcNow >= _pumpSkipUntil)
            {
                var res = resMinutes switch { <= 1 => "1m", <= 5 => "5m", <= 15 => "15m", <= 60 => "1h", _ => "4h" };
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var got = await _pump.FetchChartAsync(netId, address, 0, endMs, res, 1, cts.Token);
                if (got is { Bars.Count: > 0 }) result = new("pump.fun", got.Bars, got.Supply, got.CreatedMs, got.HasOlder);
                else if (got is null) _pumpSkipUntil = DateTime.UtcNow.AddMinutes(10);
            }
        }
        catch (OperationCanceledException) { _logger.LogWarning("pump.fun candles timed out for {Address}", address); }   // slow, not broken: no lockout
        catch (Exception ex) { _pumpSkipUntil = DateTime.UtcNow.AddMinutes(10); _logger.LogWarning("pump.fun candles failed for {Address}: {Error}", address, ex.Message); }

        // 2) GeckoTerminal (after a 429 it is left alone for 2 minutes)
        if (result.Bars.Count == 0 && DateTime.UtcNow >= _geckoSkipUntil)
        {
            try
            {
                var (bars, older) = await GeckoPageAsync(address, endMs / 1000, resMinutes);
                if (bars.Count > 0) result = new("GeckoTerminal", bars, null, null, older);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests) { _geckoSkipUntil = DateTime.UtcNow.AddMinutes(2); _logger.LogWarning("GeckoTerminal rate limit hit, resting 2 minutes"); }
            catch (Exception ex) { _logger.LogWarning("GeckoTerminal candles failed for {Address}: {Error}", address, ex.Message); }
        }

        // 3) our own trades, all on the newest page: each bar opens where the previous one closed, so the line is continuous
        if (result.Bars.Count == 0 && beforeSec is null)
        {
            var trades = await ourTrades();
            var raw = trades.Where(t => t.Price is > 0).Select(t => (T: DateTimeOffset.Parse(t.At, CultureInfo.InvariantCulture).ToUnixTimeSeconds(), P: t.Price!.Value, V: t.Usd)).OrderBy(t => t.T).ToList();
            if (raw.Count > 0)
            {
                var bars = Bucket(raw.Select(r => new ChartBar(r.T, r.P, r.P, r.P, r.P, r.V)), resMinutes);
                for (var i = 1; i < bars.Count; i++)
                {
                    var o = bars[i - 1].C;
                    bars[i] = bars[i] with { O = o, H = Math.Max(bars[i].H, o), L = Math.Min(bars[i].L, o) };
                }
                result = new("our trades", bars, null, null);
            }
        }

        // a fallback result is only kept briefly, so the real candles show as soon as a source answers again
        var keep = result.Source is "pump.fun" or "GeckoTerminal" ? (beforeSec is null ? TimeSpan.FromSeconds(30) : TimeSpan.FromHours(6)) : TimeSpan.FromSeconds(10);
        _cache[key] = (DateTime.UtcNow + keep, result);
        if (_cache.Count > 2000)
            foreach (var old in _cache.Where(x => x.Value.Until < DateTime.UtcNow).Select(x => x.Key).ToList()) _cache.TryRemove(old, out _);
        return result;
    }

    // Bars of `resMinutes` from finer bars (or single prices): open of the first, close of the last, extremes, summed volume.
    private static List<ChartBar> Bucket(IEnumerable<ChartBar> fine, int resMinutes)
    {
        var step = resMinutes * 60L;
        return fine.OrderBy(b => b.T).GroupBy(b => b.T / step * step).Select(g =>
        {
            var l = g.ToList();
            return new ChartBar(g.Key, l[0].O, l.Max(b => b.H), l.Min(b => b.L), l[^1].C, l.Sum(b => b.V));
        }).ToList();
    }

    private async Task<(List<ChartBar> Bars, bool Older)> GeckoPageAsync(string address, long beforeSec, int resMinutes)
    {
        if (!_pools.TryGetValue(address, out var pool) || DateTime.UtcNow - pool.At > TimeSpan.FromHours(1))
        {
            using var doc = JsonDocument.Parse(await _http.GetStringAsync($"https://api.geckoterminal.com/api/v2/search/pools?query={Uri.EscapeDataString(address)}"));
            string? net = null, addr = null; double best = -1;
            foreach (var p in doc.RootElement.GetProperty("data").EnumerateArray())
            {
                var a = p.GetProperty("attributes");
                var baseId = p.GetProperty("relationships").GetProperty("base_token").GetProperty("data").GetProperty("id").GetString() ?? "";
                if (!baseId.EndsWith("_" + address, StringComparison.OrdinalIgnoreCase)) continue;   // the token must be the pool's base token
                var vol = a.TryGetProperty("volume_usd", out var v) && v.TryGetProperty("h24", out var h) && double.TryParse(h.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
                // the search result has no network field: the pool id is "<network>_<pool address>"
                if (vol > best) { best = vol; addr = a.GetProperty("address").GetString(); net = (p.GetProperty("id").GetString() ?? "").Split('_')[0]; }
            }
            pool = (DateTime.UtcNow, net, addr);
            _pools[address] = pool;
        }
        if (pool.Network is null || pool.Pool is null) return (new(), false);

        // GeckoTerminal has 1, 5 and 15-minute and 1 and 4-hour bars natively: every size the chart offers
        var (tf, agg) = resMinutes switch { <= 1 => ("minute", 1), <= 5 => ("minute", 5), <= 15 => ("minute", 15), <= 60 => ("hour", 1), _ => ("hour", 4) };
        var url = $"https://api.geckoterminal.com/api/v2/networks/{pool.Network}/pools/{pool.Pool}/ohlcv/{tf}?aggregate={agg}&limit={PageBars}&currency=usd&token={Uri.EscapeDataString(address)}&before_timestamp={beforeSec}";
        using var page = JsonDocument.Parse(await _http.GetStringAsync(url));
        var list = page.RootElement.GetProperty("data").GetProperty("attributes").GetProperty("ohlcv_list");
        var bars = list.EnumerateArray().Select(x => new ChartBar(x[0].GetInt64(), x[1].GetDouble(), x[2].GetDouble(), x[3].GetDouble(), x[4].GetDouble(), x[5].GetDouble())).OrderBy(b => b.T).ToList();
        return (bars, bars.Count >= PageBars);
    }
}
