using Microsoft.AspNetCore.Mvc;
using TelegramBot.Data;
using TelegramBot.Services;
using Microsoft.EntityFrameworkCore;

namespace TelegramBot.Controllers;

[ApiController]
[Route("api/[controller]")]
public class UsersController : ControllerBase
{
    private readonly IUserService _userService;
    private readonly ITelegramService _telegramService;
    private readonly ILogger<UsersController> _logger;
    private readonly AppDbContext _dbContext;

    public UsersController(IUserService userService, ITelegramService telegramService, ILogger<UsersController> logger, AppDbContext dbContext)
    {
        _userService = userService;
        _telegramService = telegramService;
        _logger = logger;
        _dbContext = dbContext;
    }

    [HttpGet]
    public async Task<IActionResult> GetAllUsers()
    {
        try
        {
            var users = await _userService.GetAllUsersAsync();

            // Get total traders count
            var totalTraders = await _dbContext.Traders.CountAsync();

            // Get trader counts for each user
            var userTraderCounts = await _dbContext.UserTraders
                .Where(ut => users.Select(u => u.Id).Contains(ut.UserId))
                .GroupBy(ut => ut.UserId)
                .Select(g => new { UserId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.UserId, x => x.Count);

            return Ok(new
            {
                status = "success",
                count = users.Count,
                users = users.Select(u => new
                {
                    chatId = u.ChatId,
                    username = u.Username,
                    firstName = u.FirstName,
                    joinedAt = u.JoinedAt,
                    isActive = u.IsActive,
                    autoFollowNewTraders = u.AutoFollowNewTraders,
                    hasHiddenTradersAccess = u.HasHiddenTradersAccess,
                    trackingCount = userTraderCounts.GetValueOrDefault(u.Id, 0),
                    totalTraders = totalTraders
                })
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting users");
            return StatusCode(500, new { status = "error", message = ex.Message });
        }
    }

    [HttpGet("{chatId}")]
    public async Task<IActionResult> GetUser(long chatId)
    {
        try
        {
            var user = await _userService.GetUserByChatIdAsync(chatId);
            if (user == null)
            {
                return NotFound(new { status = "error", message = "User not found" });
            }

            return Ok(new
            {
                status = "success",
                user = new
                {
                    chatId = user.ChatId,
                    username = user.Username,
                    firstName = user.FirstName,
                    joinedAt = user.JoinedAt,
                    isActive = user.IsActive
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting user");
            return StatusCode(500, new { status = "error", message = ex.Message });
        }
    }

    [HttpPost("add")]
    public async Task<IActionResult> AddUser([FromBody] AddUserRequest request)
    {
        try
        {
            var user = await _userService.AddOrUpdateUserAsync(
                request.ChatId,
                request.Username,
                request.FirstName
            );

            return Ok(new
            {
                status = "success",
                message = "User added/updated",
                user = new
                {
                    chatId = user.ChatId,
                    username = user.Username,
                    firstName = user.FirstName,
                    joinedAt = user.JoinedAt,
                    isActive = user.IsActive
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding user");
            return StatusCode(500, new { status = "error", message = ex.Message });
        }
    }

    [HttpPost("{chatId}/deactivate")]
    public async Task<IActionResult> DeactivateUser(long chatId)
    {
        try
        {
            await _userService.DeactivateUserAsync(chatId);
            return Ok(new { status = "success", message = "User deactivated" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deactivating user");
            return StatusCode(500, new { status = "error", message = ex.Message });
        }
    }

    [HttpPost("send-message")]
    public async Task<IActionResult> SendMessage([FromBody] SendMessageRequest request)
    {
        try
        {
            // Validate user exists and is active
            var user = await _userService.GetUserByChatIdAsync(request.ChatId);
            if (user == null)
            {
                return NotFound(new { status = "error", message = "User not found" });
            }

            if (!user.IsActive)
            {
                return BadRequest(new { status = "error", message = "User is not active" });
            }

            // Send plain text message (no ticker logic, no formatting)
            var success = await _telegramService.SendPlainMessageAsync(request.ChatId, request.Message);

            if (success)
            {
                _logger.LogInformation("Plain message sent to user {ChatId}", request.ChatId);
                return Ok(new { status = "success", message = "Message sent successfully" });
            }
            else
            {
                return StatusCode(500, new { status = "error", message = "Failed to send message" });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending message to user {ChatId}", request.ChatId);
            return StatusCode(500, new { status = "error", message = ex.Message });
        }
    }

    [HttpPost("broadcast-message")]
    public async Task<IActionResult> BroadcastMessage([FromBody] BroadcastMessageRequest request)
    {
        try
        {
            var users = await _userService.GetAllActiveUsersAsync();

            if (users.Count == 0)
            {
                return Ok(new { status = "success", message = "No active users to send to", sentCount = 0 });
            }

            int successCount = 0;
            int failCount = 0;

            foreach (var user in users)
            {
                var success = await _telegramService.SendPlainMessageAsync(user.ChatId, request.Message);
                if (success)
                {
                    successCount++;
                }
                else
                {
                    failCount++;
                }
            }

            _logger.LogInformation("Broadcast message sent to {Success}/{Total} users ({Failed} failed)",
                successCount, users.Count, failCount);

            return Ok(new
            {
                status = "success",
                message = $"Broadcast sent to {successCount} users",
                sentCount = successCount,
                failedCount = failCount,
                totalUsers = users.Count
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error broadcasting message to all users");
            return StatusCode(500, new { status = "error", message = ex.Message });
        }
    }

    [HttpPost("{chatId}/follow-all")]
    public async Task<IActionResult> FollowAllTraders(long chatId)
    {
        try
        {
            var user = await _userService.GetUserByChatIdAsync(chatId);
            if (user == null)
            {
                return NotFound(new { status = "error", message = "User not found" });
            }

            var traderService = HttpContext.RequestServices.GetRequiredService<ITraderService>();
            var followedCount = await traderService.FollowAllTradersAsync(user.Id);
            var allTraders = await traderService.GetAllTradersAsync();

            return Ok(new
            {
                status = "success",
                message = $"User now following all {allTraders.Count} traders",
                newFollows = followedCount,
                totalTraders = allTraders.Count
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error making user {ChatId} follow all traders", chatId);
            return StatusCode(500, new { status = "error", message = ex.Message });
        }
    }

    [HttpPost("{chatId}/unfollow-all")]
    public async Task<IActionResult> UnfollowAllTraders(long chatId)
    {
        try
        {
            var user = await _userService.GetUserByChatIdAsync(chatId);
            if (user == null)
            {
                return NotFound(new { status = "error", message = "User not found" });
            }

            var traderService = HttpContext.RequestServices.GetRequiredService<ITraderService>();
            var unfollowedCount = await traderService.UnfollowAllTradersAsync(user.Id);

            return Ok(new
            {
                status = "success",
                message = $"User unfollowed all traders",
                unfollowedCount = unfollowedCount
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error making user {ChatId} unfollow all traders", chatId);
            return StatusCode(500, new { status = "error", message = ex.Message });
        }
    }

    [HttpPost("{chatId}/toggle-auto-follow")]
    public async Task<IActionResult> ToggleAutoFollowNewTraders(long chatId, [FromBody] ToggleAutoFollowRequest request)
    {
        try
        {
            var user = await _userService.GetUserByChatIdAsync(chatId);
            if (user == null)
            {
                return NotFound(new { status = "error", message = "User not found" });
            }

            user.AutoFollowNewTraders = request.AutoFollowNewTraders;
            await _dbContext.SaveChangesAsync();

            return Ok(new
            {
                status = "success",
                message = $"Auto-follow new traders set to {request.AutoFollowNewTraders}",
                autoFollowNewTraders = user.AutoFollowNewTraders
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error toggling auto-follow for user {ChatId}", chatId);
            return StatusCode(500, new { status = "error", message = ex.Message });
        }
    }

    [HttpPost("{chatId}/toggle-hidden-access")]
    public async Task<IActionResult> ToggleHiddenAccess(long chatId, [FromBody] ToggleHiddenAccessRequest request)
    {
        try
        {
            var user = await _userService.GetUserByChatIdAsync(chatId);
            if (user == null)
                return NotFound(new { status = "error", message = "User not found" });

            user.HasHiddenTradersAccess = request.HasHiddenTradersAccess;
            await _dbContext.SaveChangesAsync();

            return Ok(new
            {
                status = "success",
                message = $"Hidden traders access set to {request.HasHiddenTradersAccess}",
                hasHiddenTradersAccess = user.HasHiddenTradersAccess
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error toggling hidden access for user {ChatId}", chatId);
            return StatusCode(500, new { status = "error", message = ex.Message });
        }
    }

    [HttpPost("{chatId}/follow-all-public")]
    public async Task<IActionResult> FollowAllPublicTraders(long chatId)
    {
        try
        {
            var user = await _userService.GetUserByChatIdAsync(chatId);
            if (user == null)
                return NotFound(new { status = "error", message = "User not found" });

            var traderService = HttpContext.RequestServices.GetRequiredService<ITraderService>();
            var followedCount = await traderService.FollowAllPublicTradersAsync(user.Id);

            return Ok(new
            {
                status = "success",
                message = $"User now following {followedCount} new public traders",
                newFollows = followedCount
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error making user {ChatId} follow all public traders", chatId);
            return StatusCode(500, new { status = "error", message = ex.Message });
        }
    }

    [HttpPost("{chatId}/follow-all-hidden")]
    public async Task<IActionResult> FollowAllHiddenTraders(long chatId)
    {
        try
        {
            var user = await _userService.GetUserByChatIdAsync(chatId);
            if (user == null)
                return NotFound(new { status = "error", message = "User not found" });

            var traderService = HttpContext.RequestServices.GetRequiredService<ITraderService>();
            var followedCount = await traderService.FollowAllHiddenTradersAsync(user.Id);

            return Ok(new
            {
                status = "success",
                message = $"User now following {followedCount} new hidden traders",
                newFollows = followedCount
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error making user {ChatId} follow all hidden traders", chatId);
            return StatusCode(500, new { status = "error", message = ex.Message });
        }
    }
}

public record AddUserRequest(long ChatId, string? Username, string? FirstName);
public record SendMessageRequest(long ChatId, string Message);
public record BroadcastMessageRequest(string Message);
public record ToggleAutoFollowRequest(bool AutoFollowNewTraders);
public record ToggleHiddenAccessRequest(bool HasHiddenTradersAccess);
