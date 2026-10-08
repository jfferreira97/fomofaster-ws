using System.Data;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MaxMind.Db;
using Microsoft.EntityFrameworkCore;
using TelegramBot.Data;

namespace TelegramBot.Services;

// First-party, cookie-less analytics for the public landing site, plus attribution of the /start payload
// the website's buttons put on the Telegram link (t.me/GROUPCHAT_ALERTS_BOT?start=web_hero_u_x_v_ab12cd34).
//
// What is kept, per page event: time, page, referrer host, utm source, device / browser / OS, language, country (looked up from
// the IP in a local DB-IP database, no external call), the IP itself for 30 days (then blanked), and a visitor id that is only
// a hash of IP + user agent + the UTC day + a secret that changes on every restart. Nothing is stored on the visitor's device
// and Do-Not-Track / Global Privacy Control requests are not recorded. Visits from the owner (flagged by the site's own
// ?internal=1 switch, private or Tailscale addresses, or addresses listed in AppConfigs.WebInternalIps) are marked internal
// and kept out of the numbers by default. The tables are created with raw SQL (not EF migrations).
public sealed record WebHit(string Type, string Path, string Label, string RefHost, string Utm, string Device, string Browser, string Os,
    string Lang, string Visitor, string VisitToken, string Ip, bool IsBot, bool ClientInternal);

public class WebAnalyticsService
{
    private const string DefaultInternalIps = "38.247.144.173";   // this server's own public address (its own test visits)
    private const int IpRetentionDays = 30;

    private readonly IServiceProvider _services;
    private readonly ILogger<WebAnalyticsService> _logger;
    private readonly byte[] _secret = RandomNumberGenerator.GetBytes(32);
    private readonly Dictionary<string, (DateTime Hour, int Count)> _rate = new();
    private readonly object _rateLock = new();
    private (DateTime Hour, int Count) _global;   // all events, all visitors: a hard cap so the public beacon cannot flood the shared SQLite file

    private Reader? _geo;
    private bool _geoTried;
    private HashSet<string> _internalIps = new();
    private DateTime _internalAt = DateTime.MinValue;
    private DateTime _purgedAt = DateTime.MinValue;

    private static readonly Regex TokenSuffix = new(@"_v_([A-Za-z0-9]{6,12})$", RegexOptions.Compiled);

    public WebAnalyticsService(IServiceProvider services, ILogger<WebAnalyticsService> logger)
    {
        _services = services;
        _logger = logger;
    }

