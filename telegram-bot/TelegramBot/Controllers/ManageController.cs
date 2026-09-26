using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;
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
    private readonly TelegramSettings _telegramSettings;
    private readonly TraderIntelService _intel;

    // Budget is per SUBMISSION, not per handle — see SuggestTrader.
    private const int SubmissionRateLimit = 24;
    private const int MaxSuggestionsPerSubmission = 25;
    private static readonly TimeSpan SuggestionWindow = TimeSpan.FromHours(24);

    public ManageController(
        WebSessionService sessionService,
        IUserService userService,
        ITraderService traderService,
        IChainSettingsService chainSettingsService,
        AppDbContext dbContext,
        IOptions<TelegramSettings> telegramSettings,
        TraderIntelService intel)
    {
        _sessionService = sessionService;
        _userService = userService;
        _traderService = traderService;
        _chainSettingsService = chainSettingsService;
        _dbContext = dbContext;
        _telegramSettings = telegramSettings.Value;
        _intel = intel;
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

        var onActiveTrial = user.TrialExpiresAt.HasValue && user.TrialExpiresAt.Value > DateTime.UtcNow;
        if (!user.IsRegisteredNurse && !user.IsRN4L && !onActiveTrial)
            return (null, StatusCode(403, new { status = "error", code = "subscription_required", message = "This page is for subscribers only. Use /subscribe in the bot to get access." }));

        return (user, null);
    }

    // Paying members (lifetime, or a monthly sub the payment poller hasn't expired) get the
    // trader ratings; trial users get the list, categories and follows, but none of the stats.
    // Enforced here, per request, from the session's own user — the page only mirrors it.
    private static bool HasRatings(Models.User user) => user.IsRN4L || user.IsRegisteredNurse;

    [HttpGet("me")]
    public async Task<IActionResult> GetMe()
    {
        var (user, error) = await ResolveSubscriberAsync();
        if (user == null) return error!;

        return Ok(new
        {
            status = "success",
            user = new { chatId = user.ChatId, username = user.Username, firstName = user.FirstName },
            access = HasRatings(user) ? "full" : "trial",
            settings = new
            {
                autoFollowFomoTraders = user.AutoFollowFomoTraders,
                autoFollowPumpTraders = user.AutoFollowPumpTraders,
                notifyFomoBuySell = user.NotifyFomoBuySell,
                notifyFomoThesis = user.NotifyFomoThesis,
                notifyPumpCallouts = user.NotifyPumpCallouts,
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
        var ratings = await _dbContext.TraderRatings.AsNoTracking().ToDictionaryAsync(r => r.TraderId);
        var traderRows = await _dbContext.Traders.AsNoTracking().ToDictionaryAsync(t => t.Id);
        var followedCategories = await FollowedCategoriesAsync(user.Id);
        var full = HasRatings(user);

        return Ok(new
        {
            status = "success",
            access = full ? "full" : "trial",
            followedCategories,
            traders = traders.Select(t => new
            {
                id = t.Id,
                handle = t.Handle,
                platform = t.Platform.ToString(),
                isFollowing = t.IsFollowing,
                minValueUsd = t.MinValueUsd,
                category = t.Category,
                isMuted = t.IsMuted,
                // getting their alerts through a followed category rather than a direct follow
                viaCategory = !t.IsFollowing && !t.IsMuted && followedCategories.Contains(t.Category),
                intel = full
                    ? (object)_intel.Compose(traderRows[t.Id], ratings.GetValueOrDefault(t.Id), withCalls: false)
                    : new { style = TraderCategories.Effective(t.Category) }
            })
        });
    }

    // One trader's full record for the profile card, recent calls included.
    [HttpGet("traders/{traderId:int}")]
    public async Task<IActionResult> GetTrader(int traderId)
    {
        var (user, error) = await ResolveSubscriberAsync();
        if (user == null) return error!;
        if (!HasRatings(user))
            return StatusCode(403, new { status = "error", code = "ratings_require_subscription", message = "Trader stats are for subscribers. Use /subscribe in the bot." });

        var trader = await _traderService.GetTraderByIdAsync(traderId);
        if (trader == null)
            return NotFound(new { status = "error", message = "No such trader" });

        var follow = await _dbContext.UserTraders.FirstOrDefaultAsync(ut => ut.UserId == user.Id && ut.TraderId == traderId);
        var rating = await _dbContext.TraderRatings.AsNoTracking().FirstOrDefaultAsync(r => r.TraderId == traderId);
        var category = TraderCategories.Effective(trader.Category);
        var catFollow = await _dbContext.UserCategoryFollows.FirstOrDefaultAsync(f => f.UserId == user.Id && f.Category == category);
        var excludedAt = (await _dbContext.TraderFollowExclusions.FirstOrDefaultAsync(e => e.UserId == user.Id && e.TraderId == traderId))?.ExcludedAt;
        var isMuted = catFollow != null && TraderService.IsMutedForCategory(excludedAt, catFollow.FollowedAt);
        var viaCategory = follow == null && catFollow != null && !isMuted;
        return Ok(new
        {
            status = "success",
            trader = new
            {
                id = trader.Id,
                handle = trader.Handle,
                platform = trader.Platform.ToString(),
                isFollowing = follow != null,
                minValueUsd = follow?.MinValueUsd,
                category,
                isMuted,
                viaCategory,
                firstSeenAt = trader.FirstSeenAt,
                intel = _intel.Compose(trader, rating, withCalls: true)
            }
        });
    }

    [HttpPost("follow")]
    public async Task<IActionResult> Follow([FromBody] ManageTraderRequest request)
    {
        var (user, error) = await ResolveSubscriberAsync();
        if (user == null) return error!;

        // FollowTraderAsync inserts straight into UserTraders, so an unknown id hits the
        // foreign key and surfaces as an unhandled DbUpdateException. Unfollow and threshold
        // already fail cleanly on a missing trader; match them.
        if (await _traderService.GetTraderByIdAsync(request.TraderId) is null)
            return NotFound(new { status = "error", message = "No such trader" });

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
        // "Unfollow all" means silence: category subscriptions go too, not just direct follows.
        await _dbContext.UserCategoryFollows.Where(f => f.UserId == user.Id).ExecuteDeleteAsync();
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

    // Trader categories with how many traders are in each right now, roughly how many alerts
    // a day they add up to, and whether this user follows the category.
    [HttpGet("categories")]
    public async Task<IActionResult> GetCategories()
    {
        var (user, error) = await ResolveSubscriberAsync();
        if (user == null) return error!;

        var followed = await FollowedCategoriesAsync(user.Id);
        var full = HasRatings(user);
        var traders = await _dbContext.Traders.AsNoTracking()
            .Select(t => new { t.Id, t.Category, t.Platform })
            .ToListAsync();
        var ratings = await _dbContext.TraderRatings.AsNoTracking().ToDictionaryAsync(r => r.TraderId);
        var perDay = ratings.ToDictionary(r => r.Key, r => r.Value.AlertsPerDay);
        var scoreRank = ratings.ToDictionary(r => r.Key, r => TraderCategorizerService.ParseStats(r.Value.StatsJson).ScoreRank);
        var lastRated = await _dbContext.TraderRatings.AsNoTracking().MaxAsync(r => (DateTime?)r.ComputedAt);
        var byCategory = traders.GroupBy(t => TraderCategories.Effective(t.Category)).ToDictionary(g => g.Key, g => g.ToList());

        return Ok(new
        {
            status = "success",
            ratedAt = lastRated is DateTime d ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : (DateTime?)null,
            categories = TraderCategories.All.Select(c =>
            {
                var members = byCategory.GetValueOrDefault(c.Id) ?? new();
                return new
                {
                    id = c.Id,
                    label = c.Label,
                    emoji = c.Emoji,
                    description = c.Description,
                    followed = followed.Contains(c.Id),
                    followable = TraderCategories.IsFollowable(c.Id),
                    traderCount = members.Count,
                    fomoCount = members.Count(m => m.Platform == Platform.Fomo),
                    pumpCount = members.Count(m => m.Platform == Platform.Pump),
                    alertsPerDay = full ? Math.Round(members.Sum(m => perDay.GetValueOrDefault(m.Id)), 1) : (double?)null,
                    // average 0-100 score of the members that have one
                    avgScore = !full ? null : members.Select(m => scoreRank.GetValueOrDefault(m.Id)).Where(v => v != null).Select(v => (double)v!.Value).DefaultIfEmpty(double.NaN).Average() is var avg && !double.IsNaN(avg) ? (int?)Math.Round(avg) : null
                };
            })
        });
    }

    // Follow or unfollow a whole category. Live: alerts come from whoever is in it at the time,
    // nothing is copied into the user's own follow list.
    [HttpPost("categories")]
    public async Task<IActionResult> SetCategoryFollow([FromBody] SetCategoryFollowRequest request)
    {
        var (user, error) = await ResolveSubscriberAsync();
        if (user == null) return error!;

        if (!TraderCategories.IsValid(request.Category))
            return BadRequest(new { status = "error", message = "Unknown category" });
        if (request.Follow && !TraderCategories.IsFollowable(request.Category))
            return BadRequest(new { status = "error", message = "That category can't be followed as a group" });

        var existing = await _dbContext.UserCategoryFollows.FirstOrDefaultAsync(f => f.UserId == user.Id && f.Category == request.Category);
        if (request.Follow && existing == null)
            _dbContext.UserCategoryFollows.Add(new UserCategoryFollow { UserId = user.Id, Category = request.Category, FollowedAt = DateTime.UtcNow });
        else if (!request.Follow && existing != null)
            _dbContext.UserCategoryFollows.Remove(existing);
        await _dbContext.SaveChangesAsync();

        return Ok(new { status = "success", followedCategories = await FollowedCategoriesAsync(user.Id) });
    }

    private async Task<HashSet<string>> FollowedCategoriesAsync(int userId) =>
        (await _dbContext.UserCategoryFollows.Where(f => f.UserId == userId).Select(f => f.Category).ToListAsync()).ToHashSet();

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

    // Belt-and-braces alongside allowIntegerValues: false in Program.cs. GetOrCreateAsync
    // will happily insert a row for any Chain value it's handed, and those rows are
    // invisible in the UI (GetChains iterates the enum), so they'd accumulate unnoticed.
    private static bool IsValidChain(Chain chain) => Enum.IsDefined(chain);

    [HttpPost("chains/disabled")]
    public async Task<IActionResult> SetChainDisabled([FromBody] SetChainDisabledRequest request)
    {
        var (user, error) = await ResolveSubscriberAsync();
        if (user == null) return error!;

        if (!IsValidChain(request.Chain))
            return BadRequest(new { status = "error", message = "Unknown chain" });

        await _chainSettingsService.SetDisabledAsync(user.Id, request.Chain, request.Disabled);
        return Ok(new { status = "success" });
    }

    [HttpPost("chains/minmarketcap")]
    public async Task<IActionResult> SetChainMinMarketCap([FromBody] SetChainMinMarketCapRequest request)
    {
        var (user, error) = await ResolveSubscriberAsync();
        if (user == null) return error!;

        if (!IsValidChain(request.Chain))
            return BadRequest(new { status = "error", message = "Unknown chain" });

        await _chainSettingsService.SetMinMarketCapAsync(user.Id, request.Chain, request.MinMarketCap);
        return Ok(new { status = "success" });
    }

    [HttpPost("chains/trending")]
    public async Task<IActionResult> SetChainTrendingDisabled([FromBody] SetChainTrendingDisabledRequest request)
    {
        var (user, error) = await ResolveSubscriberAsync();
        if (user == null) return error!;

        if (!IsValidChain(request.Chain))
            return BadRequest(new { status = "error", message = "Unknown chain" });

        await _chainSettingsService.SetTrendingDisabledAsync(user.Id, request.Chain, request.Disabled);
        return Ok(new { status = "success" });
    }

    // One submission may carry many handles and costs a single unit of the daily budget,
    // so filling twenty rows at once is not twenty times more expensive than filling one.
    // Rows are judged individually: bad ones come back annotated, good ones still land.
    [HttpPost("suggest-trader")]
    public async Task<IActionResult> SuggestTrader([FromBody] SuggestTraderRequest request)
    {
        var (user, error) = await ResolveSubscriberAsync();
        if (user == null) return error!;

        var items = request.Items ?? new List<SuggestTraderItem>();
        if (items.Count == 0)
            return BadRequest(new { status = "error", message = "Enter at least one trader handle" });
        if (items.Count > MaxSuggestionsPerSubmission)
            return BadRequest(new { status = "error", message = $"Up to {MaxSuggestionsPerSubmission} traders per submission." });

        var windowStart = DateTime.UtcNow - SuggestionWindow;
        var recentSubmissions = await _dbContext.SuggestedTraders
            .Where(s => s.UserId == user.Id && s.CreatedAt >= windowStart)
            .Select(s => s.BatchId)
            .Distinct()
            .CountAsync();
        if (recentSubmissions >= SubmissionRateLimit)
            return StatusCode(429, new { status = "error", code = "rate_limited", message = $"You can send up to {SubmissionRateLimit} suggestion batches per day — try again later." });

        var batchId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var results = new List<object>();
        var accepted = new List<(string Handle, Platform Platform)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var handle = item.Handle?.Trim().TrimStart('@');

            if (!Enum.IsDefined(item.Platform))
            {
                results.Add(new { index = i, handle, ok = false, code = "invalid_platform", message = "Unknown platform." });
                continue;
            }
            if (string.IsNullOrWhiteSpace(handle) || handle.Length > 100)
            {
                results.Add(new { index = i, handle, ok = false, code = "invalid", message = "Not a valid handle." });
                continue;
            }
            if (!seen.Add($"{item.Platform}:{handle}"))
            {
                results.Add(new { index = i, handle, ok = false, code = "duplicate", message = "Listed twice in this batch." });
                continue;
            }
            if (await _traderService.GetTraderByHandleIgnoreCaseAsync(handle, item.Platform) != null)
            {
                results.Add(new { index = i, handle, ok = false, code = "already_tracked", message = "Already tracked — follow them from the list instead." });
                continue;
            }

            _dbContext.SuggestedTraders.Add(new SuggestedTrader
            {
                UserId = user.Id,
                Handle = handle,
                Platform = item.Platform,
                CreatedAt = now,
                BatchId = batchId
            });
            accepted.Add((handle, item.Platform));
            results.Add(new { index = i, handle, ok = true, code = "sent", message = (string?)null });
        }

        // Nothing usable means nothing was stored, so the budget is untouched — a batch of
        // typos shouldn't burn one of the day's submissions.
        if (accepted.Count == 0)
            return Ok(new { status = "success", accepted = 0, remaining = SubmissionRateLimit - recentSubmissions, results });

        await _dbContext.SaveChangesAsync();
        await NotifyOwnerOfSuggestionsAsync(user, accepted);

        return Ok(new
        {
            status = "success",
            accepted = accepted.Count,
            remaining = SubmissionRateLimit - recentSubmissions - 1,
            results
        });
    }

    // Reuses the same admin-bot DM channel HandleFreeTextAsync already forwards non-command
    // messages through — one place the owner checks, not a second notification surface.
    // One DM per submission, not per handle — a 20-row batch used to mean 20 messages.
    private async Task NotifyOwnerOfSuggestionsAsync(Models.User user, List<(string Handle, Platform Platform)> suggestions)
    {
        if (string.IsNullOrEmpty(_telegramSettings.AdminBotToken))
            return;

        var owner = await _dbContext.Users.FindAsync(_telegramSettings.OwnerUserId);
        if (owner == null)
            return;

        // HTML, not Markdown: trader handles are full of underscores, and legacy Markdown
        // renders the backslash escapes for them literally ("zz\_batch\_test\_a").
        static string Esc(string text) =>
            text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

        static string ProfileUrl(string handle, Platform platform) => platform == Platform.Pump
            ? $"https://pump.fun/profile/{Uri.EscapeDataString(handle)}"
            : $"https://fomo.family/profile/{Uri.EscapeDataString(handle)}";

        // @username auto-links in Telegram's own rendering when present; tg://user deep-links
        // work even without one, so a requester is always clickable either way.
        var requester = !string.IsNullOrEmpty(user.Username)
            ? $"@{Esc(user.Username)}"
            : $"<a href=\"tg://user?id={user.ChatId}\">{Esc(user.FirstName ?? "a user")}</a>";

        var lines = suggestions.Select(s =>
            $"• <a href=\"{Esc(ProfileUrl(s.Handle, s.Platform))}\">{Esc(s.Handle)}</a> on <b>{s.Platform}</b>");
        var heading = suggestions.Count == 1 ? "Trader suggestion" : $"Trader suggestions ({suggestions.Count})";
        var text = $"📬 <b>{Esc(heading)}</b>\n\nfrom {requester}\n\n{string.Join("\n", lines)}";

        try
        {
            var adminBot = new TelegramBotClient(_telegramSettings.AdminBotToken);
            await adminBot.SendTextMessageAsync(
                chatId: owner.ChatId,
                text: text,
                parseMode: ParseMode.Html,
                disableWebPagePreview: true
            );
        }
        catch
        {
            // Suggestion is already saved — a failed DM shouldn't fail the request.
        }
    }
}

public record UpdateManageSettingsRequest(
    bool AutoFollowFomoTraders,
    bool AutoFollowPumpTraders,
    bool NotifyFomoBuySell,
    bool NotifyFomoThesis,
    bool NotifyPumpCallouts,
    bool NotifyTrending,
    int RepeatWindowMinutes);

public record ManageTraderRequest(int TraderId);
public record SetThresholdRequest(int TraderId, decimal? MinValueUsd);
public record SetChainDisabledRequest(Chain Chain, bool Disabled);
public record SetChainMinMarketCapRequest(Chain Chain, decimal? MinMarketCap);
public record SetChainTrendingDisabledRequest(Chain Chain, bool Disabled);
public record SuggestTraderItem(string Handle, Platform Platform);
public record SuggestTraderRequest(List<SuggestTraderItem> Items);
public record SetCategoryFollowRequest(string Category, bool Follow);
