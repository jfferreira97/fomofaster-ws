using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using TelegramBot.Data;
using TelegramBot.Models;

namespace TelegramBot.Controllers;

[ApiController]
[Route("api/pump-events")]
public class PumpEventsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ILogger<PumpEventsController> _logger;

    public PumpEventsController(AppDbContext db, ILogger<PumpEventsController> logger)
    {
        _db = db;
        _logger = logger;
    }

    [HttpPost]
    public async Task<IActionResult> Post([FromBody] JsonElement payload)
    {
        try
        {
            static string? Str(JsonElement el, string key) =>
                el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

            var externalId = Str(payload, "externalId");
            var kind = Str(payload, "kind");
            if (externalId is null || kind is null)
                return BadRequest(new { error = "missing externalId or kind" });

            if (await _db.PumpEvents.AnyAsync(e => e.ExternalId == externalId))
                return Ok(new { accepted = false, reason = "duplicate" });

            var author = payload.TryGetProperty("author", out var a) ? a : (JsonElement?)null;

            var pumpEvent = new PumpEvent
            {
                ExternalId  = externalId,
                Kind        = kind,
                ActorUserId = author.HasValue ? Str(author.Value, "userId") : null,
                ActorHandle = author.HasValue ? Str(author.Value, "userName") : null,
                CoinMint    = Str(payload, "coinMint"),
                ChainId     = payload.TryGetProperty("chainId", out var cid) && cid.ValueKind == JsonValueKind.Number ? cid.GetInt32() : null,
                Symbol      = Str(payload, "symbol"),
                CreatedAt   = payload.TryGetProperty("createdAt", out var ca) && ca.ValueKind == JsonValueKind.String && DateTime.TryParse(ca.GetString(), out var caVal) ? caVal : null,
                ReceivedAt  = DateTime.UtcNow,
                RawJson     = payload.GetRawText(),
                Handled     = false,
            };

            _db.PumpEvents.Add(pumpEvent);
            await _db.SaveChangesAsync();

            return Ok(new { accepted = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving pump-event");
            return StatusCode(500, new { error = "internal error" });
        }
    }
}
