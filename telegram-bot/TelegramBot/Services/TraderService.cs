using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;
using TelegramBot.Data;
using TelegramBot.Models;

namespace TelegramBot.Services;

public class TraderService : ITraderService
{
    private readonly AppDbContext _dbContext;
    private readonly ILogger<TraderService> _logger;
    private readonly TelegramBotClient? _botClient;
    private readonly IServiceProvider _serviceProvider;

    public TraderService(
        AppDbContext dbContext,
        ILogger<TraderService> logger,
        IOptions<TelegramSettings> settings,
        IServiceProvider serviceProvider)
    {
        _dbContext = dbContext;
        _logger = logger;
        _serviceProvider = serviceProvider;

        if (!string.IsNullOrEmpty(settings.Value.BotToken))
        {
            _botClient = new TelegramBotClient(settings.Value.BotToken);
        }
    }

    public async Task<Trader?> GetTraderByHandleIgnoreCaseAsync(string handle, Platform platform = Platform.Fomo)
    {
        return await _dbContext.Traders.FirstOrDefaultAsync(t => t.Handle.ToLower() == handle.ToLower() && t.Platform == platform);
    }

    public async Task<Trader?> GetTraderByIdAsync(int traderId)
    {
        return await _dbContext.Traders.FindAsync(traderId);
    }

    public async Task<List<Trader>> GetAllTradersAsync()
    {
        return await _dbContext.Traders.OrderBy(t => t.Id).ToListAsync();
    }

    public async Task<List<Trader>> GetTradersByUserIdAsync(int userId)
    {
        return await _dbContext.UserTraders
            .Where(ut => ut.UserId == userId)
            .Include(ut => ut.Trader)
            .Select(ut => ut.Trader)
            .OrderBy(t => t.Id)
            .ToListAsync();
    }

    public async Task<Trader> AddOrUpdateTraderAsync(string handle, Platform platform = Platform.Fomo)
    {
        var trader = await GetTraderByHandleIgnoreCaseAsync(handle, platform);
        var isNewTrader = trader == null;

        if (trader == null)
        {
            trader = new Trader
            {
                Handle = handle,
                Platform = platform,
                FirstSeenAt = DateTime.UtcNow,
                LastSeenAt = DateTime.UtcNow,
            };

            _dbContext.Traders.Add(trader);
            _logger.LogInformation("New trader added: Handle={Handle} Platform={Platform}", handle, platform);
        }
        else
        {
            if (trader.Handle != handle)
            {
                _logger.LogInformation("Trader handle casing updated: {OldHandle} => {NewHandle}", trader.Handle, handle);
                trader.Handle = handle;
            }
            trader.LastSeenAt = DateTime.UtcNow;
            _logger.LogInformation("Trader updated: Handle={Handle}", handle);
        }

        await _dbContext.SaveChangesAsync();

        if (isNewTrader && _botClient != null)
        {
            await BroadcastNewTraderMessageAsync(trader);
        }

        return trader;
    }

    // Silent upsert counterpart to AddOrUpdateTraderAsync — no broadcast, no "just
    // discovered live" framing. For seeding/re-syncing known-external trader rosters
    // (e.g. a platform's own verified-trader list) where per-handle announcements to
    // every active user would be pure noise. Updates IsPumpVerified on existing rows
    // too, since this is meant to be called routinely, not just once.
    public async Task<BulkRegisterResult> BulkRegisterTradersAsync(IEnumerable<TraderSeedEntry> traders, Platform platform)
    {
        int added = 0, updated = 0;
        foreach (var entry in traders)
        {
            if (string.IsNullOrWhiteSpace(entry.Handle)) continue;

            var existing = await GetTraderByHandleIgnoreCaseAsync(entry.Handle, platform);
            if (existing == null)
            {
                _dbContext.Traders.Add(new Trader
                {
                    Handle = entry.Handle,
                    Platform = platform,
                    FirstSeenAt = DateTime.UtcNow,
                    LastSeenAt = DateTime.UtcNow,
                    IsPumpVerified = entry.IsVerified,
                });
                added++;
            }
            else if (existing.IsPumpVerified != entry.IsVerified)
            {
                existing.IsPumpVerified = entry.IsVerified;
                existing.LastSeenAt = DateTime.UtcNow;
                updated++;
            }
        }

        await _dbContext.SaveChangesAsync();
        _logger.LogInformation("Bulk-registered {Added} new / updated {Updated} existing {Platform} traders (silent, no broadcast)", added, updated, platform);
        return new BulkRegisterResult(added, updated);
    }

