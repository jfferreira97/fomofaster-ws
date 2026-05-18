using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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

    [HttpGet("unhandled")]
    public async Task<IActionResult> GetUnhandled([FromQuery] int limit = 200)
    {
        var events = await _db.WsEvents
            .Where(e => !e.Handled)
            .OrderByDescending(e => e.ReceivedAt)
            .Take(limit)
            .Select(e => new {
                id          = e.Id,
                wsId        = e.WsId,
                type        = e.Type,
                receivedAt  = e.ReceivedAt,
                userHandle  = e.UserHandle,
                displayName = e.DisplayName,
                ticker      = e.Ticker,
                usdAmount   = e.UsdAmount,
                rawJson     = e.RawJson
            })
            .ToListAsync();

        return Ok(new { events });
    }

    [HttpPost("{id:int}/mark-handled")]
    public async Task<IActionResult> MarkHandled(int id)
    {
        var ev = await _db.WsEvents.FindAsync(id);
        if (ev is null) return NotFound();
        ev.Handled = true;
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    [HttpGet("milestones")]
    public async Task<IActionResult> GetMilestones([FromQuery] int limit = 1000)
    {
        var events = await _db.WsEvents
            .Where(e => e.Type == "user_trade_profit_milestone")
            .OrderByDescending(e => e.ReceivedAt)
            .Take(limit)
            .Select(e => new {
                id               = e.Id,
                receivedAt       = e.ReceivedAt,
                userHandle       = e.UserHandle,
                displayName      = e.DisplayName,
                ticker           = e.Ticker,
                tag              = e.Tag,
                totalPnlUsd      = e.TotalPnlUsd,
                totalPercentagePnl = e.TotalPercentagePnl,
                totalCostBasis   = e.TotalCostBasis,
                entryTime        = e.EntryTime,
                showAbsolutePnl  = e.ShowAbsolutePnl,
                tradeId          = e.TradeId,
                rawJson          = e.RawJson
            })
            .ToListAsync();

        return Ok(new { events });
    }

    [HttpPost]
    public async Task<IActionResult> Post([FromBody] JsonElement payload)
    {
        try
        {
            var body = payload.TryGetProperty("body", out var b) ? b : (JsonElement?)null;

            static string? Str(JsonElement el, string key) =>
                el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            static double? Num(JsonElement el, string key) =>
                el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
            static bool? Bool(JsonElement el, string key) =>
                el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True ? true
                : el.TryGetProperty(key, out var v2) && v2.ValueKind == JsonValueKind.False ? false : null;

            var wsId = Str(payload, "id");
            if (wsId is null)
                return BadRequest(new { error = "missing id" });

            if (await _db.WsEvents.AnyAsync(e => e.WsId == wsId))
                return Ok(new { accepted = false, reason = "duplicate" });

            var wsEvent = new WsEvent
            {
                WsId         = wsId,
                Type         = Str(payload, "type") ?? "unknown",
                UserId       = Str(payload, "userId"),
                UserHandle   = Str(payload, "userHandle") ?? (body.HasValue ? Str(body.Value, "userHandle") : null),
                DisplayName  = Str(payload, "displayName") ?? (body.HasValue ? Str(body.Value, "displayName") : null),
                TradeId      = Str(payload, "tradeId"),
                TokenAddress = Str(payload, "tokenAddress"),
                NetworkId    = payload.TryGetProperty("networkId", out var nid) && nid.ValueKind == JsonValueKind.Number ? nid.GetInt32() : null,
                Ticker       = Str(payload, "ticker") ?? (body.HasValue ? Str(body.Value, "ticker") : null),
                CreatedAt    = payload.TryGetProperty("createdAt", out var ca) && ca.ValueKind == JsonValueKind.String && DateTime.TryParse(ca.GetString(), out var caVal) ? caVal : null,
                ReceivedAt   = DateTime.UtcNow,
                Equity       = (decimal?)Num(payload, "equity"),
                Price        = (decimal?)(Num(payload, "price") ?? (body.HasValue ? Num(body.Value, "price") : null)),
                MarketCap    = (decimal?)(Num(payload, "marketCap") ?? (body.HasValue ? Num(body.Value, "marketCap") : null)),
                UsdAmount    = (decimal?)Num(payload, "usdAmount"),
                Tag              = body.HasValue ? Str(body.Value, "tag") : null,
                TotalCostBasis   = (decimal?)(body.HasValue ? Num(body.Value, "totalCostBasis") : null),
                TotalPnlUsd      = (decimal?)(body.HasValue ? Num(body.Value, "totalPnlUsd") : null),
                TotalPercentagePnl = (decimal?)(body.HasValue ? Num(body.Value, "totalPercentagePnl") : null),
                EntryTime        = body.HasValue && body.Value.TryGetProperty("entryTime", out var et) && et.ValueKind == JsonValueKind.String && DateTime.TryParse(et.GetString(), out var etVal) ? etVal : null,
                ShowAbsolutePnl  = body.HasValue ? Bool(body.Value, "showAbsolutePnl") : null,
                RawJson      = payload.GetRawText(),
                Handled      = Str(payload, "type") == "user_trade_profit_milestone",
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
