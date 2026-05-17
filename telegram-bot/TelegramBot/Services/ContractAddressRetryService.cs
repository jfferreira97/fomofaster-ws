using Microsoft.EntityFrameworkCore;
using TelegramBot.Data;
using TelegramBot.Models;
using Microsoft.AspNetCore.SignalR;
using TelegramBot.Hubs;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;

namespace TelegramBot.Services;

public class ContractAddressRetryService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ContractAddressRetryService> _logger;
    private readonly List<ContractAddressRetryItem> _retryQueue = new();
    private readonly SemaphoreSlim _queueLock = new(1, 1);
    private const int MaxRetries = 1;
    private const int RetryDelaySeconds = 5;

    public ContractAddressRetryService(
        IServiceProvider serviceProvider,
        ILogger<ContractAddressRetryService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task EnqueueRetryAsync(int notificationId, string? ticker, string? trader, double? marketCap)
    {
        await _queueLock.WaitAsync();
        try
        {
            var retryItem = new ContractAddressRetryItem
            {
                NotificationId = notificationId,
                Ticker = ticker,
                Trader = trader,
                MarketCap = marketCap,
                EnqueuedAt = DateTime.UtcNow,
                RetryCount = 0,
                NextRetryAt = DateTime.UtcNow.AddSeconds(RetryDelaySeconds)
            };

            _retryQueue.Add(retryItem);
            _logger.LogInformation("Enqueued notification {NotificationId} for CA retry (ticker: {Ticker}, trader: {Trader}, marketCap: ${MarketCap:N0})",
                notificationId, ticker, trader, marketCap);
        }
        finally
        {
            _queueLock.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ContractAddressRetryService started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessRetryQueueAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing retry queue");
            }

            // Check queue every second
            await Task.Delay(1000, stoppingToken);
        }

        _logger.LogInformation("ContractAddressRetryService stopped");
    }

    private async Task ProcessRetryQueueAsync(CancellationToken stoppingToken)
    {
        List<ContractAddressRetryItem> itemsToProcess;

        await _queueLock.WaitAsync(stoppingToken);
        try
        {
            // Get items ready for retry
            itemsToProcess = _retryQueue
                .Where(item => DateTime.UtcNow >= item.NextRetryAt)
                .ToList();
        }
        finally
        {
            _queueLock.Release();
        }

        if (itemsToProcess.Count == 0)
            return;

        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var solanaService = scope.ServiceProvider.GetRequiredService<ISolanaService>();
        var userService = scope.ServiceProvider.GetRequiredService<IUserService>();
        var hubContext = scope.ServiceProvider.GetRequiredService<IHubContext<DashboardHub>>();
        var botClient = scope.ServiceProvider.GetRequiredService<Telegram.Bot.ITelegramBotClient>();

        foreach (var item in itemsToProcess)
        {
            if (stoppingToken.IsCancellationRequested)
                break;

            await ProcessRetryItemAsync(item, dbContext, solanaService, userService, hubContext, botClient, stoppingToken);
        }
    }

    private async Task ProcessRetryItemAsync(
        ContractAddressRetryItem item,
        AppDbContext dbContext,
        ISolanaService solanaService,
        IUserService userService,
        IHubContext<DashboardHub> hubContext,
        Telegram.Bot.ITelegramBotClient botClient,
        CancellationToken stoppingToken)
    {
        try
        {
            // Strategy on retry: Try DexScreener first (with marketcap if available), then Helius
            string? contractAddress = null;
            ContractAddressSource? source = null;

            if (!string.IsNullOrWhiteSpace(item.Ticker))
            {
                // Method 1: Try DexScreener first (better for tokens that have been around)
                if (item.MarketCap.HasValue && item.MarketCap.Value > 0)
                {
                    _logger.LogInformation("🔍 Retry Method 1: DexScreener with marketcap ${MarketCap:N0}", item.MarketCap);
                    using var dexScope = _serviceProvider.CreateScope();
                    var dexScreenerService = dexScope.ServiceProvider.GetRequiredService<IDexScreenerService>();
                    contractAddress = await dexScreenerService.GetContractAddressByTickerAndMarketCapAsync(item.Ticker, item.MarketCap.Value);

                    if (!string.IsNullOrWhiteSpace(contractAddress))
                    {
                        source = ContractAddressSource.DexScreener;
                    }
                }

                // Method 2: If DexScreener fails, try Helius wallet scanning
                if (string.IsNullOrWhiteSpace(contractAddress))
                {
                    _logger.LogInformation("🔍 Retry Method 2: Helius wallet scanning");
                    contractAddress = await solanaService.GetContractAddressByTickerAsync(item.Ticker);

                    if (!string.IsNullOrWhiteSpace(contractAddress))
                    {
                        source = ContractAddressSource.Helius;
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(contractAddress) && source.HasValue)
            {
                // Found it! Update the notification
                await UpdateNotificationWithContractAddressAsync(
                    item.NotificationId,
                    contractAddress,
                    source.Value,
                    dbContext,
                    userService,
                    hubContext,
                    botClient,
                    stoppingToken);

                // Remove from queue
                await RemoveFromQueueAsync(item);
                _logger.LogInformation("Successfully fetched CA for notification {NotificationId} on retry {RetryCount} via {Source}",
                    item.NotificationId, item.RetryCount + 1, source);
            }
            else
            {
                // Not found yet
                item.RetryCount++;

                if (item.RetryCount >= MaxRetries)
                {
                    // Give up
                    await RemoveFromQueueAsync(item);
                    _logger.LogInformation("Giving up on CA fetch for notification {NotificationId} after {MaxRetries} retries",
                        item.NotificationId, MaxRetries);
                }
                else
                {
                    // Schedule next retry
                    await _queueLock.WaitAsync(stoppingToken);
                    try
                    {
                        item.NextRetryAt = DateTime.UtcNow.AddSeconds(RetryDelaySeconds);
                    }
                    finally
                    {
                        _queueLock.Release();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing retry for notification {NotificationId}", item.NotificationId);

            // Schedule retry
            item.RetryCount++;
            if (item.RetryCount >= MaxRetries)
            {
                await RemoveFromQueueAsync(item);
            }
            else
            {
                await _queueLock.WaitAsync(stoppingToken);
                try
                {
                    item.NextRetryAt = DateTime.UtcNow.AddSeconds(RetryDelaySeconds);
                }
                finally
                {
                    _queueLock.Release();
                }
            }
        }
    }

    private async Task UpdateNotificationWithContractAddressAsync(
        int notificationId,
        string contractAddress,
        ContractAddressSource source,
        AppDbContext dbContext,
        IUserService userService,
        IHubContext<DashboardHub> hubContext,
        Telegram.Bot.ITelegramBotClient botClient,
        CancellationToken stoppingToken)
    {
        // Get the notification
        var notification = await dbContext.Notifications
            .FirstOrDefaultAsync(n => n.Id == notificationId, stoppingToken);

        if (notification == null)
        {
            _logger.LogWarning("Notification {NotificationId} not found", notificationId);
            return;
        }

        // Update notification with contract address
        notification.ContractAddress = contractAddress;
        notification.HasCA = true;
        notification.Chain = Chain.SOL; // Solana default
        notification.ContractAddressSource = source;  // The actual source (DexScreener or Helius)
        notification.WasRetried = true;  // Mark that this was found via retry service

        // Get all sent messages for this notification
        var sentMessages = await dbContext.SentMessages
            .Where(sm => sm.NotificationId == notificationId)
            .ToListAsync(stoppingToken);

        if (sentMessages.Count == 0)
        {
            _logger.LogWarning("No sent messages found for notification {NotificationId}", notificationId);
            return;
        }

        // Build updated message
        string dexScreenerUrl = $"https://dexscreener.com/solana/{contractAddress}";
        string newMessage = $@"{notification.Message}
📝 Contract: `{contractAddress}`
🔗 [DEXScreener]({dexScreenerUrl})";

        // Edit all Telegram messages
        int successCount = 0;
        int failCount = 0;

        foreach (var sentMessage in sentMessages)
        {
            try
            {
                await botClient.EditMessageTextAsync(
                    chatId: sentMessage.ChatId,
                    messageId: sentMessage.MessageId,
                    text: newMessage,
                    parseMode: Telegram.Bot.Types.Enums.ParseMode.Markdown,
                    disableWebPagePreview: true,
                    cancellationToken: stoppingToken
                );

                // Mark as system edited
                sentMessage.IsSystemEdited = true;
                sentMessage.EditedAt = DateTime.UtcNow;
                successCount++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to edit message {MessageId} for chat {ChatId}",
                    sentMessage.MessageId, sentMessage.ChatId);
                failCount++;
            }
        }

        // Save all changes
        await dbContext.SaveChangesAsync(stoppingToken);

        // Get total active users for dashboard
        var totalActiveUsers = await userService.GetAllActiveUsersAsync();

        // Broadcast update to dashboard via SignalR
        await hubContext.Clients.All.SendAsync("NotificationUpdated", new
        {
            id = notification.Id,
            message = notification.Message,
            ticker = notification.Ticker,
            trader = notification.Trader,
            hasCA = notification.HasCA,
            contractAddress = notification.ContractAddress,
            chain = notification.Chain?.ToString(),
            sentAt = notification.SentAt,
            recipientCount = successCount,
            totalUsers = totalActiveUsers.Count,
            isManuallyEdited = false,
            isSystemEdited = true,
            editedAt = DateTime.UtcNow,
            // Tracking data
            contractAddressSource = notification.ContractAddressSource?.ToString(),
            timesCacheHit = notification.TimesCacheHit,
            timesDexScreenerApiHit = notification.TimesDexScreenerApiHit,
            timesHeliusApiHit = notification.TimesHeliusApiHit,
            lookupDuration = notification.LookupDuration?.TotalMilliseconds,
            wasRetried = notification.WasRetried,
            marketCapAtNotification = notification.MarketCapAtNotification
        }, stoppingToken);

        _logger.LogInformation("System edited {Success}/{Total} messages for notification {NotificationId}",
            successCount, sentMessages.Count, notificationId);
    }

    private async Task RemoveFromQueueAsync(ContractAddressRetryItem item)
    {
        await _queueLock.WaitAsync();
        try
        {
            _retryQueue.Remove(item);
        }
        finally
        {
            _queueLock.Release();
        }
    }
}
