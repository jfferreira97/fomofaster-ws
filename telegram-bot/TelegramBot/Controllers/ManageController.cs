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
[ServiceFilter(typeof(DataNoticeFilter))]
[ServiceFilter(typeof(UserActivityFilter))]
public class ManageController : ControllerBase
{
    private readonly WebSessionService _sessionService;
    private readonly IUserService _userService;
    private readonly ITraderService _traderService;
    private readonly IChainSettingsService _chainSettingsService;
    private readonly AppDbContext _dbContext;
    private readonly TelegramSettings _telegramSettings;
    private readonly TraderIntelService _intel;
    private readonly ActiveUserCache _activeUsers;
    private readonly SubscriptionService _subscriptions;
    private readonly ReferralService _referrals;
    private readonly AppConfigService _appConfig;
    private readonly SidecarFollowClient _sidecarFollow;

    // Budget is per SUBMISSION, not per handle — see AddTrader.
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
        TraderIntelService intel,
        ActiveUserCache activeUsers,
        SubscriptionService subscriptions,
        ReferralService referrals,
        AppConfigService appConfig,
        SidecarFollowClient sidecarFollow)
    {
        _sidecarFollow = sidecarFollow;
        _activeUsers = activeUsers;
        _subscriptions = subscriptions;
        _referrals = referrals;
        _appConfig = appConfig;
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

    // Every registered user manages their follows and settings here (the bot's chat commands
    // for that are gone). What differs by plan is the trader stats: see HasRatings.
    private async Task<(Models.User? User, IActionResult? Error)> ResolveSubscriberAsync()
    {
        var user = await GetCurrentUserAsync();
        if (user == null)
            return (null, Unauthorized(new { status = "error", message = "Not logged in" }));

        return (user, null);
    }

    // Paying members (lifetime, or a monthly sub the payment poller hasn't expired) get the
    // trader ratings; everyone else gets the list, categories and follows, but none of the stats.
    // Enforced here, per request, from the session's own user — the page only mirrors it.
    private static bool HasRatings(Models.User user) => user.IsRN4L || user.IsRegisteredNurse;

    // ---- Subscribe from the website: the same address and the same price as /subscribe in the bot ----

    private static object PaymentShape(PaymentQuote q) => new
    {
        amount = q.Amount,
        address = q.Address,
        expiresAt = DateTime.SpecifyKind(q.ExpiresAt, DateTimeKind.Utc),
        renewing = q.Renewing,
        reused = q.Reused,
        solanaPay = q.SolanaPayUrl,
    };

    public sealed record PayRequest(string? Code);

    [HttpGet("subscription")]
    public async Task<IActionResult> GetSubscription()
    {
        var (user, error) = await ResolveSubscriberAsync();
        if (user == null) return error!;

        var now = DateTime.UtcNow;
        var hasAccess = user.IsRN4L || (user.IsRegisteredNurse && user.RNExpiresAt > now);
        var canPay = !user.IsRN4L && (!hasAccess || user.RNExpiresAt!.Value - now <= TimeSpan.FromDays(3));
        var open = await _subscriptions.GetOpenAsync(user.ChatId);
        return Ok(new
        {
            status = "success",
            fullAccess = hasAccess,
            lifetime = user.IsRN4L,
            activeUntil = user.IsRN4L || user.RNExpiresAt == null ? null : (DateTime?)DateTime.SpecifyKind(user.RNExpiresAt.Value, DateTimeKind.Utc),
            canPay,
            trialEndsAt = user.TrialExpiresAt == null ? null : (DateTime?)DateTime.SpecifyKind(user.TrialExpiresAt.Value, DateTimeKind.Utc),
            basePrice = await _appConfig.GetSubscriptionPriceSolAsync(),
            open = open == null ? null : PaymentShape(open),
        });
    }

    // Opens (or reuses) the payment address. A code typed on the page, or the one the site remembered from a referral link, is attached
    // first (first one wins, wrong ones are rate limited), so the amount the page shows is the one the payment is checked against.
    [HttpPost("subscription/pay")]
    public async Task<IActionResult> Pay([FromBody] PayRequest? req)
    {
        var (user, error) = await ResolveSubscriberAsync();
        if (user == null) return error!;

        string? codeResult = null;
        if (!string.IsNullOrWhiteSpace(req?.Code))
        {
            var attached = await _referrals.AttachAsync(user.ChatId, req!.Code);
            codeResult = attached switch
            {
                AttachResult.Attached => "saved",
                AttachResult.AlreadyHasCode => "kept",
                AttachResult.Throttled => "throttled",
                _ => "invalid",
            };

            // An address opened at the full price before the code arrived: let it lapse so the new one carries the right amount.
            if (attached == AttachResult.Attached)
            {
                var stale = await _dbContext.PendingPayments
                    .Where(p => p.ChatId == user.ChatId && !p.IsConfirmed && p.ExpiresAt > DateTime.UtcNow && p.ReferralCode == null)
                    .ToListAsync();
                // a minute in the past, not "now": a timestamp equal to the clock tick can still compare as "open" in SQLite's text comparison
                foreach (var p in stale) p.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
                if (stale.Count > 0) await _dbContext.SaveChangesAsync();
            }
        }

        var quote = await _subscriptions.GetOrCreateAsync(user.ChatId);
        if (quote.Status == "active")
            return Ok(new { status = "active", lifetime = quote.Lifetime, activeUntil = quote.ActiveUntil });

        return Ok(new { status = "success", codeResult, payment = PaymentShape(quote) });
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
            access = HasRatings(user) ? "full" : "trial",
            settings = new
            {
                autoFollowFomoTraders = user.AutoFollowFomoTraders,
                autoFollowPumpTraders = user.AutoFollowPumpTraders,
                notifyFomoBuySell = user.NotifyFomoBuySell,
                notifyFomoThesis = user.NotifyFomoThesis,
                notifyPumpCallouts = user.NotifyPumpCallouts,
                notifyPumpBuySell = user.NotifyPumpBuySell,
                notifyTrending = user.NotifyTrending,
                repeatWindowMinutes = user.RepeatWindowMinutes,
                minTokenAgeMinutes = user.MinTokenAgeMinutes
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
        // Nullable on purpose: a manage page cached from before this setting existed posts no
        // value, which must leave the preference alone rather than silently turn it off.
        if (request.NotifyPumpBuySell.HasValue) user.NotifyPumpBuySell = request.NotifyPumpBuySell.Value;
        user.NotifyTrending = request.NotifyTrending;
        user.RepeatWindowMinutes = request.RepeatWindowMinutes;
        // Nullable on purpose, like NotifyPumpBuySell: a page cached from before this setting posts nothing, which must leave it alone.
        if (request.MinTokenAgeMinutes.HasValue) user.MinTokenAgeMinutes = Math.Clamp(request.MinTokenAgeMinutes.Value, 0, 129_600);

        await _dbContext.SaveChangesAsync();
        await _activeUsers.RefreshNowAsync();   // the alert pipeline reads a copy of the users: make it see this change now, not in up to 2 minutes
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
                viaCategory = t.ViaCategory,   // the category that added this follow, or null
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
        var viaCategory = follow?.ViaCategory;
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

        // Following adds everyone in it now (and the daily sync adds whoever joins); unfollowing
        // takes back only the follows the category added.
        if (request.Follow)
            await _traderService.SyncCategoryFollowsAsync(user.Id);
        else
            await _traderService.RemoveCategoryFollowsAsync(user.Id, request.Category);

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
                trendingDisabled = s?.TrendingDisabled ?? false,
                minTokenAgeMinutes = s?.MinTokenAgeMinutes
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

    // Minimum token age for one chain, in minutes. Null = follow the general setting; 0 = no minimum on this chain.
    [HttpPost("chains/minage")]
    public async Task<IActionResult> SetChainMinTokenAge([FromBody] SetChainMinTokenAgeRequest request)
    {
        var (user, error) = await ResolveSubscriberAsync();
        if (user == null) return error!;

        if (!IsValidChain(request.Chain))
            return BadRequest(new { status = "error", message = "Unknown chain" });

        await _chainSettingsService.SetMinTokenAgeAsync(user.Id, request.Chain, request.MinTokenAgeMinutes is int m ? Math.Clamp(m, 0, 129_600) : null);
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

    // Add traders: our own logged-in account on that platform (the FOMO and pump.fun sidecars) follows each one right away,
    // the trader is created, and the user follows them. No more suggestions waiting for someone to follow them by hand.
    // One submission may carry many handles and costs a single unit of the daily budget. Rows are judged individually: bad
    // ones come back annotated, good ones still land. "suggest-trader" stays as an alias for pages opened before the change.
    [HttpPost("add-trader")]
    [HttpPost("suggest-trader")]
    public async Task<IActionResult> AddTrader([FromBody] SuggestTraderRequest request, CancellationToken ct)
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
            return StatusCode(429, new { status = "error", code = "rate_limited", message = $"You can add traders up to {SubmissionRateLimit} times per day — try again later." });

        var batchId = Guid.NewGuid();
        var results = new List<object>();
        var accepted = new List<(string Handle, Platform Platform)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var handle = CleanHandle(item.Handle);

            if (!Enum.IsDefined(item.Platform))
            {
                results.Add(new { index = i, handle, ok = false, code = "invalid_platform", message = "Unknown platform." });
                continue;
            }
            if (string.IsNullOrWhiteSpace(handle) || handle.Length > 64 || !handle.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-'))
            {
                results.Add(new { index = i, handle, ok = false, code = "invalid", message = "Not a valid handle." });
                continue;
            }
            if (!seen.Add($"{item.Platform}:{handle}"))
            {
                results.Add(new { index = i, handle, ok = false, code = "duplicate", message = "Listed twice in this batch." });
                continue;
            }

            var tracked = await _traderService.GetTraderByHandleIgnoreCaseAsync(handle, item.Platform);
            // Tracked or not, our account follows them (a no-op when it already does): a tracked trader the account dropped
            // would otherwise stay silent with nothing to show for it.
            var follow = await _sidecarFollow.FollowAsync(item.Platform, tracked?.Handle ?? handle, ct);
            if (tracked != null)
            {
                await _traderService.FollowTraderAsync(user.Id, tracked.Id);
                results.Add(new { index = i, handle = tracked.Handle, ok = true, code = "following", message = "Already tracked — you follow them now." });
                continue;
            }
            if (!follow.Ok)
            {
                var platformName = item.Platform == Platform.Pump ? "pump.fun" : "FOMO";
                var message = follow.Code switch
                {
                    "not_found" => $"No {platformName} user with that handle.",
                    "invalid" => follow.Message == "that account has no pump.fun username" ? "That pump.fun account has no username, so it can't be tracked." : $"That {platformName} account can't be added.",
                    "rate_limited" => $"{platformName} is busy — try again in a minute.",
                    _ => $"Couldn't reach {platformName} right now — try again in a few minutes.",
                };
                results.Add(new { index = i, handle, ok = false, code = follow.Code, message });
                continue;
            }

            // The platform's own spelling of the handle, the one their alerts will carry.
            var canonical = string.IsNullOrWhiteSpace(follow.Handle) ? handle : follow.Handle;
            _dbContext.SuggestedTraders.Add(new SuggestedTrader
            {
                UserId = user.Id,
                Handle = canonical,
                Platform = item.Platform,
                CreatedAt = DateTime.UtcNow,
                BatchId = batchId
            });
            await _dbContext.SaveChangesAsync();
            await _traderService.RegisterAddedTraderAsync(canonical, item.Platform);   // creates them and follows everyone who asked
            accepted.Add((canonical, item.Platform));
            results.Add(new { index = i, handle = canonical, ok = true, code = "added", message = (string?)null });
        }

        // Nothing added means nothing was stored, so the budget is untouched — a batch of
        // typos shouldn't burn one of the day's submissions.
        if (accepted.Count == 0)
            return Ok(new { status = "success", accepted = 0, remaining = SubmissionRateLimit - recentSubmissions, results });

        await NotifyOwnerOfSuggestionsAsync(user, accepted);

        return Ok(new
        {
            status = "success",
            accepted = accepted.Count,
            remaining = SubmissionRateLimit - recentSubmissions - 1,
            results
        });
    }

    // As-you-type check for the add form: which accounts exist on that platform for what the user typed, asked through the
    // logged-in sidecar (FOMO: the app's own fuzzy user search; pump.fun has no user search, so the exact handle check there),
    // plus the tracked traders whose handle starts with it. Read-only. checkedLive=false means the platform could not be asked.
    [HttpGet("trader-lookup")]
    public async Task<IActionResult> TraderLookup([FromQuery] Platform platform, [FromQuery] string? q, CancellationToken ct)
    {
        var (user, error) = await ResolveSubscriberAsync();
        if (user == null) return error!;

        var query = CleanHandle(q);
        if (!Enum.IsDefined(platform) || query.Length < 2 || query.Length > 64 || !query.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-'))
            return Ok(new { checkedLive = false, users = Array.Empty<object>() });

        var live = await _sidecarFollow.LookupAsync(platform, query, ct);
        var liveUsers = live.Ok ? live.Users ?? new() : new();
        var lower = query.ToLowerInvariant();
        var liveLower = liveUsers.Select(u => u.Handle.ToLower()).ToList();
        var tracked = await _dbContext.Traders
            .Where(t => t.Platform == platform && (t.Handle.ToLower().StartsWith(lower) || liveLower.Contains(t.Handle.ToLower())))
            .OrderBy(t => t.Handle.Length)
            .Take(30)
            .Select(t => new { t.Id, t.Handle })
            .ToListAsync(ct);
        var trackedIds = tracked.Select(t => t.Id).ToList();
        var following = (await _dbContext.UserTraders
            .Where(ut => ut.UserId == user.Id && trackedIds.Contains(ut.TraderId))
            .Select(ut => ut.TraderId)
            .ToListAsync(ct)).ToHashSet();
        var trackedByHandle = tracked.GroupBy(t => t.Handle.ToLower()).ToDictionary(g => g.Key, g => g.First().Id);

        var users = new List<object>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string handle, string? name, string? pic, double? followers, bool verified)
        {
            if (users.Count >= 8 || !seen.Add(handle)) return;
            int? id = trackedByHandle.TryGetValue(handle.ToLower(), out var tid) ? tid : null;
            users.Add(new { handle, name, pic = SafePic(pic), followers, verified, tracked = id != null, following = id != null && following.Contains(id.Value) });
        }
        // the exact match first, then what the platform found, then tracked traders starting with it
        foreach (var u in liveUsers.OrderBy(u => u.Handle.Equals(query, StringComparison.OrdinalIgnoreCase) ? 0 : 1))
            Add(u.Handle, u.Name, u.Pic, u.Followers, u.Verified);
        foreach (var t in tracked.Where(t => t.Handle.ToLower().StartsWith(lower)))
            Add(t.Handle, null, null, null, false);

        return Ok(new { checkedLive = live.Ok, exact = platform == Platform.Pump, users });
    }

    // A profile picture the page can show as is: https only, dead IPFS gateways swapped for pump.fun's own.
    private static string? SafePic(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > 500) return null;
        var at = url.IndexOf("/ipfs/", StringComparison.OrdinalIgnoreCase);
        if (at >= 0) url = "https://pump.mypinata.cloud" + url[at..];
        return Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps ? u.ToString() : null;
    }

    // "@x", "x", or a pasted profile link (fomo.family/profile/x, pump.fun/profile/x) -> "x".
    private static string CleanHandle(string? input)
    {
        var s = (input ?? "").Trim();
        var at = s.IndexOf("/profile/", StringComparison.OrdinalIgnoreCase);
        if (at >= 0) s = s[(at + "/profile/".Length)..];
        s = s.Split('?', '#', '/')[0];
        return s.Trim().TrimStart('@');
    }

    // For the owner's information only (nothing to do any more): who added which traders. Same admin-bot DM channel
    // HandleFreeTextAsync forwards non-command messages through. One DM per submission, not per handle.
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
        var heading = suggestions.Count == 1 ? "Trader added" : $"Traders added ({suggestions.Count})";
        var text = $"➕ <b>{Esc(heading)}</b>\n\nby {requester}, our accounts follow them now\n\n{string.Join("\n", lines)}";

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
    int RepeatWindowMinutes,
    bool? NotifyPumpBuySell = null,
    int? MinTokenAgeMinutes = null);

public record ManageTraderRequest(int TraderId);
public record SetThresholdRequest(int TraderId, decimal? MinValueUsd);
public record SetChainDisabledRequest(Chain Chain, bool Disabled);
public record SetChainMinMarketCapRequest(Chain Chain, decimal? MinMarketCap);
public record SetChainMinTokenAgeRequest(Chain Chain, int? MinTokenAgeMinutes);
public record SetChainTrendingDisabledRequest(Chain Chain, bool Disabled);
public record SuggestTraderItem(string Handle, Platform Platform);
public record SuggestTraderRequest(List<SuggestTraderItem> Items);
public record SetCategoryFollowRequest(string Category, bool Follow);
