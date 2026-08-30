using Microsoft.AspNetCore.Mvc;

namespace TelegramBot.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SidecarController : ControllerBase
{
    private readonly ILogger<SidecarController> _logger;

    public SidecarController(ILogger<SidecarController> logger)
    {
        _logger = logger;
    }

    [HttpPost("heartbeat")]
    public IActionResult Heartbeat([FromQuery] string source = "ws-sidecar")
    {
        _logger.LogInformation("Sidecar heartbeat received from {Source} at {Time}", source, DateTime.UtcNow);
        return Ok(new { received = true, serverTime = DateTime.UtcNow });
    }
}
