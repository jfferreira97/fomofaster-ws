using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;
using TelegramBot.Data;
using TelegramBot.Hubs;
using TelegramBot.Models;

namespace TelegramBot.Services;

public class TelegramService : ITelegramService
{
    private readonly TelegramSettings _settings;
    private readonly TelegramBotClient? _botClient;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<TelegramService> _logger;
    private readonly IHubContext<DashboardHub> _hubContext;
    private readonly ChainSettingsCache _chainSettingsCache;
    private PaymentPollerService? _paymentPoller;

    public TelegramService(
        IOptions<TelegramSettings> settings,
        IServiceProvider serviceProvider,
        ILogger<TelegramService> logger,
        IHubContext<DashboardHub> hubContext,
        ChainSettingsCache chainSettingsCache)
    {
        _settings = settings.Value;
        _serviceProvider = serviceProvider;
        _logger = logger;
        _hubContext = hubContext;
        _chainSettingsCache = chainSettingsCache;

        if (!string.IsNullOrEmpty(_settings.BotToken))
        {
            try
            {
                _botClient = new TelegramBotClient(_settings.BotToken);
                _logger.LogInformation("Telegram bot client initialized successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize Telegram bot client");
            }
        }
        else
        {
            _logger.LogWarning("Telegram bot token not configured");
        }
    }

    public bool IsConfigured()
    {
        return _botClient != null;
    }

