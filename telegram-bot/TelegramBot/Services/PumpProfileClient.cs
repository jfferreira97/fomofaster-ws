using System.Net;
using System.Text.Json;

namespace TelegramBot.Services;

public record PumpPnl(double? Pnl30dUsd, double? Pnl7dUsd, int? Rank30d);

// pump.fun's public profile and PnL endpoints (the same ones the trader audit scraped): a user's
// wallet and id by handle, a wallet's position on pump.fun's own PnL leaderboard, and a user's
// portfolio PnL. These are pump.fun's numbers; we only read them.
public class PumpProfileClient
{
    public const string HttpClientName = "pumpapi";
    private const string Base = "https://frontend-api-v3.pump.fun";

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<PumpProfileClient> _logger;

    public PumpProfileClient(IHttpClientFactory httpFactory, ILogger<PumpProfileClient> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    // (userId, main Solana wallet) for a pump.fun handle; null when there's no such user.
    public async Task<(string UserId, string? Wallet)?> GetUserAsync(string handle, CancellationToken ct)
    {
        using var doc = await GetAsync($"/users/{Uri.EscapeDataString(handle)}", ct);
        if (doc == null || !doc.RootElement.TryGetProperty("userId", out var id)) return null;
        var wallet = Str(doc.RootElement, "canonical_svm_wallet") ?? Str(doc.RootElement, "address");
        return (id.ToString(), wallet);
    }

    // The wallet's 30-day and 7-day PnL and 30-day rank on pump.fun's PnL leaderboard. Null = the
    // lookup failed; a wallet that isn't ranked comes back with nulls.
    public async Task<PumpPnl?> GetWalletPnlAsync(string wallet, CancellationToken ct)
    {
        using var doc = await GetAsync($"/pnl-leaderboard/projected-rank?wallet={Uri.EscapeDataString(wallet)}", ct);
        if (doc == null) return null;
        (double? Pnl, int? Rank) Period(string name)
        {
            if (!doc.RootElement.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.Object) return (null, null);
            if (!p.TryGetProperty("found", out var f) || f.ValueKind != JsonValueKind.True) return (null, null);
            if (!p.TryGetProperty("entry", out var e) || e.ValueKind != JsonValueKind.Object) return (null, null);
            // pump.fun's board has corrupt entries (e.g. -108,000 SOL "lost" on 6.7 SOL of buys).
            // Real wallets lose at most ~2x what they spent in the period (older bags sold at a
            // loss); more than 5x is bad data, so the period counts as unknown.
            var pnlSol = Num(e, "pnlSol");
            var spendSol = Num(e, "buySpendSol") ?? 0;
            if (pnlSol < 0 && -pnlSol > 5 * Math.Max(spendSol, 1)) return (null, null);
            int? rank = p.TryGetProperty("projectedRank", out var r) && r.ValueKind == JsonValueKind.Number ? r.GetInt32() : null;
            return (Num(e, "pnlUsd"), rank);
        }
        var monthly = Period("monthly");
        var weekly = Period("weekly");
        return new PumpPnl(Sane(monthly.Pnl), Sane(weekly.Pnl), monthly.Rank);
    }

    // All-time PnL across the user's pump.fun portfolio.
    public async Task<double?> GetPortfolioPnlAsync(string userId, CancellationToken ct)
    {
        using var doc = await GetAsync($"/user-portfolio/{Uri.EscapeDataString(userId)}", ct);
        if (doc == null || !doc.RootElement.TryGetProperty("summary", out var s) || s.ValueKind != JsonValueKind.Object) return null;
        return Sane(Num(s, "totalPnlUsd"));
    }

    // pump.fun occasionally reports impossible PnL (hundreds of billions): treat as unknown
    private static double? Sane(double? usd) => usd is double v && Math.Abs(v) < 1e9 ? v : null;

    private async Task<JsonDocument?> GetAsync(string path, CancellationToken ct)
    {
        var http = _httpFactory.CreateClient(HttpClientName);
        http.Timeout = TimeSpan.FromSeconds(20);
        for (var attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, Base + path);
                req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
                req.Headers.TryAddWithoutValidation("Accept", "application/json");
                using var res = await http.SendAsync(req, ct);
                if (res.StatusCode == HttpStatusCode.OK)
                    return JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
                if ((int)res.StatusCode is 400 or 401 or 403 or 404)
                    return null;
                await Task.Delay(res.StatusCode == HttpStatusCode.TooManyRequests ? 4000 * (attempt + 1) : 200 + Random.Shared.Next(400), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                if (attempt == 5) _logger.LogWarning(ex, "pump.fun lookup failed: {Path}", path);
                await Task.Delay(1000 * (attempt + 1), ct);
            }
        }
        return null;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(v.GetString()) ? v.GetString() : null;

    private static double? Num(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String when double.TryParse(v.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) => d,
            _ => null
        };
    }
}
