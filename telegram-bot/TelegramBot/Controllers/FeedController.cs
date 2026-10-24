using Microsoft.AspNetCore.Mvc;
using TelegramBot.Services;

namespace TelegramBot.Controllers;

// SSE stream behind /feed. Identity comes from the /manage session cookie only; no cookie (or an
// invalid one) is the public demo: redacted and delayed by FeedService.DemoDelay.
[ApiController]
[Route("api/feed")]
public class FeedController : ControllerBase
{
    private readonly FeedService _feed;
    private readonly WebSessionService _sessions;
    private readonly UserActivityService _activity;

    public FeedController(FeedService feed, WebSessionService sessions, UserActivityService activity)
    {
        _feed = feed;
        _sessions = sessions;
        _activity = activity;
    }

    // Pictures for the logged-out preview, by opaque id (see FeedService.ProxyUrl).
    [HttpGet("img/{id}")]
    public async Task<IActionResult> Img(string id)
    {
        if (id.Length > 32) return NotFound();
        var img = await _feed.GetImageAsync(id);
        if (img is null) return NotFound();
        Response.Headers.CacheControl = "public, max-age=86400, immutable";
        return File(img.Value.Data, img.Value.Type);
    }

    [HttpGet("stream")]
    public async Task Stream(CancellationToken ct)
    {
        DataNotice.ApplyHeaders(Response);
        var chatId = await _sessions.ValidateTokenAsync(Request.Cookies[WebSessionService.CookieName]);
        _ = long.TryParse(Request.Headers["Last-Event-ID"].ToString(), out var last);
        // History of who uses the feed: when they open it and how long they stay (logged-in people only).
        var opened = DateTime.UtcNow;
        if (chatId is { } who) _activity.Log(who, "feed_open", Request.Headers.UserAgent.ToString().Contains("Mobi", StringComparison.OrdinalIgnoreCase) ? "mobile" : "desktop");
        try { await _feed.StreamAsync(Response, chatId, last, ct); }
        finally { if (chatId is { } who2) _activity.Log(who2, "feed_close", null, (int)(DateTime.UtcNow - opened).TotalSeconds); }
    }
}