    public async Task SendNotificationToAllUsersAsync(NotificationRequest notification, string? contractAddress = null, Chain? chain = null, string? traderHandle = null, string? ticker = null, double? marketCap = null, NotificationType notificationType = NotificationType.Unknown, string? fomoWsTradeId = null, Platform platform = Platform.Fomo, double? usdAmount = null)
    {
        if (_botClient == null)
        {
            _logger.LogWarning("Telegram bot not configured, skipping message send");
            return;
        }

        using var scope = _serviceProvider.CreateScope();
        var userService = scope.ServiceProvider.GetRequiredService<IUserService>();
        var traderService = scope.ServiceProvider.GetRequiredService<ITraderService>();

        List<Models.User> users;
        Dictionary<int, decimal?> followerThresholds = new();

        // CRITICAL: Filter by trader followers if trader handle provided - O(log n)
        if (!string.IsNullOrEmpty(traderHandle))
        {
            followerThresholds = await traderService.GetFollowerThresholdsForTraderHandleAsync(traderHandle, platform);

            if (followerThresholds.Count == 0)
            {
                _logger.LogInformation("No users following trader {Trader}, skipping notification", traderHandle);
                return;
            }

            // Get only active users who follow this trader
            var allUsers = await userService.GetAllActiveUsersAsync();
            users = allUsers.Where(u => followerThresholds.ContainsKey(u.Id)).ToList();

            _logger.LogInformation("Filtered to {Count} users following trader {Trader}", users.Count, traderHandle);
        }
        else
        {
            // No trader specified - send to all active users (backward compatible)
            users = await userService.GetAllActiveUsersAsync();
        }

        // Per-trader alert floor, set on the manage page: only deliver trade-like activity
        // (Buy/Sell/Callout) to a follower whose MinValueUsd for this specific trader is
        // unset or met by this trade's USD size. Notifications with no known dollar amount
        // (thesis, repost, reply, trending) pass through untouched.
        if (usdAmount.HasValue
            && (notificationType is NotificationType.Buy or NotificationType.Sell or NotificationType.Callout)
            && followerThresholds.Count > 0)
        {
            users = users.Where(u =>
            {
                var minValueUsd = followerThresholds.GetValueOrDefault(u.Id);
                return !minValueUsd.HasValue || usdAmount.Value >= (double)minValueUsd.Value;
            }).ToList();
        }

        // Per-user chain preferences, set via /chains: a user who disabled this chain gets
        // nothing on it; one with a per-chain market cap floor set only gets it once the
        // token's market cap (when known) meets that floor; one who muted just Trending for
        // this chain (TrendingDisabled) keeps Buy/Sell/Callout but loses Trending here only.
        // Users with no row for this chain are unaffected (default: enabled, no minimum).
        // Reads straight from the in-memory ChainSettingsCache, not the DB — this runs on
        // every notification, so it must stay a plain dictionary lookup rather than a query.
        if (chain.HasValue)
        {
            var chainSettings = _chainSettingsCache.GetForChain(chain.Value);
            if (chainSettings.Count > 0)
            {
                users = users.Where(u =>
                {
                    if (!chainSettings.TryGetValue(u.Id, out var setting)) return true;
                    if (setting.IsDisabled) return false;
                    if (notificationType == NotificationType.CUSTOM_Trending && setting.TrendingDisabled) return false;
                    if (setting.MinMarketCap.HasValue && marketCap.HasValue)
                        return marketCap.Value >= (double)setting.MinMarketCap.Value;
                    return true;
                }).ToList();
            }
        }

        // Repeat window: skip users who already got a Buy/Sell alert for this exact
        // trader+contract+side within their configured window (e.g. scrolling back
        // through history shouldn't re-surface the same signal on the same coin).
        if ((notificationType == NotificationType.Buy || notificationType == NotificationType.Sell)
            && !string.IsNullOrEmpty(traderHandle) && !string.IsNullOrEmpty(contractAddress))
        {
            var dbContextForThrottle = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var lastSentByChat = await dbContextForThrottle.SentMessages
                .Where(sm => sm.Notification.Trader == traderHandle
                          && sm.Notification.ContractAddress == contractAddress
                          && sm.Notification.Type == notificationType)
                .GroupBy(sm => sm.ChatId)
                .Select(g => new { ChatId = g.Key, LastSentAt = g.Max(sm => sm.SentAt) })
                .ToDictionaryAsync(x => x.ChatId, x => x.LastSentAt);

            users = users.Where(u =>
            {
                if (u.RepeatWindowMinutes <= 0) return true; // throttling disabled for this user
                if (!lastSentByChat.TryGetValue(u.ChatId, out var lastSentAt)) return true;
                return lastSentAt <= DateTime.UtcNow.AddMinutes(-u.RepeatWindowMinutes);
            }).ToList();
        }

        // Per-user notification-type preferences, set via /settings. Callout/Repost/Reply
        // share one Pump toggle by design — to a subscriber they're all just "pump activity
        // from people you follow" — and PumpVerifiedOnly additionally requires the trader
        // be IsPumpVerified when set. Types with no toggle (Deposit/Verified/Unknown) pass
        // through unfiltered.
        bool? traderIsPumpVerified = null;
        if (notificationType is NotificationType.Callout or NotificationType.Repost or NotificationType.Reply
            && !string.IsNullOrEmpty(traderHandle))
        {
            var trader = await traderService.GetTraderByHandleIgnoreCaseAsync(traderHandle, platform);
            traderIsPumpVerified = trader?.IsPumpVerified ?? false;
        }

        users = users.Where(u => notificationType switch
        {
            NotificationType.Buy or NotificationType.Sell => u.NotifyFomoBuySell,
            NotificationType.Thesis                        => u.NotifyFomoThesis,
            NotificationType.Callout or NotificationType.Repost or NotificationType.Reply =>
                u.NotifyPumpCallouts && (!u.PumpVerifiedOnly || traderIsPumpVerified == true),
            NotificationType.CUSTOM_Trending => u.NotifyTrending,
            _ => true
        }).ToList();

        if (users.Count == 0)
        {
            _logger.LogWarning("No active users to send notification to");
            return;
        }

        // Escape Markdown special chars in the raw message before parsing — stray _, *, `, [
        // characters (including those shifted by invisible unicode) cause Telegram to throw
        // "Can't find end of entity". Do this first, then inject the intentional link.
        static string EscapeMarkdown(string text) =>
            text.Replace("_", "\\_").Replace("*", "\\*").Replace("`", "\\`").Replace("[", "\\[");

        var processedMessage = EscapeMarkdown(notification.Message);
        if (!string.IsNullOrEmpty(traderHandle))
        {
            // Strip @handle — plain name only, no Twitter link
            processedMessage = System.Text.RegularExpressions.Regex.Replace(
                processedMessage,
                System.Text.RegularExpressions.Regex.Escape($"@{traderHandle}"),
                traderHandle,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
            );
        }

        string fullMessage;
        string obfuscatedMessage;

        var chainLabel = (chain ?? Chain.SOL).ToString();

        // Fire bubble still marks Trending in the link line; buy/sell no longer gets one there.
        var typeBubble = notificationType switch
        {
            NotificationType.CUSTOM_Trending => "🔥 | ",
            _                                => ""
        };

        // Axiom and Terminal (Padre) are each omitted per-chain when that platform doesn't
        // support the chain (confirmed 2026-08-30: Axiom has no Base/Monad, Padre has no Monad)
        // rather than guessed. DexScreener always has a slug for every chain we track, so it
        // stays last as the one link that's always present.
        static string TradeLinks(Chain chain, string contractAddress)
        {
            var parts = new List<string>();
            var axiomUrl = ChainInfo.AxiomUrl(chain, contractAddress);
            if (axiomUrl != null) parts.Add($"[Axiom]({axiomUrl})");

            var padreUrl = ChainInfo.PadreUrl(chain, contractAddress);
            if (padreUrl != null) parts.Add($"[Terminal]({padreUrl})");

            parts.Add($"[Dexscreener]({ChainInfo.DexScreenerUrl(chain, contractAddress)})");

            return string.Join(" | ", parts);
        }

        const string GenericTradeLinks = "[Axiom](https://axiom.trade) | [Terminal](https://trade.padre.gg)";

        if (!string.IsNullOrEmpty(contractAddress))
        {
            var resolvedChain = chain ?? Chain.SOL;

            fullMessage = $@"{processedMessage}

📝 Contract: `{contractAddress}`
🔗 {chainLabel} | {typeBubble}{TradeLinks(resolvedChain, contractAddress)}";

            var redactedCa = contractAddress.Length > 4
                ? contractAddress[..2] + new string('*', contractAddress.Length - 4) + contractAddress[^2..]
                : contractAddress;
            obfuscatedMessage = $@"{BuildObfuscatedText(notification.Message, traderHandle, ticker, marketCap)}

📝 Contract: `{redactedCa}`
🔗 {chainLabel} | {typeBubble}{GenericTradeLinks}

To get full details: /subscribe";
        }
        else
        {
            fullMessage = $@"{processedMessage}

🔗 {chainLabel} | {typeBubble}{GenericTradeLinks}";
            obfuscatedMessage = $@"{BuildObfuscatedText(notification.Message, traderHandle, ticker, marketCap)}

📝 Contract: `{new string('*', 44)}`
🔗 {chainLabel} | {typeBubble}{GenericTradeLinks}

To get full details: /subscribe";
        }

        // Global per-platform prefix — 👀 mirrors the FOMO logo, 💊 marks Pump-sourced
        // notifications. TRENDING alerts are cross-platform by nature, so they skip it entirely.
        if (notificationType != NotificationType.CUSTOM_Trending)
        {
            var platformPrefix = platform == Platform.Pump ? "💊" : "👀";
            fullMessage = $"{platformPrefix} | {fullMessage}";
            obfuscatedMessage = $"{platformPrefix} | {obfuscatedMessage}";
        }

        static bool IsRNActive(Models.User u) =>
            u.IsRN4L || (u.IsRegisteredNurse && u.RNExpiresAt > DateTime.UtcNow);

        _paymentPoller ??= _serviceProvider.GetService<PaymentPollerService>();

        int successCount = 0;
        int failCount = 0;

        // Create Notification record in database
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var notificationRecord = new Models.Notification
        {
            Message = notification.Message,
            Ticker = ticker,
            Trader = traderHandle,
            ContractAddress = contractAddress,
            Chain = chain,
            SentAt = DateTime.UtcNow,
            MarketCapAtNotification = marketCap.HasValue ? (decimal)marketCap.Value : null,
            Type = notificationType,
            Platform = platform,
            FK_WsEvent_WsId = fomoWsTradeId
        };
        dbContext.Notifications.Add(notificationRecord);
        await dbContext.SaveChangesAsync();

        // RN4L first, then active RN, then free users
        users = users
            .OrderByDescending(u => u.IsRN4L)
            .ThenByDescending(u => IsRNActive(u))
            .ToList();

        // Send messages and track MessageIds
        foreach (var user in users)
        {
            try
            {
                string userMessage;
                if (IsRNActive(user))
                {
                    userMessage = fullMessage;
                }
                else
                {
                    var pendingWallet = _paymentPoller?.PendingWalletCache.GetValueOrDefault(user.ChatId);
                    userMessage = pendingWallet != null
                        ? obfuscatedMessage + $"\n\nYour pending payment wallet:\n`{pendingWallet}`"
                        : obfuscatedMessage;
                }
                var sentMessage = await _botClient.SendTextMessageAsync(
                    chatId: user.ChatId,
                    text: userMessage,
                    parseMode: ParseMode.Markdown,
                    disableWebPagePreview: true
                );

                var sentMessageRecord = new Models.SentMessage
                {
                    NotificationId = notificationRecord.Id,
                    ChatId = user.ChatId,
                    MessageId = sentMessage.MessageId,
                    SentAt = DateTime.UtcNow,
                    IsManuallyEdited = false,
                    IsSystemEdited = false,
                    EditedAt = null
                };
                dbContext.SentMessages.Add(sentMessageRecord);

                successCount++;
            }
            catch (Telegram.Bot.Exceptions.ApiRequestException apiEx) when (apiEx.Message.Contains("bot was blocked by the user") || apiEx.Message.Contains("user is deactivated") || apiEx.Message.Contains("chat not found"))
            {
                _logger.LogWarning("User {ChatId} blocked the bot or is unavailable, deactivating user", user.ChatId);
                await userService.DeactivateUserAsync(user.ChatId);
                failCount++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send message to user {ChatId}", user.ChatId);
                failCount++;
            }
        }

        // Save all SentMessage records in one batch
        await dbContext.SaveChangesAsync();

        // Get total active users for dashboard metadata
        var totalActiveUsers = await userService.GetAllActiveUsersAsync();

        // Broadcast notification to dashboard via SignalR
        await _hubContext.Clients.All.SendAsync("ReceiveNotification", new
        {
            id = notificationRecord.Id,
            message = notificationRecord.Message,
            ticker = notificationRecord.Ticker,
            trader = notificationRecord.Trader,

            contractAddress = notificationRecord.ContractAddress,
            chain = notificationRecord.Chain?.ToString(),
            sentAt = notificationRecord.SentAt,
            recipientCount = successCount,
            totalUsers = totalActiveUsers.Count,
            marketCapAtNotification = notificationRecord.MarketCapAtNotification
        });

        _logger.LogInformation("✅ Notification sent to {Success}/{Total} users ({Failed} failed)",
            successCount, users.Count, failCount);

    }

