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
    private PaymentPollerService? _paymentPoller;

    public TelegramService(
        IOptions<TelegramSettings> settings,
        IServiceProvider serviceProvider,
        ILogger<TelegramService> logger,
        IHubContext<DashboardHub> hubContext)
    {
        _settings = settings.Value;
        _serviceProvider = serviceProvider;
        _logger = logger;
        _hubContext = hubContext;

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

    public async Task SendNotificationToAllUsersAsync(NotificationRequest notification, string? contractAddress = null, Chain? chain = null, string? traderHandle = null, string? ticker = null, double? marketCap = null, NotificationType notificationType = NotificationType.Unknown, string? fomoWsTradeId = null)
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

        // CRITICAL: Filter by trader followers if trader handle provided - O(log n)
        if (!string.IsNullOrEmpty(traderHandle))
        {
            var followerUserIds = await traderService.GetFollowerUserIdsForTraderHandleAsync(traderHandle);

            if (followerUserIds.Count == 0)
            {
                _logger.LogInformation("No users following trader {Trader}, skipping notification", traderHandle);
                return;
            }

            // Get only active users who follow this trader
            var allUsers = await userService.GetAllActiveUsersAsync();
            users = allUsers.Where(u => followerUserIds.Contains(u.Id)).ToList();

            _logger.LogInformation("Filtered to {Count} users following trader {Trader}", users.Count, traderHandle);
        }
        else
        {
            // No trader specified - send to all active users (backward compatible)
            users = await userService.GetAllActiveUsersAsync();
        }

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

        if (!string.IsNullOrEmpty(contractAddress))
        {
            string dexScreenerUrl = ChainInfo.DexScreenerUrl(chain ?? Chain.SOL, contractAddress);

            fullMessage = $@"{processedMessage}

📝 Contract: `{contractAddress}`
🔗 {chainLabel} | [DEXScreener]({dexScreenerUrl})";

            var redactedCa = contractAddress.Length > 4
                ? contractAddress[..2] + new string('*', contractAddress.Length - 4) + contractAddress[^2..]
                : contractAddress;
            obfuscatedMessage = $@"{BuildObfuscatedText(notification.Message, traderHandle, ticker, marketCap)}

📝 Contract: `{redactedCa}`
🔗 {chainLabel} | [DEXScreener](https://dexscreener.com)

To get full details: /subscribe";
        }
        else
        {
            fullMessage = $@"{processedMessage}

🔗 {chainLabel} | [DEXScreener](https://dexscreener.com)";
            obfuscatedMessage = $@"{BuildObfuscatedText(notification.Message, traderHandle, ticker, marketCap)}

📝 Contract: `{new string('*', 44)}`
🔗 {chainLabel} | [DEXScreener](https://dexscreener.com)

To get full details: /subscribe";
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
