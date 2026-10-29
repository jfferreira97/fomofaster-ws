using System.Net;
using System.Text.Json;

namespace TelegramBot.Services;

// Candles from pump.fun's multi-chain candle route (free, no key; serves Solana and most EVM chains we see). Pages backwards from the
// end of the range, 300 bars at a time (the route refuses more since October 2026: "count must not be greater than 300", which it
// answers with a 400 that used to be read as "token unknown" and left most calls without price data).
// The route answers a plain HTTP client from this machine with a Cloudflare challenge, so every request first goes through the local
// candle service (a real Chrome past Cloudflare, see pump-ws-sidecar/src/candle-proxy.ts, 127.0.0.1:8130) and only falls back to a
// direct request when that service is down.
public class CandleClient
{
    public const string HttpClientName = "candles";
    private static readonly HashSet<int> Served = new() { 1399811149, 4663, 56, 8453, 1, 5042 };

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<CandleClient> _logger;

    public CandleClient(IHttpClientFactory httpFactory, ILogger<CandleClient> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public static bool IsServed(int networkId) => Served.Contains(networkId);

    // Candle services, each going out through its own IP (pump.fun limits the route per IP). With more than one, a request goes to the
    // one with fewer in flight, so the one slowed by 429s takes less, and one that is down is skipped.
    private static readonly string[] ProxyUrls = { "http://127.0.0.1:8130/fetch?url=" };
    private readonly DateTime[] _proxyDownUntil = new DateTime[ProxyUrls.Length];
    private readonly int[] _inFlight = new int[ProxyUrls.Length];
    private const int PageSize = 300;
    private int _badRequestsLogged;

    // Candles for a chart, in any size the route knows (1m, 5m, 15m, 1h, 4h), with the volume, plus the supply pump.fun itself uses for
    // the market cap and the token's creation time. Null = the source kept failing.
    public sealed record ChartFetch(List<ChartBar> Bars, double? Supply, long? CreatedMs, bool HasOlder = false);
    public async Task<ChartFetch?> FetchChartAsync(int networkId, string address, long startMs, long endMs, string res, int maxPages, CancellationToken ct)
    {
        if (!Served.Contains(networkId)) return new ChartFetch(new(), null, null);
        var http = _httpFactory.CreateClient(HttpClientName);
        http.Timeout = TimeSpan.FromSeconds(20);
        var bars = new List<ChartBar>();
        double? supply = null; long? created = null;
        var older = false;
        var to = Math.Min(endMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        for (var page = 0; page < maxPages; page++)
        {
            JsonDocument? doc;
            try { doc = await GetAsync(http, $"https://frontend-api-v3.pump.fun/candles/{Caip(networkId)}/{Uri.EscapeDataString(address)}?res={res}&count={PageSize}&to={to}", ct, priority: true); }
            catch (OperationCanceledException) when (bars.Count > 0) { break; }   // out of time: show what came so far
            if (doc == null) return bars.Count > 0 ? new ChartFetch(bars, supply, created) : null;
            if (doc.RootElement.TryGetProperty("supplyUsed", out var su)) supply ??= Num(doc.RootElement, "supplyUsed") is > 0 and var sv ? sv : null;
            if (doc.RootElement.TryGetProperty("createdMs", out var cm) && cm.ValueKind == JsonValueKind.Number) created ??= cm.GetInt64();
            if (!doc.RootElement.TryGetProperty("candles", out var candles) || candles.ValueKind != JsonValueKind.Array) break;
            var got = candles.EnumerateArray().Select(c => new ChartBar(Long(c, "openTimeMs") / 1000, Num(c, "open"), Num(c, "high"), Num(c, "low"), Num(c, "close"), Num(c, "volumeUsd"))).OrderBy(b => b.T).ToList();
            bars.InsertRange(0, got);
            older = got.Count > 0 && doc.RootElement.TryGetProperty("hasOlder", out var ho) && ho.ValueKind == JsonValueKind.True;
            if (!older || got[0].T * 1000 <= startMs) break;
            to = got[0].T * 1000 - 1;
        }
        return new ChartFetch(bars.Where(b => b.T * 1000 >= startMs - 60_000).GroupBy(b => b.T).Select(g => g.First()).OrderBy(b => b.T).ToList(), supply, created, older);
    }

    private static string Caip(int networkId) =>
        networkId == 1399811149 ? "solana:5eykt4UsFv8P8NJdTREpY1vzqKqZKvdp" : $"eip155:{networkId}";

    // Bars for replaying a group of calls on one token, from 5 minutes before the first call to endMs, oldest first: 1-minute bars
    // for the first ~5 hours after each call (entry, 15-minute and 1-hour peaks, the quick exits, the 4-hour stop), 5-minute bars
    // for the rest of the 24 hours. That is 1-2 five-minute pages plus one 1-minute page per cluster of calls, instead of a
    // 1-minute page for every 5 hours, which matters now that pump.fun rate-limits the candle route to a few hundred requests an hour.
    // Empty = the source has nothing for this token. Null = the source failed or was too slow (retry on a later run).
    public async Task<List<PriceBar>?> FetchReplayAsync(int networkId, string address, IReadOnlyList<long> callsMs, long endMs, CancellationToken ct)
    {
        if (!Served.Contains(networkId) || callsMs.Count == 0)
            return new List<PriceBar>();

        var http = _httpFactory.CreateClient(HttpClientName);
        http.Timeout = TimeSpan.FromMinutes(5);   // waits in the candle service's queue, behind the dashboard's charts
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var startMs = callsMs.Min() - 5 * 60_000;
        endMs = Math.Min(endMs, now);

        // 1-minute pages: each reaches from 295 minutes after a call back 300 bars (further when the token had quiet minutes)
        var fine = new List<PriceBar>();
        var spans = new List<(long From, long To)>();
        foreach (var t in callsMs.OrderBy(x => x))
        {
            if (spans.Any(s => t - 5 * 60_000 >= s.From && t + 4 * 3_600_000 <= s.To)) continue;
            var to = Math.Min(t + 295 * 60_000L, now);
            var page = await PageAsync(http, networkId, address, "1m", to, ct);
            if (page == null) return null;
            if (page.Count == 0 && fine.Count == 0) return page;   // pump.fun has nothing for this token
            fine.AddRange(page);
            spans.Add((page.Count >= PageSize ? page[0].T : long.MinValue, to));
        }

        // 5-minute pages over the whole range, used only outside the 1-minute spans
        var coarse = new List<PriceBar>();
        var to5 = endMs;
        for (var p = 0; p < 4; p++)
        {
            var page = await PageAsync(http, networkId, address, "5m", to5, ct);
            if (page == null) return null;
            coarse.InsertRange(0, page);
            if (page.Count < PageSize || page[0].T <= startMs) break;
            to5 = page[0].T - 1;
        }

        return fine
            .Concat(coarse.Where(b => !spans.Any(s => b.T + 5 * 60_000 > s.From && b.T < s.To)))
            .Where(b => b.T >= startMs - 60_000 && b.T <= endMs)
            .GroupBy(b => b.T).Select(g => g.First())
            .OrderBy(b => b.T)
            .ToList();
    }

    // One page of up to 300 bars ending at `to`, oldest first. Empty = nothing there. Null = the source failed.
    private async Task<List<PriceBar>?> PageAsync(HttpClient http, int networkId, string address, string res, long to, CancellationToken ct)
    {
        using var doc = await GetAsync(http, $"https://frontend-api-v3.pump.fun/candles/{Caip(networkId)}/{Uri.EscapeDataString(address)}?res={res}&count={PageSize}&to={to}", ct);
        if (doc == null) return null;
        if (!doc.RootElement.TryGetProperty("candles", out var candles) || candles.ValueKind != JsonValueKind.Array) return new();
        return candles.EnumerateArray()
            .Select(c => new PriceBar(Long(c, "openTimeMs"), Num(c, "open"), Num(c, "high"), Num(c, "low"), Num(c, "close")))
            .OrderBy(b => b.T)
            .ToList();
    }

    // The local candle service, when it is up: it passes the request to pump.fun from a real Chrome. Null = not available this time.
    // Priority requests (a chart on the dashboard) jump the service's queue, ahead of the ratings replay.
    private async Task<JsonDocument?> ViaProxyAsync(HttpClient http, string url, bool priority, CancellationToken ct)
    {
        var tried = new bool[ProxyUrls.Length];
        for (var round = 0; round < ProxyUrls.Length; round++)
        {
            var i = -1;
            for (var k = 0; k < ProxyUrls.Length; k++)
                if (!tried[k] && DateTime.UtcNow >= _proxyDownUntil[k] && (i < 0 || Volatile.Read(ref _inFlight[k]) < Volatile.Read(ref _inFlight[i]))) i = k;
            if (i < 0) return null;
            tried[i] = true;
            Interlocked.Increment(ref _inFlight[i]);
            try
            {
                using var res = await http.GetAsync(ProxyUrls[i] + Uri.EscapeDataString(url) + (priority ? "&prio=1" : ""), ct);
                var body = await res.Content.ReadAsStringAsync(ct);
                if (res.StatusCode == HttpStatusCode.OK) return JsonDocument.Parse(body);
                if (res.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound)
                {
                    if (res.StatusCode == HttpStatusCode.BadRequest && Interlocked.Increment(ref _badRequestsLogged) <= 3)
                        _logger.LogWarning("pump.fun refused a candle request (400): {Body}", body.Length > 300 ? body[..300] : body);
                    return JsonDocument.Parse("{}");
                }
                // challenge or upstream trouble: the other service gets its chance, then the direct route below
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (TaskCanceledException) { return null; }   // our own timeout: the service is busy or slowed by pump.fun's rate limit, not down
            catch (Exception) { _proxyDownUntil[i] = DateTime.UtcNow.AddMinutes(2); }
            finally { Interlocked.Decrement(ref _inFlight[i]); }
        }
        return null;
    }

    private async Task<JsonDocument?> GetAsync(HttpClient http, string url, CancellationToken ct, bool priority = false)
    {
        var proxied = await ViaProxyAsync(http, url, priority, ct);
        if (proxied != null) return proxied;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
                req.Headers.TryAddWithoutValidation("Accept", "application/json");
                using var res = await http.SendAsync(req, ct);
                if (res.StatusCode == HttpStatusCode.OK)
                    return JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
                if (res.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound)
                    return JsonDocument.Parse("{}"); // unknown token: nothing to fetch, not a failure
                if (res.StatusCode == HttpStatusCode.Forbidden)
                    return null;   // Cloudflare's challenge page: retrying from a plain client never gets past it
                // 503s are sporadic lane hiccups (often more than half of requests): retry quickly
                // with jitter. 429 means actually slow down.
                await Task.Delay(res.StatusCode == HttpStatusCode.TooManyRequests ? 5000 * (attempt + 1) : 200 + Random.Shared.Next(300), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (attempt == 9) _logger.LogWarning(ex, "Candle fetch failed: {Url}", url);
                await Task.Delay(1000 * (attempt + 1), ct);
            }
        }
        return null;
    }

    private static double Num(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return 0;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String when double.TryParse(v.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) => d,
            _ => 0
        };
    }

    private static long Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
}
