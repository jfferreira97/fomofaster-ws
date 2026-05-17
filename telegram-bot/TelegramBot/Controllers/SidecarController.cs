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
    public IActionResult Heartbeat()
    {
        _logger.LogInformation("WS sidecar heartbeat received at {Time}", DateTime.UtcNow);
        return Ok(new { received = true, serverTime = DateTime.UtcNow });
    }
}
