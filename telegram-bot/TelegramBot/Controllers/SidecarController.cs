using Microsoft.AspNetCore.Mvc;

namespace TelegramBot.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SidecarController : ControllerBase
{
    private readonly ILogger<SidecarController> _logger;
    private readonly TelegramBot.Services.MonitorStore _monitor;

    public SidecarController(ILogger<SidecarController> logger, TelegramBot.Services.MonitorStore monitor)
    {
        _logger = logger;
        _monitor = monitor;
    }

    [HttpPost("heartbeat")]
    public IActionResult Heartbeat([FromQuery] string source = "fomo-ws-sidecar")
    {
        _monitor.Beat(source);
        _logger.LogInformation("Sidecar heartbeat received from {Source} at {Time}", source, DateTime.UtcNow);
        return Ok(new { received = true, serverTime = DateTime.UtcNow });
    }
}
