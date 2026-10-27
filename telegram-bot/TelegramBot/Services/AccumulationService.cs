using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using TelegramBot.Data;
using TelegramBot.Models;

namespace TelegramBot.Services;

// Which tokens the traders we track keep buying, DAY AFTER DAY, and not just on launch day. Built from the raw event tables, which are kept
// for months (FOMO trades from WsEvents, pump.fun trades from PumpEvents, the latter only since 19 Oct), not from the 8-day Notifications.
//   - The window is whole UTC days: today plus the N-1 before it.
//   - A day only counts when at least MinBuyersPerDay different traders bought that day, so one wallet buying all week is not "days".
//   - EVM addresses are lowercased (FOMO writes them lowercase, pump.fun checksummed) so one token is never split in two.
//   - Wrapped natives and stablecoins are left out here; tokenized majors are filtered by the caller, who knows the token's age.
// The heavy query runs on a temporary table and its result is kept for 5 minutes.
public sealed record AccumulationDay(string Day, int Buyers, int Buys, int Sells);
public sealed record AccumulationRow(
    string Chain, string Address, string Ticker, int SolidDays, int BuyDays, int Buyers, int Sellers, int Buys, int Sells, double BuyUsd, double SellUsd,
    string? FirstBuyAt, string? LastBuyAt, double? FirstPrice, double? LastPrice, int TopBuyerBuys, double TopBuyerPct, List<AccumulationDay> PerDay, string? Img = null);

// One trade of one token, for the bubble chart: who, on which platform, buy or sell, how many dollars, at what price and market cap.
public sealed record TradePoint(string At, string Trader, string Platform, bool Buy, double Usd, double? Price, double? Mcap, string? Img);

