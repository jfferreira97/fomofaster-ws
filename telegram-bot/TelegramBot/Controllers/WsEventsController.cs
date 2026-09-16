using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Text.Json;
using TelegramBot.Data;
using TelegramBot.Models;

namespace TelegramBot.Controllers;

[ApiController]
[Route("api/ws-events")]
public class WsEventsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ILogger<WsEventsController> _logger;

    public WsEventsController(AppDbContext db, ILogger<WsEventsController> logger)
    {
        _db = db;
        _logger = logger;
    }

    [HttpPost]
    public async Task<IActionResult> Post([FromBody] JsonElement payload)
    {
        try
        {
            var body = payload.TryGetProperty("body", out var b) ? b : (JsonElement?)null;

            static string? Str(JsonElement el, string key) =>
                el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

            // Source timestamps arrive ISO-8601 with a trailing Z. Bare DateTime.TryParse
            // converts those to server LOCAL time, which on this box (UTC+1) stored every
            // CreatedAt an hour ahead of ReceivedAt and made source-to-ingest lag
            // unmeasurable. Force UTC so it shares a clock with ReceivedAt = DateTime.UtcNow.
            static DateTime? Utc(JsonElement el, string key) =>
                el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
                && DateTime.TryParse(v.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
                    ? parsed : null;

            static double? Num(JsonElement el, string key) =>
                el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
            static bool? Bool(JsonElement el, string key) =>
                el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True ? true
                : el.TryGetProperty(key, out var v2) && v2.ValueKind == JsonValueKind.False ? false : null;

            var wsId = Str(payload, "id");
            if (wsId is null)
                return BadRequest(new { error = "missing id" });

            var eventType = Str(payload, "type") ?? "unknown";

            // Retired 2026-09-04 at rr3332's request — never acted on, just archived.
            if (eventType == "user_trade_profit_milestone")
                return Ok(new { accepted = false, reason = "profit_milestone_not_stored" });

            if (await _db.WsEvents.AnyAsync(e => e.WsId == wsId))
                return Ok(new { accepted = false, reason = "duplicate" });

            var wsEvent = new WsEvent
            {
                WsId         = wsId,
                Type         = eventType,
                UserId       = Str(payload, "userId"),
                UserHandle   = Str(payload, "userHandle") ?? (body.HasValue ? Str(body.Value, "userHandle") : null),
                DisplayName  = Str(payload, "displayName") ?? (body.HasValue ? Str(body.Value, "displayName") : null),
                TradeId      = Str(payload, "tradeId"),
                TokenAddress = Str(payload, "tokenAddress"),
                NetworkId    = payload.TryGetProperty("networkId", out var nid) && nid.ValueKind == JsonValueKind.Number ? nid.GetInt32() : null,
                Ticker       = Str(payload, "ticker") ?? (body.HasValue ? Str(body.Value, "ticker") : null),
                CreatedAt    = Utc(payload, "createdAt"),
                ReceivedAt   = DateTime.UtcNow,
                Equity       = (decimal?)Num(payload, "equity"),
                Price        = (decimal?)(Num(payload, "price") ?? (body.HasValue ? Num(body.Value, "price") : null)),
                MarketCap    = (decimal?)(Num(payload, "marketCap") ?? (body.HasValue ? Num(body.Value, "marketCap") : null)),
                UsdAmount    = (decimal?)Num(payload, "usdAmount"),
                Tag              = body.HasValue ? Str(body.Value, "tag") : null,
                TotalCostBasis   = (decimal?)(body.HasValue ? Num(body.Value, "totalCostBasis") : null),
                TotalPnlUsd      = (decimal?)(body.HasValue ? Num(body.Value, "totalPnlUsd") : null),
                TotalPercentagePnl = (decimal?)(body.HasValue ? Num(body.Value, "totalPercentagePnl") : null),
                EntryTime        = body.HasValue ? Utc(body.Value, "entryTime") : null,
                ShowAbsolutePnl  = body.HasValue ? Bool(body.Value, "showAbsolutePnl") : null,
                RawJson      = payload.GetRawText(),
                Handled      = false,
            };

            _db.WsEvents.Add(wsEvent);
            await _db.SaveChangesAsync();

            return Ok(new { accepted = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving ws-event");
            return StatusCode(500, new { error = "internal error" });
        }
    }
}