    public async Task SendTestMessageAsync(long chatId, string message)
    {
        if (_botClient == null)
        {
            throw new InvalidOperationException("Telegram bot not configured");
        }

        await _botClient.SendTextMessageAsync(
            chatId: chatId,
            text: message,
            parseMode: ParseMode.Markdown
        );

        _logger.LogInformation("✅ Test message sent to chat: {ChatId}", chatId);
    }

    private string? _cachedBotUsername;

    public async Task<string?> GetBotUsernameAsync()
    {
        if (_cachedBotUsername != null)
            return _cachedBotUsername;

        if (_botClient == null)
            return null;

        var me = await _botClient.GetMeAsync();
        _cachedBotUsername = me.Username;
        return _cachedBotUsername;
    }

    public async Task<object> GetUpdatesAsync()
    {
        if (_botClient == null)
        {
            throw new InvalidOperationException("Telegram bot not configured");
        }

        var updates = await _botClient.GetUpdatesAsync();
        return updates.Select(u => new
        {
            updateId = u.Id,
            message = u.Message != null ? new
            {
                chatId = u.Message.Chat.Id,
                text = u.Message.Text,
                from = u.Message.From != null ? new
                {
                    id = u.Message.From.Id,
                    username = u.Message.From.Username,
                    firstName = u.Message.From.FirstName
                } : null
            } : null
        }).ToList();
    }

