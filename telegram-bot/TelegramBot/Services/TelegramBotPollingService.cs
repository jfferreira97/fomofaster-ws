using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using TelegramBot.Data;
using TelegramBot.Hubs;
using TelegramBot.Models;

namespace TelegramBot.Services;

public class TelegramBotPollingService : BackgroundService
{
    private readonly TelegramBotClient? _botClient;
    private readonly TelegramBotClient? _adminBotClient;
    private readonly TelegramSettings _settings;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<TelegramBotPollingService> _logger;
    private readonly IHubContext<DashboardHub> _hubContext;
    private int _offset = 0;
    private long _ownerChatId;
    private string? _ownerUsername;

    // FOMOFASTER token contract address - update this when token launches
    // private const string TOKEN_CONTRACT_ADDRESS = "6gCEGUjPisdGFc6FhRGL43hoD263dRF81i2L3bo5bonk";

    public TelegramBotPollingService(
        IOptions<TelegramSettings> settings,
        IServiceProvider serviceProvider,
        ILogger<TelegramBotPollingService> logger,
        IHubContext<DashboardHub> hubContext)
    {
        _settings = settings.Value;
        _serviceProvider = serviceProvider;
        _logger = logger;
        _hubContext = hubContext;

        if (!string.IsNullOrEmpty(_settings.AdminBotToken))
            _adminBotClient = new TelegramBotClient(_settings.AdminBotToken);

        if (!string.IsNullOrEmpty(_settings.BotToken))
        {
            _botClient = new TelegramBotClient(_settings.BotToken);
            _logger.LogInformation("Telegram polling service initialized");
        }
        else
        {
            _logger.LogWarning("Bot token not configured, polling service will not start");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_botClient == null)
        {
            _logger.LogWarning("Bot client not initialized, polling service stopped");
            return;
        }

        _logger.LogInformation("Starting Telegram bot polling...");

        // Resolve owner ChatId and Username from DB at startup
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var owner = await dbContext.Users.FindAsync(_settings.OwnerUserId);
            if (owner != null)
            {
                _ownerChatId = owner.ChatId;
                _ownerUsername = owner.Username;
                _logger.LogInformation("Owner resolved: @{Username} ({ChatId})", _ownerUsername, _ownerChatId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not resolve owner from DB UserId {UserId}", _settings.OwnerUserId);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var updates = await _botClient.GetUpdatesAsync(
                    offset: _offset,
                    timeout: 30,
                    cancellationToken: stoppingToken
                );

                foreach (var update in updates)
                {
                    _offset = update.Id + 1;
                    await HandleUpdateAsync(update);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Polling cancelled");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during polling");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }

        _logger.LogInformation("Telegram bot polling stopped");
    }