    public async Task EnsureSchemaAsync()
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.ExecuteSqlRawAsync(@"CREATE TABLE IF NOT EXISTS ""WebEvents"" (
            ""Id"" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            ""At"" TEXT NOT NULL, ""Day"" TEXT NOT NULL, ""Type"" TEXT NOT NULL, ""Path"" TEXT NOT NULL,
            ""Label"" TEXT NOT NULL, ""Ref"" TEXT NOT NULL, ""Utm"" TEXT NOT NULL, ""Device"" TEXT NOT NULL,
            ""Visitor"" TEXT NOT NULL, ""IsBot"" INTEGER NOT NULL)");
        var cols = (await QueryAsync(db, "PRAGMA table_info(\"WebEvents\")")).Select(r => S(r[1])).ToHashSet();
        foreach (var (name, ddl) in new[] {
            ("Ip", "TEXT NOT NULL DEFAULT ''"), ("Country", "TEXT NOT NULL DEFAULT ''"), ("Lang", "TEXT NOT NULL DEFAULT ''"),
            ("Browser", "TEXT NOT NULL DEFAULT ''"), ("Os", "TEXT NOT NULL DEFAULT ''"), ("IsInternal", "INTEGER NOT NULL DEFAULT 0"),
            ("VisitToken", "TEXT NOT NULL DEFAULT ''") })
        {
            if (!cols.Contains(name)) await db.Database.ExecuteSqlRawAsync($"ALTER TABLE \"WebEvents\" ADD COLUMN \"{name}\" {ddl}");
        }
        await db.Database.ExecuteSqlRawAsync(@"CREATE INDEX IF NOT EXISTS ""IX_WebEvents_Day_Type"" ON ""WebEvents"" (""Day"", ""Type"")");
        await db.Database.ExecuteSqlRawAsync(@"CREATE INDEX IF NOT EXISTS ""IX_WebEvents_VisitToken"" ON ""WebEvents"" (""VisitToken"")");
        await db.Database.ExecuteSqlRawAsync(@"CREATE TABLE IF NOT EXISTS ""BotStarts"" (
            ""Id"" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            ""At"" TEXT NOT NULL, ""ChatId"" INTEGER NOT NULL, ""Payload"" TEXT NOT NULL)");
        await db.Database.ExecuteSqlRawAsync(@"CREATE INDEX IF NOT EXISTS ""IX_BotStarts_At"" ON ""BotStarts"" (""At"")");
    }

    // ---------------- recording ----------------

    public string VisitorHash(string ip, string userAgent)
    {
        var day = DateTime.UtcNow.ToString("yyyy-MM-dd");
        using var h = new HMACSHA256(_secret);
        return Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes(day + "|" + ip + "|" + userAgent)))[..16].ToLowerInvariant();
    }

    // Max 120 events per address per hour and 6000 in total; beyond that events are dropped silently.
    public bool Allow(string key)
    {
        lock (_rateLock)
        {
            var hour = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, DateTime.UtcNow.Day, DateTime.UtcNow.Hour, 0, 0);
            if (_global.Hour != hour) _global = (hour, 0);
            if (++_global.Count > 6000) return false;
            if (_rate.Count > 20000) _rate.Clear();
            _rate.TryGetValue(key, out var cur);
            if (cur.Hour != hour) cur = (hour, 0);
            cur.Count++;
            _rate[key] = cur;
            return cur.Count <= 120;
        }
    }

    public string LookupCountry(string ip)
    {
        try
        {
            if (!_geoTried)
            {
                _geoTried = true;
                var path = Environment.GetEnvironmentVariable("GEO_DB") ?? "C:/apps/tools/geo/dbip-country.mmdb";
                if (File.Exists(path)) _geo = new Reader(path);
                else _logger.LogWarning("Country database not found at {Path}; countries will be blank", path);
            }
            if (_geo == null || !IPAddress.TryParse(ip, out var addr)) return "";
            var data = _geo.Find<Dictionary<string, object>>(addr);
            if (data != null && data.TryGetValue("country", out var c) && c is Dictionary<string, object> cd && cd.TryGetValue("iso_code", out var iso))
                return (iso?.ToString() ?? "").ToUpperInvariant();
        }
        catch (Exception ex) { _logger.LogDebug(ex, "country lookup failed"); }
        return "";
    }

    private static string CountryName(string iso)
    {
        if (string.IsNullOrEmpty(iso)) return "Unknown";
        try { return new System.Globalization.RegionInfo(iso).EnglishName; } catch { return iso; }
    }

    private static bool IsPrivateAddress(IPAddress a)
    {
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (IPAddress.IsLoopback(a)) return true;
        if (a.AddressFamily == AddressFamily.InterNetworkV6) return a.IsIPv6LinkLocal || a.IsIPv6SiteLocal || a.IsIPv6UniqueLocal;
        var b = a.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254)
            || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);   // 100.64/10 includes the Tailscale range
    }

    private async Task<bool> IsInternalIpAsync(string ip)
    {
        if (!IPAddress.TryParse(ip, out var a)) return false;
        if (IsPrivateAddress(a)) return true;
        if (DateTime.UtcNow - _internalAt > TimeSpan.FromSeconds(60))
        {
            try
            {
                using var scope = _services.CreateScope();
                var cfg = scope.ServiceProvider.GetRequiredService<AppConfigService>();
                var raw = await cfg.GetAsync("WebInternalIps") ?? "";
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var part in (DefaultInternalIps + "," + raw).Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)) set.Add(part.Trim());
                _internalIps = set;
            }
            catch (Exception ex) { _logger.LogDebug(ex, "could not read WebInternalIps"); }
            _internalAt = DateTime.UtcNow;
        }
        return _internalIps.Contains(ip) || (a.IsIPv4MappedToIPv6 && _internalIps.Contains(a.MapToIPv4().ToString()));
    }

    public async Task RecordEventAsync(WebHit h)
    {
        var now = DateTime.UtcNow;
        var country = LookupCountry(h.Ip);
        var isInternal = h.ClientInternal || await IsInternalIpAsync(h.Ip);
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var at = now.ToString("yyyy-MM-dd HH:mm:ss");
        var day = now.ToString("yyyy-MM-dd");
        await db.Database.ExecuteSqlInterpolatedAsync(
            $@"INSERT INTO ""WebEvents"" (""At"",""Day"",""Type"",""Path"",""Label"",""Ref"",""Utm"",""Device"",""Visitor"",""IsBot"",""Ip"",""Country"",""Lang"",""Browser"",""Os"",""IsInternal"",""VisitToken"")
               VALUES ({at},{day},{h.Type},{h.Path},{h.Label},{h.RefHost},{h.Utm},{h.Device},{h.Visitor},{(h.IsBot ? 1 : 0)},{h.Ip},{country},{h.Lang},{h.Browser},{h.Os},{(isInternal ? 1 : 0)},{h.VisitToken})");

        // IP addresses are only kept for IpRetentionDays: blank older ones (at most once an hour)
        if (now - _purgedAt > TimeSpan.FromHours(1))
        {
            _purgedAt = now;
            var cutoff = now.AddDays(-IpRetentionDays).ToString("yyyy-MM-dd HH:mm:ss");
            await db.Database.ExecuteSqlInterpolatedAsync($@"UPDATE ""WebEvents"" SET ""Ip"" = '' WHERE ""Ip"" <> '' AND ""At"" < {cutoff}");
        }
    }

    public async Task RecordStartAsync(long chatId, string? payload)
    {
        var clean = new string((payload ?? "").Where(c => char.IsLetterOrDigit(c) || c == '_' || c == '-').Take(64).ToArray());
        var at = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $@"INSERT INTO ""BotStarts"" (""At"",""ChatId"",""Payload"") VALUES ({at},{chatId},{clean})");
    }

    // ---------------- reading ----------------

    private static async Task<List<object?[]>> QueryAsync(AppDbContext db, string sql, params (string Name, object? Value)[] ps)
    {
        var conn = db.Database.GetDbConnection();
        var wasClosed = conn.State != ConnectionState.Open;
        if (wasClosed) await conn.OpenAsync();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (name, value) in ps)
            {
                var p = cmd.CreateParameter();
                p.ParameterName = name;
                p.Value = value ?? DBNull.Value;
                cmd.Parameters.Add(p);
            }
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
        finally { if (wasClosed) await conn.CloseAsync(); }
    }

    private static long L(object? o) => o == null ? 0 : Convert.ToInt64(o);
    private static string S(object? o) => o?.ToString() ?? "";
    private static double Pct(long a, long b) => b <= 0 ? 0 : Math.Round(100.0 * a / b, 1);

    private static string? TokenOf(string payload) { var m = TokenSuffix.Match(payload); return m.Success ? m.Groups[1].Value : null; }

    // "web_hero_u_x_v_ab12cd34" -> button "web_hero", source "x", visit token "ab12cd34". Payloads from outside the site have no web_/guide_ prefix.
    private static (string Cta, string Source, bool FromWebsite) ParsePayload(string payload)
    {
        if (string.IsNullOrEmpty(payload)) return ("(none)", "direct / organic", false);
        var core = TokenSuffix.Replace(payload, "");
        var fromWeb = core.StartsWith("web_") || core.StartsWith("guide_");
        var i = core.IndexOf("_u_", StringComparison.Ordinal);
        return i > 0 ? (core[..i], core[(i + 3)..], fromWeb) : (core, fromWeb ? "(no utm)" : "other link", fromWeb);
    }

    public async Task<object> GetSummaryAsync(int days, bool includeInternal = false)
    {
        days = Math.Clamp(days, 1, 365);
        var fromDay = DateTime.UtcNow.Date.AddDays(-(days - 1)).ToString("yyyy-MM-dd");
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var P = ("@from", (object?)fromDay);
        var W = "Day >= @from AND IsBot = 0" + (includeInternal ? "" : " AND IsInternal = 0");

        var t = (await QueryAsync(db, $@"SELECT
              SUM(CASE WHEN Type='view' THEN 1 ELSE 0 END),
              COUNT(DISTINCT CASE WHEN Type='view' THEN Visitor END),
              SUM(CASE WHEN Type='cta' THEN 1 ELSE 0 END),
              COUNT(DISTINCT CASE WHEN Type='cta' THEN Visitor END),
              SUM(CASE WHEN Type='play' THEN 1 ELSE 0 END),
              COUNT(DISTINCT CASE WHEN Type='play' THEN Visitor END)
            FROM WebEvents WHERE {W}", P)).FirstOrDefault() ?? new object?[6];
        long views = L(t[0]), visitors = L(t[1]), clicks = L(t[2]), clickers = L(t[3]), plays = L(t[4]), players = L(t[5]);
        var botViews = L((await QueryAsync(db, "SELECT COUNT(*) FROM WebEvents WHERE Day >= @from AND IsBot = 1 AND Type = 'view'", P))[0][0]);
        var own = (await QueryAsync(db, "SELECT COUNT(*), COUNT(DISTINCT Visitor) FROM WebEvents WHERE Day >= @from AND IsBot = 0 AND IsInternal = 1 AND Type = 'view'", P))[0];

        var daily = (await QueryAsync(db, $@"SELECT Day,
              SUM(CASE WHEN Type='view' THEN 1 ELSE 0 END),
              COUNT(DISTINCT CASE WHEN Type='view' THEN Visitor END),
              SUM(CASE WHEN Type='cta' THEN 1 ELSE 0 END)
            FROM WebEvents WHERE {W} GROUP BY Day", P))
            .ToDictionary(r => S(r[0]), r => (Views: L(r[1]), Visitors: L(r[2]), Clicks: L(r[3])));
        var startsDaily = (await QueryAsync(db, @"SELECT substr(At,1,10), COUNT(DISTINCT ChatId) FROM BotStarts
            WHERE substr(At,1,10) >= @from AND (Payload LIKE 'web!_%' ESCAPE '!' OR Payload LIKE 'guide!_%' ESCAPE '!') GROUP BY 1", P))
            .ToDictionary(r => S(r[0]), r => L(r[1]));
        var series = Enumerable.Range(0, days).Select(i =>
        {
            var d = DateTime.UtcNow.Date.AddDays(-(days - 1 - i)).ToString("yyyy-MM-dd");
            daily.TryGetValue(d, out var v);
            startsDaily.TryGetValue(d, out var s);
            return new { day = d, views = v.Views, visitors = v.Visitors, clicks = v.Clicks, starts = s };
        }).ToList();

        List<T> Top<T>(IEnumerable<T> src, int n = 12) => src.Take(n).ToList();
        var ctaRows = Top((await QueryAsync(db, $@"SELECT Label, COUNT(*), COUNT(DISTINCT Visitor) FROM WebEvents
            WHERE {W} AND Type = 'cta' GROUP BY Label ORDER BY 2 DESC", P)).Select(r => new { label = S(r[0]), clicks = L(r[1]), visitors = L(r[2]) }));
        var sections = (await QueryAsync(db, $@"SELECT Label, COUNT(DISTINCT Visitor) FROM WebEvents
            WHERE {W} AND Type = 'sec' GROUP BY Label ORDER BY 2 DESC", P)).Select(r => new { section = S(r[0]), visitors = L(r[1]) }).ToList();
        var refs = Top((await QueryAsync(db, $@"SELECT CASE WHEN Ref = '' THEN '(direct / none)' ELSE Ref END, COUNT(DISTINCT Visitor) FROM WebEvents
            WHERE {W} AND Type = 'view' GROUP BY 1 ORDER BY 2 DESC", P)).Select(r => new { referrer = S(r[0]), visitors = L(r[1]) }));
        var utms = Top((await QueryAsync(db, $@"SELECT Utm, COUNT(DISTINCT Visitor) FROM WebEvents
            WHERE {W} AND Type = 'view' AND Utm <> '' GROUP BY Utm ORDER BY 2 DESC", P)).Select(r => new { source = S(r[0]), visitors = L(r[1]) }));
        var pages = Top((await QueryAsync(db, $@"SELECT Path, COUNT(*), COUNT(DISTINCT Visitor) FROM WebEvents
            WHERE {W} AND Type = 'view' GROUP BY Path ORDER BY 3 DESC", P)).Select(r => new { path = S(r[0]), views = L(r[1]), visitors = L(r[2]) }));
        var devices = (await QueryAsync(db, $@"SELECT Device, COUNT(DISTINCT Visitor) FROM WebEvents
            WHERE {W} AND Type = 'view' GROUP BY Device ORDER BY 2 DESC", P)).Select(r => new { device = S(r[0]), visitors = L(r[1]) }).ToList();
        var browsers = Top((await QueryAsync(db, $@"SELECT CASE WHEN Browser = '' THEN 'Unknown' ELSE Browser END, COUNT(DISTINCT Visitor) FROM WebEvents
            WHERE {W} AND Type = 'view' GROUP BY 1 ORDER BY 2 DESC", P)).Select(r => new { browser = S(r[0]), visitors = L(r[1]) }), 8);
        var oses = Top((await QueryAsync(db, $@"SELECT CASE WHEN Os = '' THEN 'Unknown' ELSE Os END, COUNT(DISTINCT Visitor) FROM WebEvents
            WHERE {W} AND Type = 'view' GROUP BY 1 ORDER BY 2 DESC", P)).Select(r => new { os = S(r[0]), visitors = L(r[1]) }), 8);
        var langs = Top((await QueryAsync(db, $@"SELECT CASE WHEN Lang = '' THEN '?' ELSE Lang END, COUNT(DISTINCT Visitor) FROM WebEvents
            WHERE {W} AND Type = 'view' GROUP BY 1 ORDER BY 2 DESC", P)).Select(r => new { lang = S(r[0]), visitors = L(r[1]) }), 8);
        var countryRows = (await QueryAsync(db, $@"SELECT Country, COUNT(DISTINCT Visitor), COUNT(DISTINCT CASE WHEN Type='cta' THEN Visitor END) FROM WebEvents
            WHERE {W} AND Type IN ('view','cta') GROUP BY Country ORDER BY 2 DESC", P))
            .Select(r => (Code: S(r[0]), Visitors: L(r[1]), Clickers: L(r[2]))).ToList();

        // One row per visit (visitor + day): the latest visits, with the address they came from
        var recent = (await QueryAsync(db, $@"SELECT MIN(At), MAX(Ip), MAX(Country), MAX(Device), MAX(Browser), MAX(Os), MAX(Ref), MAX(Utm),
              MIN(CASE WHEN Type='view' THEN Path END), SUM(CASE WHEN Type='cta' THEN 1 ELSE 0 END), SUM(CASE WHEN Type='play' THEN 1 ELSE 0 END),
              COUNT(DISTINCT CASE WHEN Type='sec' THEN Label END), MAX(IsInternal), GROUP_CONCAT(DISTINCT CASE WHEN Type='cta' THEN Label END), MAX(Lang)
            FROM WebEvents WHERE {W} GROUP BY Visitor, Day ORDER BY MIN(At) DESC LIMIT 40", P))
            .Select(r => new
            {
                at = S(r[0]), ip = S(r[1]), country = S(r[2]), device = S(r[3]), browser = S(r[4]), os = S(r[5]), referrer = S(r[6]), utm = S(r[7]),
                page = S(r[8]), clicks = L(r[9]), plays = L(r[10]), sections = L(r[11]), isInternal = L(r[12]) == 1, buttons = S(r[13]), lang = S(r[14]),
            }).ToList();

        // Bot /start attribution: one row per (payload, chat) in range, joined to the user for "joined in range" and "paid".
        var startRows = await QueryAsync(db, @"SELECT b.Payload, b.ChatId,
              CASE WHEN u.JoinedAt >= @from THEN 1 ELSE 0 END,
              CASE WHEN u.RNExpiresAt IS NOT NULL AND u.IsRN4L = 0 THEN 1 ELSE 0 END,
              MIN(b.At), u.Username
            FROM BotStarts b LEFT JOIN Users u ON u.ChatId = b.ChatId
            WHERE substr(b.At,1,10) >= @from GROUP BY b.Payload, b.ChatId", P);
        // Starts that came from an internal (owner / test) visit are kept out unless asked for
        var internalTokens = includeInternal
            ? new HashSet<string>()
            : (await QueryAsync(db, "SELECT DISTINCT VisitToken FROM WebEvents WHERE VisitToken <> '' AND IsInternal = 1 AND Day >= @from", P)).Select(r => S(r[0])).ToHashSet();
        var parsed = startRows
            .Where(r => { var tk = TokenOf(S(r[0])); return tk == null || !internalTokens.Contains(tk); })
            .Select(r => (Row: r, P: ParsePayload(S(r[0])))).ToList();
        object Group(IEnumerable<(object?[] Row, (string Cta, string Source, bool FromWebsite) P)> rows, Func<(string Cta, string Source, bool FromWebsite), string> key) =>
            rows.GroupBy(x => key(x.P)).Select(g => new
            {
                key = g.Key,
                starts = g.Select(x => L(x.Row[1])).Distinct().Count(),
                newUsers = g.Where(x => L(x.Row[2]) == 1).Select(x => L(x.Row[1])).Distinct().Count(),
                paid = g.Where(x => L(x.Row[3]) == 1).Select(x => L(x.Row[1])).Distinct().Count(),
            }).OrderByDescending(x => x.starts).ToList();
        var web = parsed.Where(x => x.P.FromWebsite).ToList();
        long webStarts = web.Select(x => L(x.Row[1])).Distinct().Count();
        long webNew = web.Where(x => L(x.Row[2]) == 1).Select(x => L(x.Row[1])).Distinct().Count();
        long webPaid = web.Where(x => L(x.Row[3]) == 1).Select(x => L(x.Row[1])).Distinct().Count();
        long allStarts = parsed.Select(x => L(x.Row[1])).Distinct().Count();

        // Which visit became which trial: the visit token travels in the Telegram start payload
        var tokens = web.Select(x => TokenOf(S(x.Row[0]))).Where(x => x != null).Distinct().ToList();
        var visitInfo = new Dictionary<string, (string Country, string Device, string Browser, string Referrer, string Utm)>();
        if (tokens.Count > 0)
        {
            var inList = string.Join(",", tokens.Select(x => "'" + x + "'"));   // tokens are validated [A-Za-z0-9]{6,12}
            foreach (var r in await QueryAsync(db, $@"SELECT VisitToken, MAX(Country), MAX(Device), MAX(Browser), MAX(Ref), MAX(Utm) FROM WebEvents WHERE VisitToken IN ({inList}) GROUP BY VisitToken"))
                visitInfo[S(r[0])] = (S(r[1]), S(r[2]), S(r[3]), S(r[4]), S(r[5]));
        }
        var latestTrials = web.OrderByDescending(x => S(x.Row[4])).Take(30).Select(x =>
        {
            var tok = TokenOf(S(x.Row[0]));
            visitInfo.TryGetValue(tok ?? "", out var vi);
            return new
            {
                at = S(x.Row[4]), user = string.IsNullOrEmpty(S(x.Row[5])) ? "(no username)" : "@" + S(x.Row[5]), button = x.P.Cta, source = x.P.Source,
                country = vi.Country ?? "", device = vi.Device ?? "", browser = vi.Browser ?? "", referrer = vi.Referrer ?? "", isNew = L(x.Row[2]) == 1, paid = L(x.Row[3]) == 1,
            };
        }).ToList();
        var startsByCountry = latestTrials.Where(x => !string.IsNullOrEmpty(x.country)).GroupBy(x => x.country).ToDictionary(g => g.Key, g => g.Count());
        // (the dictionary above only covers the 30 latest trials; the per-country table says so)
        var countries = countryRows.Take(15).Select(c => new
        {
            code = c.Code, name = CountryName(c.Code), visitors = c.Visitors, clickers = c.Clickers,
            starts = startsByCountry.TryGetValue(c.Code, out var n) ? n : 0,
        }).ToList();

        return new
        {
            days,
            from = fromDay,
            includeInternal,
            totals = new { views, visitors, clicks, clickers, plays, players, botViewsIgnored = botViews },
            ownTraffic = new { views = L(own[0]), visitors = L(own[1]), included = includeInternal },
            funnel = new
            {
                visitors,
                clickers,
                webStarts,
                webNew,
                webPaid,
                visitorToClickPct = Pct(clickers, visitors),
                clickToStartPct = Pct(webStarts, clickers),
                startToPaidPct = Pct(webPaid, webStarts),
                visitorToStartPct = Pct(webStarts, visitors),
            },
            allBotStarts = allStarts,
            series,
            cta = ctaRows,
            sections,
            referrers = refs,
            utm = utms,
            pages,
            devices,
            browsers,
            oses,
            langs,
            countries,
            recent,
            latestTrials,
            startsByButton = Group(web, p => p.Cta),
            startsBySource = Group(parsed, p => p.Source),
        };
    }
}