    public async Task<bool> SendPlainMessageAsync(long chatId, string message)
    {
        if (_botClient == null)
        {
            _logger.LogWarning("Telegram bot not configured, cannot send plain message");
            return false;
        }

        try
        {
            await _botClient.SendTextMessageAsync(
                chatId: chatId,
                text: message,
                disableWebPagePreview: true
            );

            _logger.LogInformation("Plain message sent to chat {ChatId}", chatId);
            return true;
        }
        catch (Telegram.Bot.Exceptions.ApiRequestException apiEx) when (apiEx.Message.Contains("bot was blocked by the user") || apiEx.Message.Contains("user is deactivated") || apiEx.Message.Contains("chat not found"))
        {
            // User blocked the bot or deleted their account - deactivate them
            _logger.LogWarning("User {ChatId} blocked the bot or is unavailable, deactivating user", chatId);

            using var scope = _serviceProvider.CreateScope();
            var userService = scope.ServiceProvider.GetRequiredService<IUserService>();
            await userService.DeactivateUserAsync(chatId);

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send plain message to chat {ChatId}", chatId);
            return false;
        }
    }

    private static string BuildObfuscatedText(string rawMessage, string? traderHandle, string? ticker, double? marketCap)
    {
        var text = rawMessage;

        // Strip @handle — plain name only
        if (!string.IsNullOrEmpty(traderHandle))
            text = System.Text.RegularExpressions.Regex.Replace(
                text,
                System.Text.RegularExpressions.Regex.Escape($"@{traderHandle}"),
                traderHandle,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Replace ticker symbol with "coin"
        if (!string.IsNullOrEmpty(ticker))
            text = System.Text.RegularExpressions.Regex.Replace(
                text,
                System.Text.RegularExpressions.Regex.Escape(ticker),
                "coin",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Hide buy size (dollar amount after "bought")
        text = System.Text.RegularExpressions.Regex.Replace(
            text,
            @"(?<=bought )\$[\d,]+\.?\d*",
            "****");

        // Escape markdown special chars
        text = text.Replace("_", "\\_").Replace("*", "\\*").Replace("`", "\\`").Replace("[", "\\[");

        return text;
    }
}
