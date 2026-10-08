using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using TelegramBot.Services;

namespace TelegramBot.Controllers;

// POST /api/track  : public beacon from the landing site (proxied by Caddy; tiny, validated, rate limited).
// GET  /api/analytics/web : the Website tab of the admin dashboard (NOT exposed by Caddy).
[ApiController]
public class WebAnalyticsController : ControllerBase
{
    private static readonly HashSet<string> Types = new() { "view", "cta", "play", "sec" };
    private static readonly Regex PathRx = new(@"^/[A-Za-z0-9/_\-.]{0,78}\z", RegexOptions.Compiled);
    private static readonly Regex TokenRx = new(@"^[A-Za-z0-9_\-:.]{0,48}\z", RegexOptions.Compiled);
    private static readonly Regex VisitRx = new(@"^[A-Za-z0-9]{6,12}\z", RegexOptions.Compiled);
    private static readonly Regex BotRx = new(@"bot|crawl|spider|slurp|preview|facebookexternalhit|headless|python|curl|wget|monitor|uptime|lighthouse|pingdom|scrapy|httpclient|axios|node-fetch", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private const string OwnHost = "groupchat-bot.tech";

    private readonly WebAnalyticsService _analytics;
    private readonly ILogger<WebAnalyticsController> _logger;

    public WebAnalyticsController(WebAnalyticsService analytics, ILogger<WebAnalyticsController> logger)
    {
        _analytics = analytics;
        _logger = logger;
    }

    private static string Browser(string ua)
    {
        if (Regex.IsMatch(ua, "Telegram", RegexOptions.IgnoreCase)) return "Telegram in-app";
        if (Regex.IsMatch(ua, "FBAN|FBAV|Instagram", RegexOptions.IgnoreCase)) return "Facebook/Instagram in-app";
        if (Regex.IsMatch(ua, "Twitter", RegexOptions.IgnoreCase)) return "X in-app";
        if (Regex.IsMatch(ua, "Edg(e|A|iOS)?/", RegexOptions.IgnoreCase)) return "Edge";
        if (Regex.IsMatch(ua, "OPR/|Opera", RegexOptions.IgnoreCase)) return "Opera";
        if (Regex.IsMatch(ua, "SamsungBrowser", RegexOptions.IgnoreCase)) return "Samsung Internet";
        if (Regex.IsMatch(ua, "Firefox|FxiOS", RegexOptions.IgnoreCase)) return "Firefox";
        if (Regex.IsMatch(ua, "Chrome|CriOS", RegexOptions.IgnoreCase)) return "Chrome";
        if (Regex.IsMatch(ua, "Safari", RegexOptions.IgnoreCase)) return "Safari";
        return "Other";
    }

    private static string Os(string ua)
    {
        if (Regex.IsMatch(ua, "iPhone|iPad|iPod", RegexOptions.IgnoreCase)) return "iOS";
        if (Regex.IsMatch(ua, "Android", RegexOptions.IgnoreCase)) return "Android";
        if (Regex.IsMatch(ua, "Windows", RegexOptions.IgnoreCase)) return "Windows";
        if (Regex.IsMatch(ua, "Mac OS X|Macintosh", RegexOptions.IgnoreCase)) return "macOS";
        if (Regex.IsMatch(ua, "Linux|X11", RegexOptions.IgnoreCase)) return "Linux";
        return "Other";
    }

    [HttpPost("api/track")]
    public async Task<IActionResult> Track()
    {
        try
        {
            // Respect Do-Not-Track and Global Privacy Control: record nothing.
            if (Request.Headers["DNT"] == "1" || Request.Headers["Sec-GPC"] == "1") return NoContent();
            if (Request.ContentLength is > 2048) return StatusCode(413);

            var buf = new byte[2049];
            var n = 0;
            while (n < buf.Length)
            {
                var r = await Request.Body.ReadAsync(buf.AsMemory(n, buf.Length - n));
                if (r == 0) break;
                n += r;
            }
            if (n == 0 || n > 2048) return StatusCode(n == 0 ? 400 : 413);

            using var doc = JsonDocument.Parse(buf.AsMemory(0, n));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return BadRequest();
            string Get(string k) => root.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

            var type = Get("t");
            if (!Types.Contains(type)) return BadRequest();
            var path = Get("p");
            if (!PathRx.IsMatch(path)) path = "/";
            var label = Get("l");
            if (!TokenRx.IsMatch(label)) label = "";
            var utm = Get("u");
            if (!TokenRx.IsMatch(utm)) utm = "";
            var visit = Get("k");
            if (!VisitRx.IsMatch(visit)) visit = "";
            var clientInternal = root.TryGetProperty("i", out var iv) && (iv.ValueKind == JsonValueKind.Number ? iv.TryGetInt32(out var iNum) && iNum == 1 : iv.ValueKind == JsonValueKind.True);

            var refHost = "";
            if (Uri.TryCreate(Get("r"), UriKind.Absolute, out var ru) && (ru.Scheme == "http" || ru.Scheme == "https"))
            {
                refHost = ru.Host.ToLowerInvariant();
                if (refHost.StartsWith("www.")) refHost = refHost[4..];
                if (refHost == OwnHost || refHost.EndsWith("." + OwnHost)) refHost = "";   // internal navigation
                if (refHost.Length > 60) refHost = refHost[..60];
            }

            var ua = Request.Headers.UserAgent.ToString();
            var device = Regex.IsMatch(ua, "iPad|Tablet", RegexOptions.IgnoreCase) ? "tablet"
                : Regex.IsMatch(ua, "Mobi|Android|iPhone", RegexOptions.IgnoreCase) ? "mobile" : "desktop";
            var isBot = string.IsNullOrWhiteSpace(ua) || BotRx.IsMatch(ua);
            var lang = Request.Headers.AcceptLanguage.ToString().Split(',', ';')[0].Trim();
            if (lang.Length > 8 || !Regex.IsMatch(lang, @"^[A-Za-z\-]*\z")) lang = "";

            // Caddy puts the real client address in X-Forwarded-For. It is only believed when the request really came from the
            // local Caddy; direct callers cannot forge it.
            var remote = HttpContext.Connection.RemoteIpAddress;
            var fwd = Request.Headers["X-Forwarded-For"].ToString().Split(',')[0].Trim();
            var ip = remote != null && System.Net.IPAddress.IsLoopback(remote) && !string.IsNullOrEmpty(fwd) ? fwd : remote?.ToString() ?? "";
            if (ip.Length > 45) ip = "";
            var visitor = _analytics.VisitorHash(ip, ua);
            if (!_analytics.Allow(_analytics.VisitorHash(ip, ""))) return NoContent();   // rate limit by address only, so rotating the User-Agent does not help

            await _analytics.RecordEventAsync(new WebHit(type, path, label, refHost, utm, device, Browser(ua), Os(ua), lang, visitor, visit, ip, isBot, clientInternal));
            return NoContent();
        }
        catch (JsonException) { return BadRequest(); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "track failed");
            return NoContent();   // never surface errors to the public beacon
        }
    }

    [HttpGet("api/analytics/web")]
    public async Task<IActionResult> Summary([FromQuery] int days = 7, [FromQuery] bool includeInternal = false)
    {
        try { return Ok(await _analytics.GetSummaryAsync(days, includeInternal)); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "web analytics summary failed");
            return StatusCode(500, new { status = "error", message = "Failed to load analytics" });
        }
    }
}
