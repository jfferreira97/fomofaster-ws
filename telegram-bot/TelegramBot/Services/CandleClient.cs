using System.Net;
using System.Text.Json;

namespace TelegramBot.Services;

// 1-minute candles from pump.fun's multi-chain candle route (free, no key; serves Solana and
// most EVM chains we see). Pages backwards from the end of the range, 1000 bars at a time.
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

    private static string Caip(int networkId) =>
        networkId == 1399811149 ? "solana:5eykt4UsFv8P8NJdTREpY1vzqKqZKvdp" : $"eip155:{networkId}";

    // Bars covering [startMs, endMs], oldest first. Empty = the source has nothing for this token.
    // Null = the source kept failing (retry on a later run).
    public async Task<List<PriceBar>?> FetchAsync(int networkId, string address, long startMs, long endMs, CancellationToken ct)
    {
        if (!Served.Contains(networkId))
            return new List<PriceBar>();

        var http = _httpFactory.CreateClient(HttpClientName);
        http.Timeout = TimeSpan.FromSeconds(20);
        var bars = new List<PriceBar>();
        var to = Math.Min(endMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        for (var page = 0; page < 8; page++)
        {
            var url = $"https://frontend-api-v3.pump.fun/candles/{Caip(networkId)}/{Uri.EscapeDataString(address)}?res=1m&count=1000&to={to}";
            var doc = await GetAsync(http, url, ct);
            if (doc == null) return null;
            if (!doc.RootElement.TryGetProperty("candles", out var candles) || candles.ValueKind != JsonValueKind.Array)
                break;

            var got = new List<PriceBar>();
            foreach (var c in candles.EnumerateArray())
                got.Add(new PriceBar(Long(c, "openTimeMs"), Num(c, "open"), Num(c, "high"), Num(c, "low"), Num(c, "close")));
            got.Sort((a, b) => a.T.CompareTo(b.T));
            bars.InsertRange(0, got);

            var hasOlder = doc.RootElement.TryGetProperty("hasOlder", out var ho) && ho.ValueKind == JsonValueKind.True;
            if (got.Count == 0 || !hasOlder || got[0].T <= startMs) break;
            to = got[0].T - 1;
        }

        return bars.Where(b => b.T >= startMs - 60_000 && b.T <= endMs).ToList();
    }

    private async Task<JsonDocument?> GetAsync(HttpClient http, string url, CancellationToken ct)
    {
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
