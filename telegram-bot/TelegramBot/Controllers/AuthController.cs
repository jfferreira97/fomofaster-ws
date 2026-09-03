using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;
using TelegramBot.Models;
using TelegramBot.Services;

namespace TelegramBot.Controllers;

// Backs the manage page's "Login with Telegram" flow. The widget itself proves the user's
// Telegram identity (Telegram signs the payload with our bot token); this controller only
// re-checks that signature server-side and, once satisfied, hands back an HttpOnly session
// cookie scoped to that ChatId. No password, no separate account system.
[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly TelegramSettings _settings;
    private readonly IUserService _userService;
    private readonly WebSessionService _sessionService;
    private readonly ITelegramService _telegramService;
    private readonly ILogger<AuthController> _logger;

    public AuthController(
        IOptions<TelegramSettings> settings,
        IUserService userService,
        WebSessionService sessionService,
        ITelegramService telegramService,
        ILogger<AuthController> logger)
    {
        _settings = settings.Value;
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

    [HttpPost("telegram-login")]
    public async Task<IActionResult> TelegramLogin([FromBody] TelegramLoginPayload payload)
    {
        if (!VerifyTelegramAuth(payload, _settings.BotToken))
        {
            _logger.LogWarning("Rejected Telegram login for id={Id}: signature or freshness check failed", payload.Id);
            return Unauthorized(new { status = "error", message = "Invalid or expired login" });
        }

        var user = await _userService.AddOrUpdateUserAsync(payload.Id, payload.Username, payload.FirstName);
        var token = await _sessionService.CreateTokenAsync(payload.Id);

        Response.Cookies.Append(WebSessionService.CookieName, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Expires = DateTimeOffset.UtcNow.AddDays(30),
            Path = "/"
        });

        _logger.LogInformation("Web login: ChatId={ChatId} Username={Username}", user.ChatId, user.Username);

        return Ok(new
        {
            status = "success",
            user = new { chatId = user.ChatId, username = user.Username, firstName = user.FirstName }
        });
    }

    [HttpPost("logout")]
    public IActionResult Logout()
    {
        Response.Cookies.Delete(WebSessionService.CookieName, new CookieOptions { Path = "/" });
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

    // Telegram Login Widget signature check: https://core.telegram.org/widgets/login#checking-authorization
    private static bool VerifyTelegramAuth(TelegramLoginPayload payload, string botToken)
    {
        if (string.IsNullOrEmpty(botToken) || string.IsNullOrEmpty(payload.Hash))
            return false;

        // Reject stale login attempts (widget popup left open, replayed payload, etc.)
        var authTime = DateTimeOffset.FromUnixTimeSeconds(payload.AuthDate);
        if (DateTimeOffset.UtcNow - authTime > TimeSpan.FromDays(1))
            return false;

        var fields = new SortedDictionary<string, string>(StringComparer.Ordinal);
        fields["id"] = payload.Id.ToString();
        fields["auth_date"] = payload.AuthDate.ToString();
        if (!string.IsNullOrEmpty(payload.FirstName)) fields["first_name"] = payload.FirstName;
        if (!string.IsNullOrEmpty(payload.LastName)) fields["last_name"] = payload.LastName;
        if (!string.IsNullOrEmpty(payload.Username)) fields["username"] = payload.Username;
        if (!string.IsNullOrEmpty(payload.PhotoUrl)) fields["photo_url"] = payload.PhotoUrl;

        var dataCheckString = string.Join('\n', fields.Select(kv => $"{kv.Key}={kv.Value}"));

        var secretKey = SHA256.HashData(Encoding.UTF8.GetBytes(botToken));
        var computedHash = HMACSHA256.HashData(secretKey, Encoding.UTF8.GetBytes(dataCheckString));
        var computedHex = Convert.ToHexString(computedHash).ToLowerInvariant();

        var providedBytes = Encoding.UTF8.GetBytes(payload.Hash.ToLowerInvariant());
        var computedBytes = Encoding.UTF8.GetBytes(computedHex);
        if (providedBytes.Length != computedBytes.Length)
            return false;

        return CryptographicOperations.FixedTimeEquals(providedBytes, computedBytes);
    }
}
