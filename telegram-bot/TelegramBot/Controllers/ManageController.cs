using Microsoft.AspNetCore.Mvc;
using TelegramBot.Data;
using TelegramBot.Models;
using TelegramBot.Services;

namespace TelegramBot.Controllers;

// Self-service API behind the manage page. Every action resolves identity from the signed
// session cookie only — never from a client-supplied chatId/userId — because, unlike the
// admin-facing Users/Traders controllers (which trust an operator-supplied ChatId), this one
// is reachable by any logged-in Telegram user and must only ever touch their own data.
[ApiController]
[Route("api/[controller]")]
public class ManageController : ControllerBase
{
    private readonly WebSessionService _sessionService;
    private readonly IUserService _userService;
    private readonly ITraderService _traderService;
    private readonly IChainSettingsService _chainSettingsService;
    private readonly AppDbContext _dbContext;

    public ManageController(
        WebSessionService sessionService,
        IUserService userService,
        ITraderService traderService,
        IChainSettingsService chainSettingsService,
        AppDbContext dbContext)
    {
        _sessionService = sessionService;
        _userService = userService;
        _traderService = traderService;
        _chainSettingsService = chainSettingsService;
        _dbContext = dbContext;
    }

    private async Task<Models.User?> GetCurrentUserAsync()
    {
        var chatId = await _sessionService.ValidateTokenAsync(Request.Cookies[WebSessionService.CookieName]);
        if (chatId == null)
            return null;

        return await _userService.GetUserByChatIdAsync(chatId.Value);
    }

    // The manage page is a subscriber perk, not just a logged-in-with-Telegram perk — gate
    // every action on it, not only "me", so there's no endpoint a non-subscriber can reach
    // by skipping straight to it. Distinct from the 401 case: a non-subscriber IS a valid,
    // authenticated user, just not a paying one, so the frontend needs to tell them to
    // subscribe rather than show the "log in with Telegram" screen again.
    private async Task<(Models.User? User, IActionResult? Error)> ResolveSubscriberAsync()
    {
        var user = await GetCurrentUserAsync();
        if (user == null)
            return (null, Unauthorized(new { status = "error", message = "Not logged in" }));

        if (!user.IsRegisteredNurse && !user.IsRN4L)
            return (null, StatusCode(403, new { status = "error", code = "subscription_required", message = "This page is for subscribers only. Use /subscribe in the bot to get access." }));

        return (user, null);
    }

    [HttpGet("me")]
    public async Task<IActionResult> GetMe()
    {
        var (user, error) = await ResolveSubscriberAsync();
        if (user == null) return error!;

        return Ok(new
        {
            status = "success",
            user = new { chatId = user.ChatId, username = user.Username, firstName = user.FirstName },
            settings = new
            {
                autoFollowFomoTraders = user.AutoFollowFomoTraders,
                autoFollowPumpTraders = user.AutoFollowPumpTraders,
                notifyFomoBuySell = user.NotifyFomoBuySell,
                notifyFomoThesis = user.NotifyFomoThesis,
                notifyPumpCallouts = user.NotifyPumpCallouts,
                pumpVerifiedOnly = user.PumpVerifiedOnly,
                notifyTrending = user.NotifyTrending,
                repeatWindowMinutes = user.RepeatWindowMinutes
            }
        });
    }

    [HttpPost("settings")]
    public async Task<IActionResult> UpdateSettings([FromBody] UpdateManageSettingsRequest request)
    {
        var (user, error) = await ResolveSubscriberAsync();
        if (user == null) return error!;

        // Flag-only, forward-looking (matches /autofollow and /settings in the bot): this
        // never backfills existing traders, so it can never silently undo an explicit
        // unfollow. Only FollowTraderAsync (an explicit per-trader action) does that.
        user.AutoFollowFomoTraders = request.AutoFollowFomoTraders;
        user.AutoFollowPumpTraders = request.AutoFollowPumpTraders;
        user.NotifyFomoBuySell = request.NotifyFomoBuySell;
        user.NotifyFomoThesis = request.NotifyFomoThesis;
        user.NotifyPumpCallouts = request.NotifyPumpCallouts;
        user.PumpVerifiedOnly = request.PumpVerifiedOnly;
        user.NotifyTrending = request.NotifyTrending;
        user.RepeatWindowMinutes = request.RepeatWindowMinutes;

        await _dbContext.SaveChangesAsync();
        return Ok(new { status = "success" });
    }

    [HttpGet("traders")]
    public async Task<IActionResult> GetTraders([FromQuery] string? platform)
    {
        var (user, error) = await ResolveSubscriberAsync();
        if (user == null) return error!;

        Platform? platformFilter = platform?.ToLowerInvariant() switch
        {
            "fomo" => Platform.Fomo,
            "pump" => Platform.Pump,
            _ => null // "all", missing, or unrecognized = no filter
        };

        var traders = await _traderService.GetBrowseListAsync(user.Id, platformFilter);

        return Ok(new
        {
            status = "success",
            traders = traders.Select(t => new
            {
                id = t.Id,
                handle = t.Handle,
                platform = t.Platform.ToString(),
                isPumpVerified = t.IsPumpVerified,
                isFollowing = t.IsFollowing,
                minValueUsd = t.MinValueUsd
            })
        });
    }