    private async Task BroadcastNewTraderMessageAsync(Trader trader)
    {
        if (_botClient == null)
            return;

        var activeUsers = await _dbContext.Users
            .Where(u => u.IsActive)
            .ToListAsync();

        _logger.LogInformation("Broadcasting new trader {Handle} to {Count} active users", trader.Handle, activeUsers.Count);

        foreach (var user in activeUsers)
        {
            try
            {
                string message;
                var escapedHandle = trader.Handle.Replace("_", "\\_");
                var platformLabel = trader.Platform == Platform.Pump ? "PUMP.FUN" : "FOMO APP";
                var profileLink = trader.Platform == Platform.Pump
                    ? $"https://pump.fun/profile/{trader.Handle}"
                    : $"https://fomo.family/profile/{trader.Handle}";

                // Pump's Verified-Only mode gates auto-follow eligibility too, not just
                // notification delivery — a "Verified Only" user shouldn't get auto-followed
                // onto an unverified trader just because a new one showed up.
                var restrictedByVerifiedOnly = trader.Platform == Platform.Pump
                    && user.PumpVerifiedOnly && !trader.IsPumpVerified;
                var autoFollowForPlatform = (trader.Platform == Platform.Pump
                    ? user.AutoFollowPumpTraders
                    : user.AutoFollowFomoTraders) && !restrictedByVerifiedOnly;

                if (autoFollowForPlatform)
                {
                    await FollowTraderAsync(user.Id, trader.Id);

                    message = $@"🔔 A new sharp {platformLabel} trader, [{escapedHandle}]({profileLink}), was just added to our services!

✅ This trader's trades will be tracked by you since you have {platformLabel} auto-follow ON.

Use /unfollow {escapedHandle} or /unfollow {trader.Id} if you do not desire this trader.
Use /settings to manage auto-follow and notification preferences.";
                }
                else if (restrictedByVerifiedOnly)
                {
                    message = $@"🔔 A new {platformLabel} trader, [{escapedHandle}]({profileLink}), was just added to our services!

⚠️ Not auto-followed — they're not a verified trader and you have Pump mode set to Verified Only.

Use /follow {escapedHandle} or /follow {trader.Id} if you want to follow them anyway.
Use /settings to manage auto-follow and notification preferences.";
                }
                else
                {
                    message = $@"🔔 A new sharp {platformLabel} trader, [{escapedHandle}]({profileLink}), was just added to our services!

⚠️ You are NOT following this trader since you have {platformLabel} auto-follow OFF.

Use /follow {escapedHandle} or /follow {trader.Id} if you want to follow them.
Use /settings to manage auto-follow and notification preferences.";
                }

                await _botClient.SendTextMessageAsync(
                    chatId: user.ChatId,
                    text: message,
                    parseMode: ParseMode.Markdown,
                    disableWebPagePreview: true
                );

                _logger.LogInformation("Sent new trader notification to user {ChatId}", user.ChatId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send new trader notification to user {ChatId}", user.ChatId);
            }
        }
    }

    public async Task<bool> FollowTraderAsync(int userId, int traderId)
    {
        var existing = await _dbContext.UserTraders
            .FirstOrDefaultAsync(ut => ut.UserId == userId && ut.TraderId == traderId);

        if (existing != null)
            return false;

        var userTrader = new UserTrader
        {
            UserId = userId,
            TraderId = traderId,
            FollowedAt = DateTime.UtcNow
        };

        _dbContext.UserTraders.Add(userTrader);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("User {UserId} now following trader {TraderId}", userId, traderId);
        return true;
    }

    public async Task<bool> FollowTraderByHandleAsync(int userId, string handle, Platform platform = Platform.Fomo)
    {
        var trader = await GetTraderByHandleIgnoreCaseAsync(handle, platform);
        if (trader == null)
            return false;

        return await FollowTraderAsync(userId, trader.Id);
    }

    public async Task<bool> UnfollowTraderAsync(int userId, int traderId)
    {
        var userTrader = await _dbContext.UserTraders
            .FirstOrDefaultAsync(ut => ut.UserId == userId && ut.TraderId == traderId);

        if (userTrader == null)
            return false;

        _dbContext.UserTraders.Remove(userTrader);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("User {UserId} unfollowed trader {TraderId}", userId, traderId);
        return true;
    }

    public async Task<bool> UnfollowTraderByHandleAsync(int userId, string handle, Platform platform = Platform.Fomo)
    {
        var trader = await GetTraderByHandleIgnoreCaseAsync(handle, platform);
        if (trader == null)
            return false;

        return await UnfollowTraderAsync(userId, trader.Id);
    }

    public async Task<bool> IsFollowingAsync(int userId, int traderId)
    {
        return await _dbContext.UserTraders
            .AnyAsync(ut => ut.UserId == userId && ut.TraderId == traderId);
    }

    public async Task<List<int>> GetFollowerUserIdsForTraderAsync(int traderId)
    {
        return await _dbContext.UserTraders
            .Where(ut => ut.TraderId == traderId)
            .Select(ut => ut.UserId)
            .ToListAsync();
    }

    public async Task<List<int>> GetFollowerUserIdsForTraderHandleAsync(string handle, Platform platform = Platform.Fomo)
    {
        var trader = await GetTraderByHandleIgnoreCaseAsync(handle, platform);
        if (trader == null)
            return new List<int>();

        return await GetFollowerUserIdsForTraderAsync(trader.Id);
    }

    public async Task<int> FollowAllTradersAsync(int userId)
    {
        var allTraders = await GetAllTradersAsync();
        var followedCount = 0;

        foreach (var trader in allTraders)
        {
            var success = await FollowTraderAsync(userId, trader.Id);
            if (success)
                followedCount++;
        }

        var user = await _dbContext.Users.FindAsync(userId);
        if (user != null)
        {
            user.AutoFollowFomoTraders = true;
            user.AutoFollowPumpTraders = true;
            await _dbContext.SaveChangesAsync();
        }

        _logger.LogInformation("User {UserId} followed {Count} traders (all)", userId, followedCount);
        return followedCount;
    }

    public async Task<int> UnfollowAllTradersAsync(int userId)
    {
        var followedTraders = await GetTradersByUserIdAsync(userId);
        var unfollowedCount = 0;

        foreach (var trader in followedTraders)
        {
            var success = await UnfollowTraderAsync(userId, trader.Id);
            if (success)
                unfollowedCount++;
        }

        var user = await _dbContext.Users.FindAsync(userId);
        if (user != null)
        {
            user.AutoFollowFomoTraders = false;
            user.AutoFollowPumpTraders = false;
            await _dbContext.SaveChangesAsync();
        }

        _logger.LogInformation("User {UserId} unfollowed {Count} traders (all)", userId, unfollowedCount);
        return unfollowedCount;
    }

    public async Task<bool> DeleteTraderAsync(int traderId)
    {
        var trader = await GetTraderByIdAsync(traderId);
        if (trader == null)
            return false;

        _dbContext.Traders.Remove(trader);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Deleted trader {TraderId} ({Handle})", traderId, trader.Handle);
        return true;
    }

    public async Task<bool> DeleteTraderByHandleAsync(string handle, Platform platform = Platform.Fomo)
    {
        var trader = await GetTraderByHandleIgnoreCaseAsync(handle, platform);
        if (trader == null)
            return false;

        return await DeleteTraderAsync(trader.Id);
    }
}
