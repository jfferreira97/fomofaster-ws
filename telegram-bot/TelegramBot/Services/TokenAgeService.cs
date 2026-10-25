using System.Collections.Concurrent;
using System.Data;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TelegramBot.Data;
using TelegramBot.Models;

namespace TelegramBot.Services;

// How old a token is, for the "minimum token age" settings. Neither pump.fun's trade events nor FOMO's carry a creation time, so the age is
// the creation time of the token's EARLIEST pair on DexScreener (pairCreatedAt, to the millisecond; for a pump.fun token that is its launch).
//   - A token's age never changes, so once known it is kept for good: in memory and in the TokenAges table (raw SQL, like WebEvents).
//   - A token DexScreener has no pair for yet is "unknown": the caller treats it as brand new. That answer is only remembered for 20 s,
//     because the pair appears moments later and the real age is then available.
//   - A lookup that does not answer in time returns null (the caller lets the alert through) and keeps going in the background, so the
//     next trade of the same token already finds the answer in memory.
public sealed record TokenAge(DateTime? CreatedAt);

public class TokenAgeService
{
    private sealed record Entry(DateTime? CreatedAt, DateTime CheckedAt);

    private static readonly TimeSpan UnknownTtl = TimeSpan.FromSeconds(20);

    private readonly IServiceProvider _services;
    private readonly ILogger<TokenAgeService> _logger;
    private readonly ConcurrentDictionary<string, Entry> _cache = new();
    private readonly ConcurrentDictionary<string, Task<Entry?>> _inFlight = new();
    private readonly SemaphoreSlim _gate = new(6);
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(4) };
    private long _pausedUntilTicks;

    // for the dashboard check
    public long Hits, Misses, Looked, Unknown, TimedOut, Failed;

    public TokenAgeService(IServiceProvider services, ILogger<TokenAgeService> logger)
    {
        _services = services;
        _logger = logger;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
    }

    public int Cached => _cache.Count;

    // "6h", "3d", "2mo", "1y": the short form shown in the alert's link line. A token with no pair yet reads "new".
    public static string Short(TokenAge age)
    {
        if (age.CreatedAt is not DateTime created) return "new";
        var m = (DateTime.UtcNow - created).TotalMinutes;
        if (m < 1) return "<1m";
        if (m < 60) return $"{(int)m}m";
        if (m < 48 * 60) return $"{(int)(m / 60)}h";
        var d = m / 1440;
        if (d < 60) return $"{(int)d}d";
        if (d < 730) return $"{(int)(d / 30)}mo";
        return $"{(int)(d / 365)}y";
    }

    // Solana addresses are case-sensitive; EVM ones are not.
    private static string Key(Chain chain, string address) => $"{chain}:{(chain == Chain.SOL ? address : address.ToLowerInvariant())}";

    public async Task EnsureSchemaAsync()
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.ExecuteSqlRawAsync(@"CREATE TABLE IF NOT EXISTS ""TokenAges"" (
            ""Key"" TEXT NOT NULL PRIMARY KEY, ""Chain"" TEXT NOT NULL, ""Address"" TEXT NOT NULL,
            ""CreatedAt"" TEXT NOT NULL, ""CheckedAt"" TEXT NOT NULL)");
        var conn = db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open) await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT \"Key\", \"CreatedAt\" FROM \"TokenAges\"";
        using var r = await cmd.ExecuteReaderAsync();
        var n = 0;
        while (await r.ReadAsync())
        {
            if (DateTime.TryParse(r.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var created))
            { _cache[r.GetString(0)] = new Entry(created, DateTime.UtcNow); n++; }
        }
        _logger.LogInformation("TokenAgeService loaded {Count} known token age(s)", n);
    }

    // null = could not tell in time (do not filter). CreatedAt null = DexScreener has no pair yet (brand new).
    public async Task<TokenAge?> GetAgeAsync(Chain chain, string address, TimeSpan wait)
    {
        var key = Key(chain, address);
        if (_cache.TryGetValue(key, out var e) && (e.CreatedAt != null || DateTime.UtcNow - e.CheckedAt < UnknownTtl))
        {
            Interlocked.Increment(ref Hits);
            return new TokenAge(e.CreatedAt);
        }
        Interlocked.Increment(ref Misses);

        var task = _inFlight.GetOrAdd(key, _ => LookupAsync(chain, address, key));
        var done = await Task.WhenAny(task, Task.Delay(wait));
        if (done != task) { Interlocked.Increment(ref TimedOut); return null; }
        var res = await task;
        return res == null ? null : new TokenAge(res.CreatedAt);
    }

    private async Task<Entry?> LookupAsync(Chain chain, string address, string key)
    {
        try
        {
            if (DateTime.UtcNow.Ticks < Interlocked.Read(ref _pausedUntilTicks)) { Interlocked.Increment(ref Failed); return null; }
            await _gate.WaitAsync();
            try
            {
                Interlocked.Increment(ref Looked);
                using var resp = await _http.GetAsync($"https://api.dexscreener.com/latest/dex/tokens/{Uri.EscapeDataString(address)}");
                if ((int)resp.StatusCode == 429)
                {
                    Interlocked.Exchange(ref _pausedUntilTicks, DateTime.UtcNow.AddSeconds(30).Ticks);
                    _logger.LogWarning("DexScreener rate limit hit, token age lookups paused for 30 s");
                    Interlocked.Increment(ref Failed);
                    return null;
                }
                if (!resp.IsSuccessStatusCode) { Interlocked.Increment(ref Failed); return null; }

                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                var slug = ChainInfo.DexScreenerSlug(chain);
                long? earliest = null;
                if (doc.RootElement.TryGetProperty("pairs", out var pairs) && pairs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var p in pairs.EnumerateArray())
                    {
                        if (!p.TryGetProperty("chainId", out var c) || !string.Equals(c.GetString(), slug, StringComparison.OrdinalIgnoreCase)) continue;
                        if (p.TryGetProperty("pairCreatedAt", out var t) && t.ValueKind == JsonValueKind.Number && t.TryGetInt64(out var ms))
                            earliest = earliest is null ? ms : Math.Min(earliest.Value, ms);
                    }
                }

                var entry = new Entry(earliest is long m ? DateTimeOffset.FromUnixTimeMilliseconds(m).UtcDateTime : null, DateTime.UtcNow);
                _cache[key] = entry;
                if (entry.CreatedAt == null) Interlocked.Increment(ref Unknown);
                else _ = SaveAsync(key, chain, address, entry);
                return entry;
            }
            finally { _gate.Release(); }
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref Failed);
            _logger.LogDebug(ex, "Token age lookup failed for {Key}", key);
            return null;
        }
        finally { _inFlight.TryRemove(key, out _); }
    }

    private async Task SaveAsync(string key, Chain chain, string address, Entry e)
    {
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.ExecuteSqlRawAsync(
                "INSERT OR REPLACE INTO \"TokenAges\" (\"Key\",\"Chain\",\"Address\",\"CreatedAt\",\"CheckedAt\") VALUES ({0},{1},{2},{3},{4})",
                key, chain.ToString(), address, e.CreatedAt!.Value.ToString("yyyy-MM-dd HH:mm:ss.fff"), e.CheckedAt.ToString("yyyy-MM-dd HH:mm:ss"));
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Could not save a token age"); }
    }
}
