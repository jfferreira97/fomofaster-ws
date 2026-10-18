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

    public FeedController(FeedService feed, WebSessionService sessions)
    {
        _feed = feed;
        _sessions = sessions;
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
        await _feed.StreamAsync(Response, chatId, last, ct);
    }
}
