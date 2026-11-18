using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using TelegramBot.Services;

namespace TelegramBot.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SidecarController : ControllerBase
{
    private readonly ILogger<SidecarController> _logger;
    private readonly MonitorStore _monitor;

    public SidecarController(ILogger<SidecarController> logger, MonitorStore monitor)
    {
        _logger = logger;
        _monitor = monitor;
    }

    // The body is optional and read by hand: a sidecar from before 2026-11-18 posts nothing at all, and [FromBody] would turn
    // that (or a missing Content-Type) into a 415 and lose its heartbeat.
    private sealed record CookieBody(string? Name, string? Domain, double Expires);
    private sealed record StatusBody(string? State, DateTime? Since, string? Detail, List<CookieBody>? Cookies);
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    [HttpPost("heartbeat")]
    public async Task<IActionResult> Heartbeat([FromQuery] string source = "fomo-ws-sidecar")
    {
        _monitor.Beat(source);
        if (Request.ContentLength is > 0 and < 64_000)
        {
            try
            {
                var body = await JsonSerializer.DeserializeAsync<StatusBody>(Request.Body, Json);
                if (body?.State is string state)
                {
                    var cookies = (body.Cookies ?? new())
                        .Where(c => c != null && !string.IsNullOrEmpty(c.Name) && c.Expires > 0 && c.Expires < 32_503_680_000)   // before year 3000
                        .Select(c => new SidecarCookie(c.Name!, c.Domain ?? "", DateTime.UnixEpoch.AddSeconds(c.Expires)))
                        .ToList();
                    var since = body.Since is DateTime s ? s.ToUniversalTime() : (DateTime?)null;
                    _monitor.Report(source, new SidecarReport(DateTime.UtcNow, state, since, body.Detail, cookies));
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning("Unreadable heartbeat status from {Source}: {Error}", source, ex.Message);
            }
        }
        else if (Request.ContentLength is null or >= 64_000 && Request.ContentType?.Contains("json") == true)
            _logger.LogWarning("Heartbeat status from {Source} skipped: body length {Length}", source, Request.ContentLength);
        _logger.LogInformation("Sidecar heartbeat received from {Source} at {Time}", source, DateTime.UtcNow);
        return Ok(new { received = true, serverTime = DateTime.UtcNow });
    }
}