    private async Task HandleUpdateAsync(Update update)
    {
        try
        {
            if (update.Message is { } message)
            {
                await HandleMessageAsync(message);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling update {UpdateId}", update.Id);
        }
    }

    private async Task HandleMessageAsync(Message message)
    {
        var chatId = message.Chat.Id;
        var text = message.Text?.Trim();

        _logger.LogInformation("Received message from {ChatId}: {Text}", chatId, text);

        if (string.IsNullOrEmpty(text))
            return;

        using var scope = _serviceProvider.CreateScope();
        var userService = scope.ServiceProvider.GetRequiredService<IUserService>();

        if (text.StartsWith("/"))
        {
            await HandleCommandAsync(message, userService);
        }
        else
        {
            await HandleFreeTextAsync(message);
        }
    }

    private async Task HandleFreeTextAsync(Message message)
    {
        if (_botClient == null) return;

        var chatId = message.Chat.Id;
        var text = message.Text ?? "";
        var username = message.From?.Username ?? message.From?.FirstName ?? "unknown";

        // Auto-reply to the user
        var supportText = !string.IsNullOrEmpty(_ownerUsername)
            ? $"This bot doesn't support direct messages. Message the developer directly: @{_ownerUsername}"
            : "This bot doesn't support direct messages.";

        await _botClient.SendTextMessageAsync(
            chatId: chatId,
            text: supportText
        );

        // Forward to owner via admin bot
        if (_ownerChatId != 0 && _adminBotClient != null)
        {
            await _adminBotClient.SendTextMessageAsync(
                chatId: _ownerChatId,
                text: $"📩 Message from @{username} (`{chatId}`):\n\n{text}",
                parseMode: ParseMode.Markdown
            );
        }
    }

    private async Task HandleCommandAsync(Message message, IUserService userService)
    {
        if (_botClient == null)
            return;

        var chatId = message.Chat.Id;
        var command = message.Text?.Split(' ')[0].ToLower();

        using var scope = _serviceProvider.CreateScope();
        var traderService = scope.ServiceProvider.GetRequiredService<ITraderService>();

        switch (command)
        {
            case "/start":
                var newUser = await userService.AddOrUpdateUserAsync(
                    chatId,
                    message.From?.Username,
                    message.From?.FirstName
                );

                await traderService.FollowAllTradersAsync(newUser.Id);
                var allTradersCount = await traderService.GetAllTradersAsync();

                await _botClient.SendTextMessageAsync(
                    chatId: chatId,
                    text: $@"🎉 Welcome to FOMOFASTER!

You're now following all {allTradersCount.Count} traders by default, configure according to your preferences if needed:

/help - show available commands
/list - view all available traders
/mytraders - view traders youre following
/follow - follow specific traders
/unfollow - unfollow specific traders
/autofollow <on/off> - check/toggle auto-follow for new traders (starts ON by default)
/top - view top tokens (e.g., /top 1h, /top sol 1d, /top sol,monad 6h)

Follow us on twitter, stay tuned for major updates: https://x.com/FOMOFASTER_BOT
",
                    parseMode: ParseMode.Markdown
                );

                // Broadcast new user to dashboard via SignalR
                await _hubContext.Clients.All.SendAsync("UserJoined", new
                {
                    chatId = newUser.ChatId,
                    username = newUser.Username,
                    firstName = newUser.FirstName,
                    joinedAt = newUser.JoinedAt,
                    isActive = newUser.IsActive
                });

                _logger.LogInformation("User started bot: ChatId={ChatId}, Username={Username}",
                    chatId, message.From?.Username);
                break;

            case "/help":
                await _botClient.SendTextMessageAsync(
                    chatId: chatId,
                    text: @"📚 FOMOFASTER Commands:

/start - Subscribe to notifications
/help - Show this help message
/list - View all available traders
/mytraders - View traders you're following
/follow <ids/handles> - Follow traders (e.g., /follow 1,2,3 or /follow trader1,trader2)
/follow all - Follow all traders
/unfollow <ids/handles> - Unfollow traders (e.g., /unfollow 1,trader2)
/unfollow all - Unfollow all traders
/autofollow <on/off> - Check/toggle auto-follow for new traders (starts ON by default)
/top [chains] <period> - Top tokens (e.g., /top 1h, /top sol 1d, /top sol,monad 6h)

You'll only receive notifications from traders you follow!",
                    parseMode: ParseMode.Markdown
                );
                break;

            case "/list":
                var user = await userService.GetUserByChatIdAsync(chatId);

                if (user == null)
                {
                    await _botClient.SendTextMessageAsync(
                        chatId: chatId,
                        text: "❌ Please use /start first to register.",
                        parseMode: ParseMode.Markdown
                    );
                    break;
                }

                var allTraders = await traderService.GetAllTradersAsync();

                if (allTraders.Count == 0)
                {
                    await _botClient.SendTextMessageAsync(
                        chatId: chatId,
                        text: "📭 No traders in the system yet. They'll appear as notifications come in!",
                        parseMode: ParseMode.Markdown
                    );
                    break;
                }

                var traderLines = new List<string>();
                foreach (var trader in allTraders)
                {
                    var isFollowing = await traderService.IsFollowingAsync(user.Id, trader.Id);
                    var status = isFollowing ? "✅" : "❌";
                    traderLines.Add($"{trader.Id} - [{trader.Handle}](https://x.com/{trader.Handle}) {status}");
                }

                await _botClient.SendTextMessageAsync(
                    chatId: chatId,
                    text: $"📊 All Traders ({allTraders.Count} total) — Use /follow 1,2,3 or /follow trader1,trader2 to follow traders.",
                    parseMode: ParseMode.Markdown
                );

                const int chunkSize = 150;
                for (int i = 0; i < traderLines.Count; i += chunkSize)
                {
                    var chunk = traderLines.GetRange(i, Math.Min(chunkSize, traderLines.Count - i));
                    await _botClient.SendTextMessageAsync(
                        chatId: chatId,
                        text: string.Join("\n", chunk),
                        parseMode: ParseMode.Markdown,
                        disableWebPagePreview: true
                    );
                }
                break;

            case "/mytraders":
                var userForMyTraders = await userService.GetUserByChatIdAsync(chatId);

                if (userForMyTraders == null)
                {
                    await _botClient.SendTextMessageAsync(
                        chatId: chatId,
                        text: "❌ Please use /start first to register.",
                        parseMode: ParseMode.Markdown
                    );
                    break;
                }

                var followedTraders = await traderService.GetTradersByUserIdAsync(userForMyTraders.Id);

                if (followedTraders.Count == 0)
                {
                    await _botClient.SendTextMessageAsync(
                        chatId: chatId,
                        text: "📭 You're not following any traders yet.\n\nUse /list to see all available traders, then /follow to start following them!",
                        parseMode: ParseMode.Markdown
                    );
                    break;
                }

                var myTraderLines = new List<string>();
                foreach (var trader in followedTraders)
                {
                    myTraderLines.Add($"{trader.Id} - [{trader.Handle}](https://x.com/{trader.Handle}) ✅");
                }

                var myTradersMessage = $@"📊 Your Followed Traders ({followedTraders.Count} total)

{string.Join("\n", myTraderLines)}

Use /unfollow 1,2,3 or /unfollow trader1,trader2 to unfollow traders.";

                await _botClient.SendTextMessageAsync(
                    chatId: chatId,
                    text: myTradersMessage,
                    parseMode: ParseMode.Markdown
                );
                break;

            case "/follow":
                var userForFollow = await userService.GetUserByChatIdAsync(chatId);

                if (userForFollow == null)
                {
                    await _botClient.SendTextMessageAsync(
                        chatId: chatId,
                        text: "❌ Please use /start first to register.",
                        parseMode: ParseMode.Markdown
                    );
                    break;
                }

                var followArgs = message.Text?.Split(' ', 2);
                if (followArgs == null || followArgs.Length < 2 || string.IsNullOrWhiteSpace(followArgs[1]))
                {
                    await _botClient.SendTextMessageAsync(
                        chatId: chatId,
                        text: "❌ Please specify traders to follow.\n\nExamples:\n/follow 1,2,3\n/follow trader1,trader2\n/follow 1,trader2,3\n/follow all",
                        parseMode: ParseMode.Markdown
                    );
                    break;
                }

                var followInput = followArgs[1].Trim();

                // Handle /follow all
                if (followInput.Equals("all", StringComparison.OrdinalIgnoreCase))
                {
                    var followedCount = await traderService.FollowAllTradersAsync(userForFollow.Id);
                    var allTradersForFollow = await traderService.GetAllTradersAsync();

                    if (allTradersForFollow.Count == 0)
                    {
                        await _botClient.SendTextMessageAsync(
                            chatId: chatId,
                            text: "❌ No traders available to follow yet."
                        );
                        break;
                    }

                    if (followedCount == 0)
                    {
                        await _botClient.SendTextMessageAsync(
                            chatId: chatId,
                            text: $"You're already following all {allTradersForFollow.Count} traders."
                        );
                    }
                    else
                    {
                        await _botClient.SendTextMessageAsync(
                            chatId: chatId,
                            text: $"Now following all traders ({followedCount} new, {allTradersForFollow.Count} total)"
                        );
                    }
                    break;
                }
                var followParts = followInput.Split(',').Select(p => p.Trim()).Where(p => !string.IsNullOrEmpty(p)).ToList();

                if (followParts.Count == 0)
                {
                    await _botClient.SendTextMessageAsync(
                        chatId: chatId,
                        text: "❌ Please specify traders to follow.\n\nExamples:\n/follow 1,2,3\n/follow trader1,trader2",
                        parseMode: ParseMode.Markdown
                    );
                    break;
                }

                var followedNames = new List<string>();
                var alreadyFollowingNames = new List<string>();
                var notFoundList = new List<string>();

                foreach (var part in followParts)
                {
                    bool success;
                    string? traderHandle = null;

                    // Check if it's an ID (number) or handle
                    if (int.TryParse(part, out var traderId))
                    {
                        // Follow by ID
                        var trader = await traderService.GetTraderByIdAsync(traderId);
                        if (trader == null)
                        {
                            notFoundList.Add(part);
                            continue;
                        }
                        traderHandle = trader.Handle;
                        success = await traderService.FollowTraderAsync(userForFollow.Id, traderId);
                    }
                    else
                    {
                        // Follow by handle (strip @ if present)
                        var handle = part.TrimStart('@');
                        success = await traderService.FollowTraderByHandleAsync(userForFollow.Id, handle);

                        if (!success)
                        {
                            // Check if trader exists
                            var trader = await traderService.GetTraderByHandleIgnoreCaseAsync(handle);
                            if (trader == null)
                            {
                                notFoundList.Add(part);
                                continue;
                            }
                            // Trader exists but already following
                            alreadyFollowingNames.Add(trader.Handle);
                            continue;
                        }
                        traderHandle = handle;
                    }

                    if (success && traderHandle != null)
                        followedNames.Add(traderHandle);
                    else if (traderHandle != null)
                        alreadyFollowingNames.Add(traderHandle);
                }

                var followResultParts = new List<string>();
                if (followedNames.Count > 0)
                    followResultParts.Add($"Now following {string.Join(", ", followedNames)}");
                if (alreadyFollowingNames.Count > 0)
                    followResultParts.Add($"Already following {string.Join(", ", alreadyFollowingNames)}");
                if (notFoundList.Count > 0)
                    followResultParts.Add($"Not found: {string.Join(", ", notFoundList)}");

                var followResultMessage = string.Join("\n", followResultParts);

                await _botClient.SendTextMessageAsync(
                    chatId: chatId,
                    text: followResultMessage
                );
                break;

            case "/unfollow":
                var userForUnfollow = await userService.GetUserByChatIdAsync(chatId);

                if (userForUnfollow == null)
                {
                    await _botClient.SendTextMessageAsync(
                        chatId: chatId,
                        text: "❌ Please use /start first to register.",
                        parseMode: ParseMode.Markdown
                    );
                    break;
                }

                var unfollowArgs = message.Text?.Split(' ', 2);
                if (unfollowArgs == null || unfollowArgs.Length < 2 || string.IsNullOrWhiteSpace(unfollowArgs[1]))
                {
                    await _botClient.SendTextMessageAsync(
                        chatId: chatId,
                        text: "❌ Please specify traders to unfollow.\n\nExamples:\n/unfollow 1,2,3\n/unfollow trader1,trader2\n/unfollow 1,trader2,3\n/unfollow all",
                        parseMode: ParseMode.Markdown
                    );
                    break;
                }

                var unfollowInput = unfollowArgs[1].Trim();

                // Handle /unfollow all
                if (unfollowInput.Equals("all", StringComparison.OrdinalIgnoreCase))
                {
                    var unfollowedCount = await traderService.UnfollowAllTradersAsync(userForUnfollow.Id);

                    if (unfollowedCount == 0)
                    {
                        await _botClient.SendTextMessageAsync(
                            chatId: chatId,
                            text: "You're not following any traders."
                        );
                    }
                    else
                    {
                        await _botClient.SendTextMessageAsync(
                            chatId: chatId,
                            text: $"Unfollowed all traders ({unfollowedCount} total)"
                        );
                    }
                    break;
                }
                var unfollowParts = unfollowInput.Split(',').Select(p => p.Trim()).Where(p => !string.IsNullOrEmpty(p)).ToList();

                if (unfollowParts.Count == 0)
                {
                    await _botClient.SendTextMessageAsync(
                        chatId: chatId,
                        text: "❌ Please specify traders to unfollow.\n\nExamples:\n/unfollow 1,2,3\n/unfollow trader1,trader2",
                        parseMode: ParseMode.Markdown
                    );
                    break;
                }

                var unfollowedNames = new List<string>();
                var notFollowingNames = new List<string>();
                var unfollowNotFoundList = new List<string>();

                foreach (var part in unfollowParts)
                {
                    bool success;
                    string? traderHandle = null;

                    // Check if it's an ID (number) or handle
                    if (int.TryParse(part, out var traderId))
                    {
                        // Unfollow by ID
                        var trader = await traderService.GetTraderByIdAsync(traderId);
                        if (trader == null)
                        {
                            unfollowNotFoundList.Add(part);
                            continue;
                        }
                        traderHandle = trader.Handle;
                        success = await traderService.UnfollowTraderAsync(userForUnfollow.Id, traderId);
                    }
                    else
                    {
                        // Unfollow by handle (strip @ if present)
                        var handle = part.TrimStart('@');
                        success = await traderService.UnfollowTraderByHandleAsync(userForUnfollow.Id, handle);

                        if (!success)
                        {
                            // Check if trader exists
                            var trader = await traderService.GetTraderByHandleIgnoreCaseAsync(handle);
                            if (trader == null)
                            {
                                unfollowNotFoundList.Add(part);
                                continue;
                            }
                            // Trader exists but not following
                            notFollowingNames.Add(trader.Handle);
                            continue;
                        }
                        traderHandle = handle;
                    }

                    if (success && traderHandle != null)
                        unfollowedNames.Add(traderHandle);
                    else if (traderHandle != null)
                        notFollowingNames.Add(traderHandle);
                }

                var unfollowResultParts = new List<string>();
                if (unfollowedNames.Count > 0)
                    unfollowResultParts.Add($"Unfollowed {string.Join(", ", unfollowedNames)}");
                if (notFollowingNames.Count > 0)
                    unfollowResultParts.Add($"Weren't following {string.Join(", ", notFollowingNames)}");
                if (unfollowNotFoundList.Count > 0)
                    unfollowResultParts.Add($"Not found: {string.Join(", ", unfollowNotFoundList)}");

                var unfollowResultMessage = string.Join("\n", unfollowResultParts);

                await _botClient.SendTextMessageAsync(
                    chatId: chatId,
                    text: unfollowResultMessage
                );
                break;

            case "/autofollow":
                var userForAutoFollow = await userService.GetUserByChatIdAsync(chatId);

                if (userForAutoFollow == null)
                {
                    await _botClient.SendTextMessageAsync(
                        chatId: chatId,
                        text: "❌ Please use /start first to register.",
                        parseMode: ParseMode.Markdown
                    );
                    break;
                }

                var autoFollowArgs = message.Text?.Split(' ', 2);

                // Just /autofollow - show current status
                if (autoFollowArgs == null || autoFollowArgs.Length < 2 || string.IsNullOrWhiteSpace(autoFollowArgs[1]))
                {
                    var currentStatus = userForAutoFollow.AutoFollowNewTraders ? "ON" : "OFF";
                    await _botClient.SendTextMessageAsync(
                        chatId: chatId,
                        text: $"Your auto-follow for new traders is currently: {currentStatus}\n\nUse /autofollow on or /autofollow off to change it.",
                        parseMode: ParseMode.Markdown
                    );
                    break;
                }

                // /autofollow on/off - toggle the setting
                var autoFollowValue = autoFollowArgs[1].Trim().ToLower();

                if (autoFollowValue == "on")
                {
                    userForAutoFollow.AutoFollowNewTraders = true;
                    using var scope1 = _serviceProvider.CreateScope();
                    var dbContext1 = scope1.ServiceProvider.GetRequiredService<AppDbContext>();
                    var userToUpdate1 = await dbContext1.Users.FindAsync(userForAutoFollow.Id);
                    if (userToUpdate1 != null)
                    {
                        userToUpdate1.AutoFollowNewTraders = true;
                        await dbContext1.SaveChangesAsync();
                    }

                    await _botClient.SendTextMessageAsync(
                        chatId: chatId,
                        text: "✅ Auto-follow for new traders is now ON\n\nYou'll automatically follow any new traders added to the system.",
                        parseMode: ParseMode.Markdown
                    );
                }
                else if (autoFollowValue == "off")
                {
                    userForAutoFollow.AutoFollowNewTraders = false;
                    using var scope2 = _serviceProvider.CreateScope();
                    var dbContext2 = scope2.ServiceProvider.GetRequiredService<AppDbContext>();
                    var userToUpdate2 = await dbContext2.Users.FindAsync(userForAutoFollow.Id);
                    if (userToUpdate2 != null)
                    {
                        userToUpdate2.AutoFollowNewTraders = false;
                        await dbContext2.SaveChangesAsync();
                    }

                    await _botClient.SendTextMessageAsync(
                        chatId: chatId,
                        text: "❌ Auto-follow for new traders is now OFF\n\nYou won't automatically follow new traders added to the system.",
                        parseMode: ParseMode.Markdown
                    );
                }
                else
                {
                    await _botClient.SendTextMessageAsync(
                        chatId: chatId,
                        text: "❌ Invalid option. Use /autofollow on or /autofollow off",
                        parseMode: ParseMode.Markdown
                    );
                }
                break;

            // case "/ca":
            //     await _botClient.SendTextMessageAsync(
            //         chatId: chatId,
            //         text: $"`{TOKEN_CONTRACT_ADDRESS}`",
            //         parseMode: ParseMode.Markdown
            //     );
            //     break;

            case "/top":
                var topArgs = message.Text?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (topArgs == null || topArgs.Length < 2)
                {
                    await _botClient.SendTextMessageAsync(
                        chatId: chatId,
                        text: $"Usage: `/top [chains] <period>`\n\nExamples:\n`/top 1h` - All chains, 1 hour\n`/top sol 1d` - Solana only\n`/top sol,monad 6h` - Multiple chains\n\nChains: {ChainInfo.ChainListForHelp()}",
                        parseMode: ParseMode.Markdown
                    );
                    break;
                }

                // Parse arguments: chains and period can be in any order
                List<Chain> chainFilters = new();
                TimeSpan? period = null;
                string periodDisplay = "";

                foreach (var arg in topArgs.Skip(1))
                {
                    var argLower = arg.Trim().ToLower();

                    // Try parse as period first
                    if (argLower.EndsWith("h") && int.TryParse(argLower[..^1], out var hours) && hours > 0 && hours <= 168)
                    {
                        period = TimeSpan.FromHours(hours);
                        periodDisplay = hours == 1 ? "1 hour" : $"{hours} hours";
                    }
                    else if (argLower.EndsWith("d") && int.TryParse(argLower[..^1], out var days) && days > 0 && days <= 30)
                    {
                        period = TimeSpan.FromDays(days);
                        periodDisplay = days == 1 ? "1 day" : $"{days} days";
                    }
                    else
                    {
                        // Try parse as chain(s)
                        var chainParts = argLower.Split(',', StringSplitOptions.RemoveEmptyEntries);
                        foreach (var chainStr in chainParts)
                        {
                            var trimmed = chainStr.Trim();
                            Chain? parsedChain = ChainInfo.FromAlias(trimmed);

                            if (parsedChain.HasValue && !chainFilters.Contains(parsedChain.Value))
                            {
                                chainFilters.Add(parsedChain.Value);
                            }
                        }
                    }
                }

                // Default to 24h if no period specified
                if (period == null)
                {
                    period = TimeSpan.FromDays(1);
                    periodDisplay = "24 hours";
                }

                using (var scopeTop = _serviceProvider.CreateScope())
                {
                    var dbContextTop = scopeTop.ServiceProvider.GetRequiredService<AppDbContext>();
                    var cutoff = DateTime.UtcNow - period.Value;

                    // Build query with optional chain filter
                    var query = dbContextTop.Notifications
                        .Where(n => n.SentAt >= cutoff && n.Ticker != null);

                    if (chainFilters.Count > 0)
                    {
                        query = query.Where(n => n.Chain != null && chainFilters.Contains(n.Chain.Value));
                    }

                    // Query notifications in time range, group by ticker, get latest CA and Chain for each
                    var tokenStats = await query
                        .GroupBy(n => n.Ticker)
                        .Select(g => new
                        {
                            Ticker = g.Key,
                            TotalTrades = g.Count(),
                            BuyCount = g.Count(n => n.Message.Contains("bought")),
                            SellCount = g.Count(n => n.Message.Contains("sold")),
                            DepositCount = g.Count(n => n.Message.Contains("deposited")),
                            ContractAddress = g.OrderByDescending(n => n.SentAt)
                                .Select(n => n.ContractAddress)
                                .FirstOrDefault(ca => ca != null),
                            Chain = g.OrderByDescending(n => n.SentAt)
                                .Select(n => n.Chain)
                                .FirstOrDefault()
                        })
                        .OrderByDescending(x => x.TotalTrades)
                        .Take(20)
                        .ToListAsync();

                    if (tokenStats.Count == 0)
                    {
                        await _botClient.SendTextMessageAsync(
                            chatId: chatId,
                            text: $"📊 No token activity in the last {periodDisplay}.",
                            parseMode: ParseMode.Markdown
                        );
                        break;
                    }

                    var lines = new List<string>();
                    for (int i = 0; i < tokenStats.Count; i++)
                    {
                        var stat = tokenStats[i];
                        var medal = i switch { 0 => "🥇", 1 => "🥈", 2 => "🥉", _ => $"{i + 1}." };

                        string caDisplay = !string.IsNullOrEmpty(stat.ContractAddress)
                            ? $"\n`{stat.ContractAddress}`"
                            : "";

                        var chainStr = stat.Chain.HasValue ? $" - {stat.Chain.Value}" : "";
                        var depositPart = stat.DepositCount > 0 ? $", {stat.DepositCount} ➕" : "";
                        lines.Add($"{medal} *{stat.Ticker}*{chainStr} - {stat.TotalTrades} trades ({stat.BuyCount} 🟢, {stat.SellCount} 🔴{depositPart}){caDisplay}");
                    }

                    // Build header with chain filter info
                    var chainInfo = chainFilters.Count > 0
                        ? $" ({string.Join(", ", chainFilters)})"
                        : " (All Chains)";
                    var topMessage = $"📊 *Top Tokens*{chainInfo} (Last {periodDisplay})\n\n{string.Join("\n\n", lines)}";

                    await _botClient.SendTextMessageAsync(
                        chatId: chatId,
                        text: topMessage,
                        parseMode: ParseMode.Markdown
                    );
                }
                break;

            case "/subscribe":
                using (var subscribeScope = _serviceProvider.CreateScope())
                {
                    var dbContext = subscribeScope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var appConfig = subscribeScope.ServiceProvider.GetRequiredService<AppConfigService>();
                    var subscribeUser = await userService.GetUserByChatIdAsync(chatId);

                    if (subscribeUser == null) break;

                    // Already active RN
                    if (subscribeUser.IsRN4L || (subscribeUser.IsRegisteredNurse && subscribeUser.RNExpiresAt > DateTime.UtcNow))
                    {
                        var until = subscribeUser.IsRN4L ? "forever" : subscribeUser.RNExpiresAt!.Value.ToString("yyyy-MM-dd HH:mm UTC");
                        await _botClient.SendTextMessageAsync(
                            chatId: chatId,
                            text: $"✅ You already have full access ({until}).",
                            parseMode: ParseMode.Markdown
                        );
                        break;
                    }

                    var priceSol = await appConfig.GetSubscriptionPriceSolAsync();
                    var priceDisplay = priceSol.ToString("0.##").Replace(".", "\\.");

                    // Check for existing unexpired unconfirmed payment
                    var existing = await dbContext.PendingPayments
                        .Where(p => p.ChatId == chatId && !p.IsConfirmed && p.ExpiresAt > DateTime.UtcNow)
                        .OrderByDescending(p => p.CreatedAt)
                        .FirstOrDefaultAsync();

                    if (existing != null)
                    {
                        var timeLeft = existing.ExpiresAt - DateTime.UtcNow;
                        var expiryDisplay = timeLeft.TotalMinutes >= 60
                            ? $"{(int)timeLeft.TotalHours}h {timeLeft.Minutes}m"
                            : $"{(int)timeLeft.TotalMinutes}m";
                        await _botClient.SendTextMessageAsync(
                            chatId: chatId,
                            text: $"💳 You have a pending payment \\(expires in {expiryDisplay}\\)\\. Send {priceDisplay} SOL to:\n\n`{existing.WalletPublicKey}`\n\nGrants 30 days of full access, automatically within seconds of payment\\.\nRefundable within the first 3 days — just message us\\.",
                            parseMode: ParseMode.MarkdownV2
                        );
                        break;
                    }

                    // Generate new Solana keypair
                    var keypair = GenerateSolanaKeypair();

                    var pending = new PendingPayment
                    {
                        ChatId = chatId,
                        WalletPublicKey = keypair.PublicKey,
                        WalletPrivateKey = keypair.PrivateKey,
                        AmountSol = priceSol,
                        CreatedAt = DateTime.UtcNow,
                        ExpiresAt = DateTime.UtcNow.AddHours(1),
                        IsConfirmed = false
                    };

                    dbContext.PendingPayments.Add(pending);
                    await dbContext.SaveChangesAsync();

                    await _botClient.SendTextMessageAsync(
                        chatId: chatId,
                        text: $"💳 Send {priceDisplay} SOL to:\n\n`{keypair.PublicKey}`\n\nGrants 30 days of full access, automatically within seconds of payment\\.\nRefundable within the first 7 days\\.\nThis address expires in 1 hour\\.",
                        parseMode: ParseMode.MarkdownV2
                    );
                }
                break;

            default:
                await _botClient.SendTextMessageAsync(
                    chatId: chatId,
                    text: "❓ Unknown command. Use /help to see available commands.",
                    parseMode: ParseMode.Markdown
                );
                break;
        }
    }

    private static (string PublicKey, string PrivateKey) GenerateSolanaKeypair()
    {
        var wallet = new Solnet.Wallet.Wallet(
            Solnet.Wallet.Bip39.WordCount.TwentyFour,
            Solnet.Wallet.Bip39.WordList.English
        );
        var account = wallet.Account;
        return (
            PublicKey: account.PublicKey.Key,
            PrivateKey: Convert.ToBase64String(account.PrivateKey.KeyBytes)
        );
    }
}
