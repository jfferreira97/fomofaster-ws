using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
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

    // ChatId -> chain currently awaiting a typed min-market-cap reply (via /chains' $ button,
    // which sends a ForceReply prompt since Telegram buttons can't accept text input directly).
    // Cleared once the reply is consumed. In-memory only — losing a pending prompt on restart
    // just means the user re-taps the button, no real cost.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, Chain> _pendingChainMcapInput = new();

    // GROUPCHAT token contract address - update this when token launches
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
            else if (update.CallbackQuery is { } callbackQuery)
            {
                await HandleCallbackQueryAsync(callbackQuery);
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
        else if (_pendingChainMcapInput.TryRemove(chatId, out var pendingChain))
        {
            await HandleChainMcapReplyAsync(message, pendingChain, userService);
        }
        else
        {
            await HandleFreeTextAsync(message);
        }
    }

    private async Task HandleChainMcapReplyAsync(Message message, Chain chain, IUserService userService)
    {
        if (_botClient == null) return;

        var chatId = message.Chat.Id;
        var user = await userService.GetUserByChatIdAsync(chatId);
        if (user == null) return;

        if (!TryParseMarketCapArg(message.Text ?? "", out var value))
        {
            await _botClient.SendTextMessageAsync(
                chatId: chatId,
                text: "❌ Invalid amount. Send a number like 50k, 1.2m, or 0 for none. Tap the $ button on /chains to try again."
            );
            return;
        }

        using var scope = _serviceProvider.CreateScope();
        var chainSettingsServiceForReply = scope.ServiceProvider.GetRequiredService<IChainSettingsService>();
        await chainSettingsServiceForReply.SetMinMarketCapAsync(user.Id, chain, value);

        var confirmText = value.HasValue
            ? $"✅ Minimum market cap for {chain} set to ${value.Value:N0}"
            : $"✅ Minimum market cap for {chain} cleared (no minimum)";

        var updatedSettings = chainSettingsServiceForReply.GetSettingsForUser(user.Id);
        await _botClient.SendTextMessageAsync(
            chatId: chatId,
            text: $"{confirmText}\n\n{ChainsText}",
            parseMode: ParseMode.Markdown,
            replyMarkup: BuildChainsKeyboard(updatedSettings)
        );
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

    private static string OnOff(bool on) => on ? "✅" : "❌";
    private static string ModeWord(bool verifiedOnly) => verifiedOnly ? "Verified Only" : "All";

    private static string BuildSettingsText(Models.User user) => "⚙️ *Notification Settings* — tap a button below to toggle.";

    // Two columns (FOMO left, Pump right) stacked to the same height for visual symmetry,
    // then one full-width row for Trending. Pump's mode (All/Verified Only) is a single
    // shared toggle — its current value is echoed in both the Pump Auto-Follow and
    // Callouts labels, and governs BOTH which new Pump traders get auto-followed and
    // which Pump notifications get delivered (see TraderService/TelegramService).
    private static InlineKeyboardMarkup BuildSettingsKeyboard(Models.User user)
    {
        var mode = ModeWord(user.PumpVerifiedOnly);
        return new(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData($"FOMO Auto-Follow: {OnOff(user.AutoFollowFomoTraders)}", "settings:af_fomo"),
                InlineKeyboardButton.WithCallbackData($"Pump Auto-Follow ({mode}): {OnOff(user.AutoFollowPumpTraders)}", "settings:af_pump"),
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData($"Buys/Sells: {OnOff(user.NotifyFomoBuySell)}", "settings:fomo_bs"),
                InlineKeyboardButton.WithCallbackData($"Callouts ({mode}): {OnOff(user.NotifyPumpCallouts)}", "settings:pump_callouts"),
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData($"Thesis: {OnOff(user.NotifyFomoThesis)}", "settings:fomo_thesis"),
                InlineKeyboardButton.WithCallbackData($"Mode: {mode}", "settings:pump_mode"),
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData($"🔥 Trending Alerts: {OnOff(user.NotifyTrending)}", "settings:trending"),
            },
        });
    }

    private static string FormatMarketCapShort(decimal value)
    {
        if (value >= 1_000_000m) return $"${value / 1_000_000m:0.#}M";
        if (value >= 1_000m) return $"${value / 1_000m:0.#}K";
        return "$0";
    }

    private const string ChainsText = "⛓ *Chain Settings*\n\nTap a chain (1st button) to enable/disable it entirely. Tap $ (2nd) to type a new minimum market cap floor — 0 means no minimum. Tap 🔥 (3rd) to mute just Trending alerts for that chain.";

    private static InlineKeyboardMarkup BuildChainsKeyboard(Dictionary<Chain, UserChainSetting> settings)
    {
        var rows = new List<InlineKeyboardButton[]>();
        foreach (var chain in Enum.GetValues<Chain>())
        {
            settings.TryGetValue(chain, out var s);
            var isDisabled = s?.IsDisabled ?? false;
            var minMarketCap = s?.MinMarketCap ?? 0m;
            var trendingDisabled = s?.TrendingDisabled ?? false;

            rows.Add(new[]
            {
                InlineKeyboardButton.WithCallbackData($"{(isDisabled ? "🔴" : "🟢")} {chain}", $"chains:toggle:{chain}"),
                InlineKeyboardButton.WithCallbackData($"Min: {FormatMarketCapShort(minMarketCap)}", $"chains:mcap:{chain}"),
                InlineKeyboardButton.WithCallbackData($"🔥 {(trendingDisabled ? "🔴" : "🟢")}", $"chains:trend:{chain}"),
            });
        }
        return new InlineKeyboardMarkup(rows);
    }

    private async Task HandleChainsCallbackAsync(CallbackQuery callbackQuery, string data, Message message)
    {
        if (_botClient == null) return;

        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var chainSettingsServiceForCallback = scope.ServiceProvider.GetRequiredService<IChainSettingsService>();

        var user = await dbContext.Users.FirstOrDefaultAsync(u => u.ChatId == message.Chat.Id);
        if (user == null)
        {
            await _botClient.AnswerCallbackQueryAsync(callbackQuery.Id, "Please use /start first.");
            return;
        }

        // "chains:toggle:SOL", "chains:mcap:SOL", or "chains:trend:SOL"
        var parts = data.Split(':');
        if (parts.Length != 3 || !Enum.TryParse<Chain>(parts[2], out var chain))
        {
            await _botClient.AnswerCallbackQueryAsync(callbackQuery.Id);
            return;
        }

        if (parts[1] == "mcap")
        {
            // Inline buttons can't accept typed input — prompt with a ForceReply instead and
            // remember which chain this chat is answering for; HandleChainMcapReplyAsync picks
            // it up off their next plain-text message.
            _pendingChainMcapInput[message.Chat.Id] = chain;
            await _botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: $"💬 Reply with the minimum market cap for {chain} (e.g. 50k, 1.2m, or 0 for none):",
                replyMarkup: new ForceReplyMarkup { Selective = true }
            );
            await _botClient.AnswerCallbackQueryAsync(callbackQuery.Id);
            return;
        }

        var settings = chainSettingsServiceForCallback.GetSettingsForUser(user.Id);
        settings.TryGetValue(chain, out var existing);

        switch (parts[1])
        {
            case "toggle":
                await chainSettingsServiceForCallback.SetDisabledAsync(user.Id, chain, !(existing?.IsDisabled ?? false));
                break;
            case "trend":
                await chainSettingsServiceForCallback.SetTrendingDisabledAsync(user.Id, chain, !(existing?.TrendingDisabled ?? false));
                break;
            default:
                await _botClient.AnswerCallbackQueryAsync(callbackQuery.Id);
                return;
        }

        var updatedSettings = chainSettingsServiceForCallback.GetSettingsForUser(user.Id);
        await _botClient.EditMessageTextAsync(
            chatId: message.Chat.Id,
            messageId: message.MessageId,
            text: ChainsText,
            parseMode: ParseMode.Markdown,
            replyMarkup: BuildChainsKeyboard(updatedSettings)
        );

        await _botClient.AnswerCallbackQueryAsync(callbackQuery.Id);
    }

    private async Task HandleCallbackQueryAsync(CallbackQuery callbackQuery)
    {
        if (_botClient == null) return;

        var data = callbackQuery.Data;
        var message = callbackQuery.Message;
        if (message == null || string.IsNullOrEmpty(data))
        {
            await _botClient.AnswerCallbackQueryAsync(callbackQuery.Id);
            return;
        }

        if (data.StartsWith("chains:"))
        {
            await HandleChainsCallbackAsync(callbackQuery, data, message);
            return;
        }

        if (!data.StartsWith("settings:"))
        {
            await _botClient.AnswerCallbackQueryAsync(callbackQuery.Id);
            return;
        }

        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await dbContext.Users.FirstOrDefaultAsync(u => u.ChatId == message.Chat.Id);
        if (user == null)
        {
            await _botClient.AnswerCallbackQueryAsync(callbackQuery.Id, "Please use /start first.");
            return;
        }

        switch (data)
        {
            case "settings:af_fomo": user.AutoFollowFomoTraders = !user.AutoFollowFomoTraders; break;
            case "settings:af_pump": user.AutoFollowPumpTraders = !user.AutoFollowPumpTraders; break;
            case "settings:fomo_bs": user.NotifyFomoBuySell = !user.NotifyFomoBuySell; break;
            case "settings:fomo_thesis": user.NotifyFomoThesis = !user.NotifyFomoThesis; break;
            case "settings:pump_callouts": user.NotifyPumpCallouts = !user.NotifyPumpCallouts; break;
            case "settings:pump_mode": user.PumpVerifiedOnly = !user.PumpVerifiedOnly; break;
            case "settings:trending": user.NotifyTrending = !user.NotifyTrending; break;
            default:
                await _botClient.AnswerCallbackQueryAsync(callbackQuery.Id);
                return;
        }

        await dbContext.SaveChangesAsync();

        await _botClient.EditMessageTextAsync(
            chatId: message.Chat.Id,
            messageId: message.MessageId,
            text: BuildSettingsText(user),
            parseMode: ParseMode.Markdown,
            replyMarkup: BuildSettingsKeyboard(user)
        );

        await _botClient.AnswerCallbackQueryAsync(callbackQuery.Id);
    }

    private async Task HandleCommandAsync(Message message, IUserService userService)
    {
        if (_botClient == null)
            return;

        var chatId = message.Chat.Id;
        var command = message.Text?.Split(' ')[0].ToLower();

        using var scope = _serviceProvider.CreateScope();
        var traderService = scope.ServiceProvider.GetRequiredService<ITraderService>();
        var chainSettingsService = scope.ServiceProvider.GetRequiredService<IChainSettingsService>();

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
                    text: $@"🎉 Welcome to GROUPCHAT!

You're now following all {allTradersCount.Count} traders by default, configure according to your preferences if needed:

/help - show available commands
/manage - open the web page to browse traders, see who you follow, and manage alerts
/follow - follow specific traders
/unfollow - unfollow specific traders
/autofollow <on/off> - check/toggle auto-follow for new traders (starts ON by default)
/settings - full notification menu: auto-follow, buys/sells, thesis, pump callouts, verified-only mode, trending
/repeatwindow <2h/30m/off> - limit repeat buy/sell alerts per trader+coin — buys and sells don't block each other (off by default)
/chains - tap-button menu to enable/disable chains and set a minimum market cap per chain
/top - view top tokens (e.g., /top 1h, /top sol 1d, /top sol,monad 6h)

Follow us on twitter, stay tuned for major updates: https://x.com/groupchat__BOT

{BuildSettingsText(newUser)}",
                    parseMode: ParseMode.Markdown,
                    replyMarkup: BuildSettingsKeyboard(newUser)
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
                    text: @"📚 GROUPCHAT Commands:

/start - Subscribe to notifications
/help - Show this help message
/manage - Open the web page to browse traders, see who you follow, and manage alerts
/follow <ids/handles> - Follow traders (e.g., /follow 1,2,3 or /follow trader1,trader2)
/follow all - Follow all traders
/unfollow <ids/handles> - Unfollow traders (e.g., /unfollow 1,trader2)
/unfollow all - Unfollow all traders
/autofollow <on/off> - Check/toggle FOMO auto-follow for new traders (starts ON by default)
/settings - Full notification menu: auto-follow (FOMO/Pump), buys/sells, thesis, pump callouts, verified-only mode, trending
/repeatwindow <2h/30m/off> - Limit repeat buy/sell alerts per trader+coin — buys and sells don't block each other (off by default)
/chains - Tap-button menu: enable/disable each chain, cycle its minimum market cap floor (also: /chains disable base, /chains minmcap sol 50k)
/top [chains] <period> - Top tokens (e.g., /top 1h, /top sol 1d, /top sol,monad 6h)

You'll only receive notifications from traders you follow!",
                    parseMode: ParseMode.Markdown
                );
                break;

            case "/manage":
                await _botClient.SendTextMessageAsync(
                    chatId: chatId,
                    text: "Manage your followed traders and per-trader alert thresholds here:",
                    replyMarkup: new InlineKeyboardMarkup(
                        InlineKeyboardButton.WithUrl("Open Manage Page", "https://groupchat-bot.tech/manage"))
                );
                break;

            // Retired 2026-09-03 — browsing/filtering the full trader roster is now 100%
            // handled by the manage page (search, platform filter, follow, thresholds, all
            // in one scrollable table instead of chunked wall-of-text messages).
            case "/list":
                await _botClient.SendTextMessageAsync(
                    chatId: chatId,
                    text: "The full trader list now lives on the manage page — search, filter by platform, and follow/unfollow from there.",
                    replyMarkup: new InlineKeyboardMarkup(
                        InlineKeyboardButton.WithUrl("Open Manage Page", "https://groupchat-bot.tech/manage"))
                );
                break;

            // Retired 2026-09-03, same as /list — the manage page already shows follow
            // state inline for every trader, so a separate "just the ones I follow" view
            // is redundant with it.
            case "/mytraders":
                await _botClient.SendTextMessageAsync(
                    chatId: chatId,
                    text: "Your followed traders are on the manage page now — same table as everyone else, just check who's followed.",
                    replyMarkup: new InlineKeyboardMarkup(
                        InlineKeyboardButton.WithUrl("Open Manage Page", "https://groupchat-bot.tech/manage"))
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
                        // Follow by handle (strip @ if present). "pump:handle" targets the
                        // Pump platform; a bare handle defaults to FOMO as it always has.
                        var raw = part.TrimStart('@');
                        var platform = Platform.Fomo;
                        if (raw.StartsWith("pump:", StringComparison.OrdinalIgnoreCase))
                        {
                            platform = Platform.Pump;
                            raw = raw["pump:".Length..];
                        }
                        var handle = raw;
                        success = await traderService.FollowTraderByHandleAsync(userForFollow.Id, handle, platform);

                        if (!success)
                        {
                            // Check if trader exists
                            var trader = await traderService.GetTraderByHandleIgnoreCaseAsync(handle, platform);
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
                        // Unfollow by handle (strip @ if present). Same "pump:handle" convention as /follow.
                        var raw = part.TrimStart('@');
                        var platform = Platform.Fomo;
                        if (raw.StartsWith("pump:", StringComparison.OrdinalIgnoreCase))
                        {
                            platform = Platform.Pump;
                            raw = raw["pump:".Length..];
                        }
                        var handle = raw;
                        success = await traderService.UnfollowTraderByHandleAsync(userForUnfollow.Id, handle, platform);

                        if (!success)
                        {
                            // Check if trader exists
                            var trader = await traderService.GetTraderByHandleIgnoreCaseAsync(handle, platform);
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
                    var currentStatus = userForAutoFollow.AutoFollowFomoTraders ? "ON" : "OFF";
                    await _botClient.SendTextMessageAsync(
                        chatId: chatId,
                        text: $"Your FOMO auto-follow for new traders is currently: {currentStatus}\n\nUse /autofollow on or /autofollow off to change it, or /settings for the full menu (Pump auto-follow, notification types, etc.).",
                        parseMode: ParseMode.Markdown
                    );
                    break;
                }

                // /autofollow on/off - toggle the setting (FOMO only; use /settings for Pump)
                var autoFollowValue = autoFollowArgs[1].Trim().ToLower();

                if (autoFollowValue == "on")
                {
                    userForAutoFollow.AutoFollowFomoTraders = true;
                    using var scope1 = _serviceProvider.CreateScope();
                    var dbContext1 = scope1.ServiceProvider.GetRequiredService<AppDbContext>();
                    var userToUpdate1 = await dbContext1.Users.FindAsync(userForAutoFollow.Id);
                    if (userToUpdate1 != null)
                    {
                        userToUpdate1.AutoFollowFomoTraders = true;
                        await dbContext1.SaveChangesAsync();
                    }

                    await _botClient.SendTextMessageAsync(
                        chatId: chatId,
                        text: "✅ FOMO auto-follow for new traders is now ON\n\nYou'll automatically follow any new FOMO traders added to the system. (Use /settings to manage Pump too.)",
                        parseMode: ParseMode.Markdown
                    );
                }
                else if (autoFollowValue == "off")
                {
                    userForAutoFollow.AutoFollowFomoTraders = false;
                    using var scope2 = _serviceProvider.CreateScope();
                    var dbContext2 = scope2.ServiceProvider.GetRequiredService<AppDbContext>();
                    var userToUpdate2 = await dbContext2.Users.FindAsync(userForAutoFollow.Id);
                    if (userToUpdate2 != null)
                    {
                        userToUpdate2.AutoFollowFomoTraders = false;
                        await dbContext2.SaveChangesAsync();
                    }

                    await _botClient.SendTextMessageAsync(
                        chatId: chatId,
                        text: "❌ FOMO auto-follow for new traders is now OFF\n\nYou won't automatically follow new FOMO traders added to the system. (Use /settings to manage Pump too.)",
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

            case "/settings":
                var userForSettings = await userService.GetUserByChatIdAsync(chatId);

                if (userForSettings == null)
                {
                    await _botClient.SendTextMessageAsync(
                        chatId: chatId,
                        text: "❌ Please use /start first to register.",
                        parseMode: ParseMode.Markdown
                    );
                    break;
                }

                await _botClient.SendTextMessageAsync(
                    chatId: chatId,
                    text: BuildSettingsText(userForSettings),
                    parseMode: ParseMode.Markdown,
                    replyMarkup: BuildSettingsKeyboard(userForSettings)
                );
                break;

            case "/repeatwindow":
                var userForRepeatWindow = await userService.GetUserByChatIdAsync(chatId);

                if (userForRepeatWindow == null)
                {
                    await _botClient.SendTextMessageAsync(
                        chatId: chatId,
                        text: "❌ Please use /start first to register.",
                        parseMode: ParseMode.Markdown
                    );
                    break;
                }

                var repeatWindowArgs = message.Text?.Split(' ', 2);

                // Just /repeatwindow - show current setting
                if (repeatWindowArgs == null || repeatWindowArgs.Length < 2 || string.IsNullOrWhiteSpace(repeatWindowArgs[1]))
                {
                    var currentDisplay = userForRepeatWindow.RepeatWindowMinutes <= 0
                        ? "OFF (every trade notifies, even repeats)"
                        : FormatRepeatWindow(userForRepeatWindow.RepeatWindowMinutes);
                    await _botClient.SendTextMessageAsync(
                        chatId: chatId,
                        text: $"Your repeat window is currently: {currentDisplay}\n\nWithin this window, a trader can trigger at most one BUY alert and one SELL alert per coin — buys and sells are tracked separately, so you'll still get notified for both sides. After the window elapses, a new trade on that coin alerts again.\n\nUse /repeatwindow 2h (or 30m, or off) to change it.",
                        parseMode: ParseMode.Markdown
                    );
                    break;
                }

                var repeatWindowInput = repeatWindowArgs[1].Trim().ToLower();
                int? newRepeatWindowMinutes = null;

                if (repeatWindowInput == "off")
                {
                    newRepeatWindowMinutes = 0;
                }
                else if (repeatWindowInput.EndsWith("h") && int.TryParse(repeatWindowInput[..^1], out var hoursVal) && hoursVal > 0 && hoursVal <= 168)
                {
                    newRepeatWindowMinutes = hoursVal * 60;
                }
                else if (repeatWindowInput.EndsWith("m") && int.TryParse(repeatWindowInput[..^1], out var minsVal) && minsVal > 0 && minsVal <= 10080)
                {
                    newRepeatWindowMinutes = minsVal;
                }
                else if (int.TryParse(repeatWindowInput, out var plainMinsVal) && plainMinsVal > 0 && plainMinsVal <= 10080)
                {
                    newRepeatWindowMinutes = plainMinsVal;
                }

                if (newRepeatWindowMinutes == null)
                {
                    await _botClient.SendTextMessageAsync(
                        chatId: chatId,
                        text: "❌ Invalid value. Examples: /repeatwindow 2h, /repeatwindow 30m, /repeatwindow off",
                        parseMode: ParseMode.Markdown
                    );
                    break;
                }

                using (var scopeRepeatWindow = _serviceProvider.CreateScope())
                {
                    var dbContextRepeatWindow = scopeRepeatWindow.ServiceProvider.GetRequiredService<AppDbContext>();
                    var userToUpdateRepeatWindow = await dbContextRepeatWindow.Users.FindAsync(userForRepeatWindow.Id);
                    if (userToUpdateRepeatWindow != null)
                    {
                        userToUpdateRepeatWindow.RepeatWindowMinutes = newRepeatWindowMinutes.Value;
                        await dbContextRepeatWindow.SaveChangesAsync();
                    }
                }

                var newDisplay = newRepeatWindowMinutes <= 0
                    ? "OFF (every trade notifies, even repeats)"
                    : FormatRepeatWindow(newRepeatWindowMinutes.Value);
                await _botClient.SendTextMessageAsync(
                    chatId: chatId,
                    text: $"✅ Repeat window set to: {newDisplay}",
                    parseMode: ParseMode.Markdown
                );
                break;

            case "/chains":
                var userForChains = await userService.GetUserByChatIdAsync(chatId);

                if (userForChains == null)
                {
                    await _botClient.SendTextMessageAsync(
                        chatId: chatId,
                        text: "❌ Please use /start first to register.",
                        parseMode: ParseMode.Markdown
                    );
                    break;
                }

                var chainsArgs = message.Text?.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                // Just /chains - show the tap-to-toggle button menu
                if (chainsArgs == null || chainsArgs.Length < 2)
                {
                    var currentChainSettings = chainSettingsService.GetSettingsForUser(userForChains.Id);
                    await _botClient.SendTextMessageAsync(
                        chatId: chatId,
                        text: ChainsText,
                        parseMode: ParseMode.Markdown,
                        replyMarkup: BuildChainsKeyboard(currentChainSettings)
                    );
                    break;
                }

                var chainsSubcommand = chainsArgs[1].ToLowerInvariant();

                if (chainsSubcommand is "enable" or "disable")
                {
                    if (chainsArgs.Length < 3)
                    {
                        await _botClient.SendTextMessageAsync(
                            chatId: chatId,
                            text: $"❌ Usage: /chains {chainsSubcommand} <chain>[,<chain2>...]",
                            parseMode: ParseMode.Markdown
                        );
                        break;
                    }

                    var wantDisabled = chainsSubcommand == "disable";
                    var chainNames = chainsArgs[2].Split(',', StringSplitOptions.RemoveEmptyEntries);
                    var appliedChains = new List<Chain>();
                    var unrecognizedChains = new List<string>();

                    foreach (var name in chainNames)
                    {
                        var parsedChain = ChainInfo.FromAlias(name.Trim());
                        if (parsedChain.HasValue)
                        {
                            await chainSettingsService.SetDisabledAsync(userForChains.Id, parsedChain.Value, wantDisabled);
                            appliedChains.Add(parsedChain.Value);
                        }
                        else
                        {
                            unrecognizedChains.Add(name.Trim());
                        }
                    }

                    var chainsResultLines = new List<string>();
                    if (appliedChains.Count > 0)
                        chainsResultLines.Add($"{(wantDisabled ? "❌ Disabled" : "✅ Enabled")}: {string.Join(", ", appliedChains)}");
                    if (unrecognizedChains.Count > 0)
                        chainsResultLines.Add($"⚠️ Unrecognized chain(s): {string.Join(", ", unrecognizedChains)}. Valid: {ChainInfo.ChainListForHelp()}");

                    await _botClient.SendTextMessageAsync(
                        chatId: chatId,
                        text: string.Join("\n", chainsResultLines),
                        parseMode: ParseMode.Markdown
                    );
                    break;
                }

                if (chainsSubcommand is "minmcap" or "minmarketcap")
                {
                    if (chainsArgs.Length < 4)
                    {
                        await _botClient.SendTextMessageAsync(
                            chatId: chatId,
                            text: "❌ Usage: /chains minmcap <chain> <amount> (e.g. /chains minmcap sol 50k, or /chains minmcap sol off)",
                            parseMode: ParseMode.Markdown
                        );
                        break;
                    }

                    var minMcapChain = ChainInfo.FromAlias(chainsArgs[2].Trim());
                    if (!minMcapChain.HasValue)
                    {
                        await _botClient.SendTextMessageAsync(
                            chatId: chatId,
                            text: $"❌ Unrecognized chain '{chainsArgs[2]}'. Valid: {ChainInfo.ChainListForHelp()}",
                            parseMode: ParseMode.Markdown
                        );
                        break;
                    }

                    if (!TryParseMarketCapArg(chainsArgs[3], out var minMcapValue))
                    {
                        await _botClient.SendTextMessageAsync(
                            chatId: chatId,
                            text: "❌ Invalid amount. Examples: 50k, 1.2m, 250000, off",
                            parseMode: ParseMode.Markdown
                        );
                        break;
                    }

                    await chainSettingsService.SetMinMarketCapAsync(userForChains.Id, minMcapChain.Value, minMcapValue);

                    var minMcapConfirmText = minMcapValue.HasValue
                        ? $"✅ Minimum market cap for {minMcapChain.Value} set to ${minMcapValue.Value:N0}"
                        : $"✅ Minimum market cap for {minMcapChain.Value} cleared";
                    await _botClient.SendTextMessageAsync(
                        chatId: chatId,
                        text: minMcapConfirmText,
                        parseMode: ParseMode.Markdown
                    );
                    break;
                }

                await _botClient.SendTextMessageAsync(
                    chatId: chatId,
                    text: "❌ Unknown /chains subcommand. Use:\n/chains — show status\n/chains disable <chain>\n/chains enable <chain>\n/chains minmcap <chain> <amount>",
                    parseMode: ParseMode.Markdown
                );
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

    // "off"/"none" clear the floor (value = null, returns true). Otherwise parses a plain
    // or k/m/b-suffixed number (e.g. "50k", "1.2m") into a dollar amount.
    private static bool TryParseMarketCapArg(string input, out decimal? value)
    {
        var s = input.Trim().ToLowerInvariant();
        if (s is "off" or "none" or "clear")
        {
            value = null;
            return true;
        }

        var multiplier = 1m;
        if (s.EndsWith("k")) { multiplier = 1_000m; s = s[..^1]; }
        else if (s.EndsWith("m")) { multiplier = 1_000_000m; s = s[..^1]; }
        else if (s.EndsWith("b")) { multiplier = 1_000_000_000m; s = s[..^1]; }

        if (decimal.TryParse(s, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var num) && num >= 0)
        {
            value = num * multiplier;
            return true;
        }

        value = null;
        return false;
    }

    private static string FormatRepeatWindow(int minutes)
    {
        if (minutes % 60 == 0)
        {
            var hours = minutes / 60;
            return hours == 1 ? "1 hour" : $"{hours} hours";
        }
        return minutes == 1 ? "1 minute" : $"{minutes} minutes";
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