public class AccumulationService
{
    private const long SolanaId = 1399811149;
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);
    private readonly IServiceProvider _services;
    private readonly ILogger<AccumulationService> _logger;
    private readonly SemaphoreSlim _gate = new(1);
    private readonly Dictionary<string, (DateTime At, List<AccumulationRow> Rows)> _cache = new();

    public AccumulationService(IServiceProvider services, ILogger<AccumulationService> logger)
    {
        _services = services;
        _logger = logger;
    }

    public async Task<List<AccumulationRow>> GetAsync(int days, int minDays, int minBuyers, int minBuyersPerDay)
    {
        var key = $"{days}/{minDays}/{minBuyers}/{minBuyersPerDay}";
        await _gate.WaitAsync();
        try
        {
            if (_cache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < Ttl) return hit.Rows;
            var rows = await ComputeAsync(days, minDays, minBuyers, minBuyersPerDay);
            _cache[key] = (DateTime.UtcNow, rows);
            return rows;
        }
        finally { _gate.Release(); }
    }

    // Every tracked trade of one token inside the window. FOMO events carry the market cap; pump.fun's carry only the price, so the market
    // cap of those is the price times the token's supply, with the supply read from the FOMO events of the same token (market cap / price),
    // or 1 billion for a pump.fun token on Solana (that is the supply of every one of them).
    public async Task<(List<TradePoint> Points, double? Supply)> TradesAsync(Chain chain, string address, int days)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var conn = db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open) await conn.OpenAsync();
        var since = DateTime.UtcNow.Date.AddDays(-(Math.Clamp(days, 1, 60) - 1)).ToString("yyyy-MM-dd HH:mm:ss");
        var cid = (long)ChainInfo.NetworkIdOf(chain);
        var rows = await Q(conn, @"
            SELECT CreatedAt AS at, 'f:' || UserHandle, 'fomo', CASE WHEN Type = 'swap_buy' THEN 1 ELSE 0 END, CAST(UsdAmount AS REAL), CAST(Price AS REAL), CAST(MarketCap AS REAL), json_extract(RawJson,'$.profilePictureLink')
            FROM WsEvents WHERE Type IN ('swap_buy','swap_sell') AND NetworkId = $c AND lower(TokenAddress) = lower($a) AND CreatedAt >= $s
            UNION ALL
            SELECT CreatedAt, 'p:' || ActorHandle, 'pump', CASE WHEN json_extract(RawJson,'$.trade.isBuy') = 1 THEN 1 ELSE 0 END,
                   json_extract(RawJson,'$.trade.amountUsd'), json_extract(RawJson,'$.trade.priceUsd'), NULL, json_extract(RawJson,'$.author.profileImage')
            FROM PumpEvents WHERE Kind = 'trade' AND ChainId = $c AND lower(CoinMint) = lower($a) AND CreatedAt >= $s
            ORDER BY 1 LIMIT 5000", ("$c", cid), ("$a", address), ("$s", since));

        var ratios = rows.Where(r => r[6] != null && r[5] != null && D(r[5]) > 0 && D(r[6]) > 0).Select(r => D(r[6]) / D(r[5])).OrderBy(x => x).ToList();
        double? supply = ratios.Count > 0 ? ratios[ratios.Count / 2] : (chain == Chain.SOL && address.EndsWith("pump", StringComparison.OrdinalIgnoreCase) ? 1_000_000_000.0 : null);

        var points = rows.Select(r =>
        {
            var price = DN(r[5]);
            var mcap = DN(r[6]) ?? (supply is double sp && price is > 0 ? price * sp : null);
            return new TradePoint(((string)r[0]!).Replace(' ', 'T').TrimEnd('Z') + "Z", ((string)r[1]!)[2..], (string)r[2]!, Convert.ToInt32(r[3]) == 1, D(r[4]), price, mcap, r[7] as string);
        }).ToList();
        return (points, supply);
    }

    private static void P(DbCommand c, string name, object value)
    {
        var p = c.CreateParameter(); p.ParameterName = name; p.Value = value; c.Parameters.Add(p);
    }

    private static async Task<List<object?[]>> Q(DbConnection conn, string sql, params (string, object)[] ps)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) P(cmd, n, v);
        var rows = new List<object?[]>();
        using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            var row = new object?[r.FieldCount];
            for (var i = 0; i < r.FieldCount; i++) row[i] = r.IsDBNull(i) ? null : r.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    private static double D(object? v) => v is null ? 0 : Convert.ToDouble(v, CultureInfo.InvariantCulture);
    private static double? DN(object? v) => v is null ? null : Convert.ToDouble(v, CultureInfo.InvariantCulture);

    private async Task<List<AccumulationRow>> ComputeAsync(int days, int minDays, int minBuyers, int minBuyersPerDay)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var conn = db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open) await conn.OpenAsync();
        var since = DateTime.UtcNow.Date.AddDays(-(days - 1)).ToString("yyyy-MM-dd HH:mm:ss");

        // one flat list of trades from both platforms (temp table: it lives and dies with this connection)
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "CREATE TEMP TABLE IF NOT EXISTS acc_ev (cid INTEGER, addr TEXT, tk TEXT, day TEXT, at TEXT, trader TEXT, buy INTEGER, usd REAL, price REAL, img TEXT)";
            await cmd.ExecuteNonQueryAsync();
            cmd.CommandText = "DELETE FROM acc_ev";
            await cmd.ExecuteNonQueryAsync();
        }
        using (var cmd = conn.CreateCommand())
        {
            P(cmd, "$s", since);
            cmd.CommandText = $@"
                INSERT INTO acc_ev
                SELECT NetworkId, CASE WHEN NetworkId = {SolanaId} THEN TokenAddress ELSE lower(TokenAddress) END, Ticker, substr(CreatedAt,1,10), CreatedAt, 'f:' || UserHandle,
                       CASE WHEN Type = 'swap_buy' THEN 1 ELSE 0 END, CAST(UsdAmount AS REAL), CAST(Price AS REAL), json_extract(RawJson,'$.tokenImageUrl')
                FROM WsEvents WHERE Type IN ('swap_buy','swap_sell') AND CreatedAt >= $s AND TokenAddress IS NOT NULL AND UserHandle IS NOT NULL;
                INSERT INTO acc_ev
                SELECT ChainId, CASE WHEN ChainId = {SolanaId} THEN CoinMint ELSE lower(CoinMint) END, Symbol, substr(CreatedAt,1,10), CreatedAt, 'p:' || ActorHandle,
                       CASE WHEN json_extract(RawJson,'$.trade.isBuy') = 1 THEN 1 ELSE 0 END,
                       json_extract(RawJson,'$.trade.amountUsd'), json_extract(RawJson,'$.trade.priceUsd'), json_extract(RawJson,'$.coinImage')
                FROM PumpEvents WHERE Kind = 'trade' AND CreatedAt >= $s AND CoinMint IS NOT NULL AND ActorHandle IS NOT NULL;
                DELETE FROM acc_ev WHERE UPPER(tk) IN ('SOL','WSOL','WETH','WBNB','USDC','USDT','USDS','USD1','DAI','PYUSD')
                   OR addr IN ('So11111111111111111111111111111111111111112','EPjFWdd5AufqSSqeM2qN1xzybapC8G4wEGGkZwyTDt1v','Es9vMFrzaCERmJfrF4H2FYD4KCoNkY11McCe8BenwNYB');
                CREATE INDEX IF NOT EXISTS acc_ix ON acc_ev (cid, addr, at);";
            await cmd.ExecuteNonQueryAsync();
        }

        // per token and day: how many different traders bought (a day only counts with enough of them)
        var perDay = (await Q(conn, @"
            SELECT cid, addr, day, COUNT(DISTINCT CASE WHEN buy = 1 THEN trader END), SUM(buy), SUM(1 - buy) FROM acc_ev GROUP BY cid, addr, day"))
            .GroupBy(r => (Convert.ToInt64(r[0]), (string)r[1]!))
            .ToDictionary(g => g.Key, g => g.OrderBy(r => (string)r[2]!).Select(r => new AccumulationDay((string)r[2]!, Convert.ToInt32(r[3]), Convert.ToInt32(r[4]), Convert.ToInt32(r[5]))).ToList());

        // the token's own totals
        var tokens = await Q(conn, @"
            SELECT cid, addr, MAX(tk), COUNT(DISTINCT CASE WHEN buy = 1 THEN trader END), COUNT(DISTINCT CASE WHEN buy = 0 THEN trader END),
                   SUM(buy), SUM(1 - buy), SUM(CASE WHEN buy = 1 THEN usd ELSE 0 END), SUM(CASE WHEN buy = 0 THEN usd ELSE 0 END),
                   MIN(CASE WHEN buy = 1 THEN at END), MAX(CASE WHEN buy = 1 THEN at END), MAX(img)
            FROM acc_ev GROUP BY cid, addr HAVING SUM(buy) > 0 AND COUNT(DISTINCT CASE WHEN buy = 1 THEN trader END) >= $b", ("$b", minBuyers));

        var chosen = new List<(object?[] T, List<AccumulationDay> Days, int Solid)>();
        foreach (var t in tokens)
        {
            if (!perDay.TryGetValue((Convert.ToInt64(t[0]), (string)t[1]!), out var pd)) continue;
            var solid = pd.Count(d => d.Buyers >= minBuyersPerDay);
            if (solid >= minDays) chosen.Add((t, pd, solid));
        }

        // who dominates a token's buys: the share of the single biggest buyer
        var top = (await Q(conn, @"
            SELECT cid, addr, MAX(n) FROM (SELECT cid, addr, trader, COUNT(*) n FROM acc_ev WHERE buy = 1 GROUP BY cid, addr, trader) GROUP BY cid, addr"))
            .ToDictionary(r => (Convert.ToInt64(r[0]), (string)r[1]!), r => Convert.ToInt32(r[2]));

        var result = new List<AccumulationRow>();
        foreach (var (t, pd, solid) in chosen)
        {
            var cid = Convert.ToInt64(t[0]); var addr = (string)t[1]!;
            var chain = ChainInfo.FromNetworkId((int)cid);
            if (chain is null) continue;   // a chain we do not map: nothing to say about it yet
            var firstBuyAt = t[9] as string; var lastBuyAt = t[10] as string;
            var buys = Convert.ToInt32(t[5]);
            top.TryGetValue((cid, addr), out var topBuys);
            var fp = await Q(conn, "SELECT price FROM acc_ev WHERE cid = $c AND addr = $a AND buy = 1 AND at = $t AND price > 0 LIMIT 1", ("$c", cid), ("$a", addr), ("$t", firstBuyAt ?? ""));
            var lp = await Q(conn, "SELECT price FROM acc_ev WHERE cid = $c AND addr = $a AND price > 0 ORDER BY at DESC LIMIT 1", ("$c", cid), ("$a", addr));
            result.Add(new AccumulationRow(chain.Value.ToString(), addr, (t[2] as string) ?? "?", solid, pd.Count(d => d.Buys > 0), Convert.ToInt32(t[3]), Convert.ToInt32(t[4]), buys, Convert.ToInt32(t[6]),
                D(t[7]), D(t[8]), firstBuyAt, lastBuyAt, fp.Count > 0 ? DN(fp[0][0]) : null, lp.Count > 0 ? DN(lp[0][0]) : null,
                topBuys, buys > 0 ? Math.Round(100.0 * topBuys / buys, 0) : 0, pd, t[11] as string));
        }
        result = result.OrderByDescending(r => r.SolidDays).ThenByDescending(r => r.Buyers).ToList();

        using (var cmd = conn.CreateCommand()) { cmd.CommandText = "DROP TABLE IF EXISTS acc_ev"; await cmd.ExecuteNonQueryAsync(); }
        _logger.LogInformation("Accumulation view built: {Count} token(s) over {Days} day(s)", result.Count, days);
        return result;
    }
}
