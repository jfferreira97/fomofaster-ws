using Microsoft.AspNetCore.Mvc;
using TelegramBot.Services;

namespace TelegramBot.Controllers;

// Session endpoints for the manage page. Sign-in happens in GET /manage (see Program.cs),
// which redeems the link token from the bot's /manage command.
[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly IUserService _userService;
    private readonly WebSessionService _sessionService;
    private readonly ITelegramService _telegramService;
    private readonly ILogger<AuthController> _logger;

    public AuthController(
        IUserService userService,
        WebSessionService sessionService,
        ITelegramService telegramService,
        ILogger<AuthController> logger)
    {
        _userService = userService;
        _sessionService = sessionService;
        _telegramService = telegramService;
        _logger = logger;
    }

    [HttpGet("bot-username")]
    public async Task<IActionResult> GetBotUsername()
    {
        var username = await _telegramService.GetBotUsernameAsync();
        if (username == null)
            return StatusCode(500, new { status = "error", message = "Bot not configured" });

        return Ok(new { status = "success", username });
    }

    [HttpPost("logout")]
    public IActionResult Logout()
    {
        Response.Cookies.Delete(WebSessionService.CookieName, new CookieOptions { Path = "/" });
        Response.Cookies.Delete(WebSessionService.LegacyCookieName, new CookieOptions { Path = "/" });
        return Ok(new { status = "success" });
    }

    [HttpGet("me")]
    public async Task<IActionResult> Me()
    {
        var chatId = await _sessionService.ValidateTokenAsync(Request.Cookies[WebSessionService.CookieName]);
        if (chatId == null)
            return Unauthorized(new { status = "error", message = "Not logged in" });

        var user = await _userService.GetUserByChatIdAsync(chatId.Value);
        if (user == null)
            return Unauthorized(new { status = "error", message = "Not logged in" });

        return Ok(new
        {
            status = "success",
            user = new { chatId = user.ChatId, username = user.Username, firstName = user.FirstName }
        });
    }

}
