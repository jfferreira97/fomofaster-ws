using Microsoft.AspNetCore.Mvc;
using TelegramBot.Services;

namespace TelegramBot.Controllers;

[ApiController]
[Route("api/[controller]")]
public class TradersController : ControllerBase
{
    private readonly ITraderService _traderService;
    private readonly IUserService _userService;
    private readonly ILogger<TradersController> _logger;

    public TradersController(
        ITraderService traderService,
        IUserService userService,
        ILogger<TradersController> logger)
    {
        _traderService = traderService;
        _userService = userService;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        try
        {
            var traders = await _traderService.GetAllTradersAsync();
            return Ok(traders);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting all traders");
            return StatusCode(500, new { status = "error", message = ex.Message });
        }
    }

    [HttpPost("follow")]
    public async Task<IActionResult> FollowTrader([FromBody] FollowRequest request)
    {
        try
        {
            var user = await _userService.GetUserByChatIdAsync(request.ChatId);
            if (user == null)
                return NotFound(new { status = "error", message = "User not found" });

            var success = await _traderService.FollowTraderAsync(user.Id, request.TraderId);
            return Ok(new { status = "success", message = success ? "Now following trader" : "Already following trader", followed = success });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error following trader");
            return StatusCode(500, new { status = "error", message = ex.Message });
        }
    }

    [HttpPost("unfollow")]
    public async Task<IActionResult> UnfollowTrader([FromBody] FollowRequest request)
    {
        try
        {
            var user = await _userService.GetUserByChatIdAsync(request.ChatId);
            if (user == null)
                return NotFound(new { status = "error", message = "User not found" });

            var success = await _traderService.UnfollowTraderAsync(user.Id, request.TraderId);
            return Ok(new { status = "success", message = success ? "Unfollowed trader" : "Was not following trader", unfollowed = success });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error unfollowing trader");
            return StatusCode(500, new { status = "error", message = ex.Message });
        }
    }

    [HttpPost("bulk-add")]
    public async Task<IActionResult> BulkAddTraders([FromBody] BulkAddTradersRequest request)
    {
        try
        {
            var added = new List<string>();

            foreach (var handle in request.Handles)
            {
                var cleanHandle = handle.TrimStart('@');
                await _traderService.AddOrUpdateTraderAsync(cleanHandle, request.IsHidden);
                added.Add(cleanHandle);
            }

            return Ok(new { status = "success", added = added.Count, total = request.Handles.Length, addedHandles = added });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error bulk adding traders");
            return StatusCode(500, new { status = "error", message = ex.Message });
        }
    }

    [HttpPost("{traderId}/toggle-hidden")]
    public async Task<IActionResult> ToggleHidden(int traderId, [FromBody] ToggleHiddenRequest request)
    {
        try
        {
            var success = await _traderService.SetTraderHiddenAsync(traderId, request.IsHidden);
            if (!success)
                return NotFound(new { status = "error", message = "Trader not found" });

            return Ok(new { status = "success" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error toggling hidden for trader {TraderId}", traderId);
            return StatusCode(500, new { status = "error", message = ex.Message });
        }
    }

    [HttpDelete("{traderId}")]
    public async Task<IActionResult> DeleteTrader(int traderId)
    {
        try
        {
            var success = await _traderService.DeleteTraderAsync(traderId);
            if (!success)
                return NotFound(new { status = "error", message = "Trader not found" });

            return Ok(new { status = "success", message = "Trader deleted" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting trader");
            return StatusCode(500, new { status = "error", message = ex.Message });
        }
    }

    [HttpDelete("by-handle/{handle}")]
    public async Task<IActionResult> DeleteTraderByHandle(string handle)
    {
        try
        {
            var cleanHandle = handle.TrimStart('@');
            var success = await _traderService.DeleteTraderByHandleAsync(cleanHandle);
            if (!success)
                return NotFound(new { status = "error", message = "Trader not found" });

            return Ok(new { status = "success", message = "Trader deleted" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting trader by handle");
            return StatusCode(500, new { status = "error", message = ex.Message });
        }
    }
}

public record FollowRequest(long ChatId, int TraderId);
public record BulkAddTradersRequest(string[] Handles, bool IsHidden = false);
public record ToggleHiddenRequest(bool IsHidden);