    [HttpPost("follow")]
    public async Task<IActionResult> Follow([FromBody] ManageTraderRequest request)
    {
        var (user, error) = await ResolveSubscriberAsync();
        if (user == null) return error!;

        var success = await _traderService.FollowTraderAsync(user.Id, request.TraderId);
        return Ok(new { status = "success", followed = success });
    }

    [HttpPost("unfollow")]
    public async Task<IActionResult> Unfollow([FromBody] ManageTraderRequest request)
    {
        var (user, error) = await ResolveSubscriberAsync();
        if (user == null) return error!;

        var success = await _traderService.UnfollowTraderAsync(user.Id, request.TraderId);
        return Ok(new { status = "success", unfollowed = success });
    }

    [HttpPost("follow-all")]
    public async Task<IActionResult> FollowAll()
    {
        var (user, error) = await ResolveSubscriberAsync();
        if (user == null) return error!;

        var count = await _traderService.FollowAllTradersAsync(user.Id);
        return Ok(new { status = "success", followedCount = count });
    }

    [HttpPost("unfollow-all")]
    public async Task<IActionResult> UnfollowAll()
    {
        var (user, error) = await ResolveSubscriberAsync();
        if (user == null) return error!;

        var count = await _traderService.UnfollowAllTradersAsync(user.Id);
        return Ok(new { status = "success", unfollowedCount = count });
    }

    [HttpPost("threshold")]
    public async Task<IActionResult> SetThreshold([FromBody] SetThresholdRequest request)
    {
        var (user, error) = await ResolveSubscriberAsync();
        if (user == null) return error!;

        var success = await _traderService.SetThresholdAsync(user.Id, request.TraderId, request.MinValueUsd);
        if (!success)
            return BadRequest(new { status = "error", message = "You must follow this trader before setting a threshold" });

        return Ok(new { status = "success" });
    }

    // Every chain the user has an explicit setting for, plus every other chain at its
    // implicit default (enabled, no minimum) — so callers always get a full roster.
    [HttpGet("chains")]
    public async Task<IActionResult> GetChains()
    {
        var (user, error) = await ResolveSubscriberAsync();
        if (user == null) return error!;

        var settings = _chainSettingsService.GetSettingsForUser(user.Id);

        var chains = Enum.GetValues<Chain>().Select(c =>
        {
            settings.TryGetValue(c, out var s);
            return new
            {
                chain = c.ToString(),
                isDisabled = s?.IsDisabled ?? false,
                minMarketCap = s?.MinMarketCap,
                trendingDisabled = s?.TrendingDisabled ?? false
            };
        });

        return Ok(new { status = "success", chains });
    }

    [HttpPost("chains/disabled")]
    public async Task<IActionResult> SetChainDisabled([FromBody] SetChainDisabledRequest request)
    {
        var (user, error) = await ResolveSubscriberAsync();
        if (user == null) return error!;

        await _chainSettingsService.SetDisabledAsync(user.Id, request.Chain, request.Disabled);
        return Ok(new { status = "success" });
    }

    [HttpPost("chains/minmarketcap")]
    public async Task<IActionResult> SetChainMinMarketCap([FromBody] SetChainMinMarketCapRequest request)
    {
        var (user, error) = await ResolveSubscriberAsync();
        if (user == null) return error!;

        await _chainSettingsService.SetMinMarketCapAsync(user.Id, request.Chain, request.MinMarketCap);
        return Ok(new { status = "success" });
    }

    [HttpPost("chains/trending")]
    public async Task<IActionResult> SetChainTrendingDisabled([FromBody] SetChainTrendingDisabledRequest request)
    {
        var (user, error) = await ResolveSubscriberAsync();
        if (user == null) return error!;

        await _chainSettingsService.SetTrendingDisabledAsync(user.Id, request.Chain, request.Disabled);
        return Ok(new { status = "success" });
    }
}

public record UpdateManageSettingsRequest(
    bool AutoFollowFomoTraders,
    bool AutoFollowPumpTraders,
    bool NotifyFomoBuySell,
    bool NotifyFomoThesis,
    bool NotifyPumpCallouts,
    bool PumpVerifiedOnly,
    bool NotifyTrending,
    int RepeatWindowMinutes);

public record ManageTraderRequest(int TraderId);
public record SetThresholdRequest(int TraderId, decimal? MinValueUsd);
public record SetChainDisabledRequest(Chain Chain, bool Disabled);
public record SetChainMinMarketCapRequest(Chain Chain, decimal? MinMarketCap);
public record SetChainTrendingDisabledRequest(Chain Chain, bool Disabled);
