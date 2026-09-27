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

    // Which bot identity this instance is polling. During the GROUPCHAT relaunch, two
    // instances of this class run side by side — one per token — sharing everything else
    // (DB, services). Only /start's IsOnNewBot flip cares about the distinction.
    private readonly bool _isNewBotInstance;
    private int _offset = 0;
    private long _ownerChatId;
    private string? _ownerUsername;

    // ChatId -> chain currently awaiting a typed min-market-cap reply (via /chains' $ button,
    // which sends a ForceReply prompt since Telegram buttons can't accept text input directly).
    // Cleared once the reply is consumed. In-memory only — losing a pending prompt on restart
    // just means the user re-taps the button, no real cost.

    // GROUPCHAT token contract address - update this when token launches
    // private const string TOKEN_CONTRACT_ADDRESS = "6gCEGUjPisdGFc6FhRGL43hoD263dRF81i2L3bo5bonk";

    public TelegramBotPollingService(
        IOptions<TelegramSettings> settings,
        IServiceProvider serviceProvider,
        ILogger<TelegramBotPollingService> logger,
        IHubContext<DashboardHub> hubContext,
        string? botToken = null,
        bool isNewBotInstance = true)
    {
        _settings = settings.Value;
        _serviceProvider = serviceProvider;
        _logger = logger;
        _hubContext = hubContext;
        _isNewBotInstance = isNewBotInstance;

        if (!string.IsNullOrEmpty(_settings.AdminBotToken))
            _adminBotClient = new TelegramBotClient(_settings.AdminBotToken);

        // botToken lets Program.cs stand up a second instance of this same service against
        // the deprecated bot during the relaunch migration; omitted (normal/single-bot case)
        // falls back to the configured primary token, same as before this existed.
        var effectiveToken = botToken ?? _settings.BotToken;
        if (!string.IsNullOrEmpty(effectiveToken))
        {
            _botClient = new TelegramBotClient(effectiveToken);
            _logger.LogInformation("Telegram polling service initialized ({Identity})", _isNewBotInstance ? "new bot" : "deprecated bot");
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
            var chatId = update.Message?.Chat.Id ?? update.CallbackQuery?.Message?.Chat.Id;
            if (chatId.HasValue)
                await TouchLastActiveAsync(chatId.Value);

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

    // No-op if the user doesn't exist yet (e.g. their very first /start, before the User row
    // is created) — that's fine, JoinedAt already covers "brand new" and their next message
    // picks LastActiveAt up from here on.
    private async Task TouchLastActiveAsync(long chatId)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Users.Where(u => u.ChatId == chatId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.LastActiveAt, DateTime.UtcNow));
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

        if (message.ReplyToMessage != null && await TryHandleNotificationReplyAsync(message, text, userService))
        {
            return;
        }
        else if (text.StartsWith("/"))
        {
            await HandleCommandAsync(message, userService);
        }
        else
        {
            await HandleFreeTextAsync(message);
        }
    }

    // Lets a user unfollow a trader, or set that trader's per-trader alert floor, by simply
    // replying "unfollow" / "setmin <amount>" (with or without a leading slash) to one of
    // that trader's notifications — no need to know or type the handle. Only intercepts
    // replies to a notification that actually has a Trader attached (Buy/Sell/Thesis/
    // Callout/Repost/Reply); Trending alerts have Trader=null (they're cross-platform, not
    // tied to one trader — see ConfluenceService), so a reply to one just falls through to
    // normal handling, same as a reply to anything else the bot ever sent.
    private async Task<bool> TryHandleNotificationReplyAsync(Message message, string text, IUserService userService)
    {
        if (_botClient == null) return false;

        var normalized = text.TrimStart('/').Trim();
        var spaceIdx = normalized.IndexOf(' ');
        var word = (spaceIdx < 0 ? normalized : normalized[..spaceIdx]).ToLowerInvariant();

        var isUnfollow = word == "unfollow";
        var isSetMin = word == "setmin";
        if (!isUnfollow && !isSetMin) return false;

        var chatId = message.Chat.Id;
        var repliedMessageId = message.ReplyToMessage!.MessageId;

        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var traderService = scope.ServiceProvider.GetRequiredService<ITraderService>();

        var sent = await dbContext.SentMessages
            .Include(s => s.Notification)
            .FirstOrDefaultAsync(s => s.ChatId == chatId && s.MessageId == repliedMessageId);

        var traderHandle = sent?.Notification.Trader;
        if (sent == null || string.IsNullOrEmpty(traderHandle))
            return false; // not a reply to a trader notification — let it fall through

        var trader = await traderService.GetTraderByHandleIgnoreCaseAsync(traderHandle, sent.Notification.Platform);
        if (trader == null)
        {
            await _botClient.SendTextMessageAsync(chatId, $"❌ Couldn't find {traderHandle} anymore — they may have been removed.");
            return true;
        }

        var user = await userService.GetUserByChatIdAsync(chatId);
        if (user == null)
        {
            await _botClient.SendTextMessageAsync(chatId, "❌ Please use /start first to register.");
            return true;
        }

        if (isUnfollow)
        {
            var unfollowed = await traderService.UnfollowTraderAsync(user.Id, trader.Id);
            await _botClient.SendTextMessageAsync(
                chatId,
                unfollowed ? $"✅ Unfollowed {trader.Handle}." : $"You weren't following {trader.Handle}."
            );
            return true;
        }

        // setmin
        var arg = spaceIdx < 0 ? "" : normalized[(spaceIdx + 1)..].Trim();
        if (arg.Length == 0 || !TryParseMarketCapArg(arg, out var minValue))
        {
            await _botClient.SendTextMessageAsync(
                chatId,
                "❌ Reply with setmin <amount>, e.g. \"setmin 50k\", \"setmin 1.2m\", or \"setmin off\" to clear it."
            );
            return true;
        }

        var ok = await traderService.SetThresholdAsync(user.Id, trader.Id, minValue);
        if (!ok)
        {
            await _botClient.SendTextMessageAsync(chatId, $"❌ You're not following {trader.Handle}, so there's no minimum to set. Follow them on /manage first.");
            return true;
        }

        var confirmText = minValue.HasValue
            ? $"✅ Minimum alert size for {trader.Handle} set to ${minValue.Value:N0}"
            : $"✅ Minimum alert size for {trader.Handle} cleared (alerts on everything)";
        await _botClient.SendTextMessageAsync(chatId, confirmText);
        return true;
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

    private static bool HasActiveSubscription(Models.User u) =>
        u.IsRN4L || (u.IsRegisteredNurse && u.RNExpiresAt > DateTime.UtcNow);

    // The ?t= token is redeemed for a session cookie by GET /manage.
    private async Task<InlineKeyboardMarkup> BuildManageButtonAsync(long chatId)
    {
        var sessions = _serviceProvider.GetRequiredService<WebSessionService>();
        var token = await sessions.CreateLoginLinkTokenAsync(chatId);
        return new InlineKeyboardMarkup(
            InlineKeyboardButton.WithUrl("Open Manage Page", $"https://groupchat-bot.tech/manage?t={Uri.EscapeDataString(token)}"));
    }

    // Settings and chains were managed with inline buttons in chat; old messages still carry
    // them. They all live on /manage now.
    private async Task HandleCallbackQueryAsync(CallbackQuery callbackQuery)
    {
        if (_botClient == null) return;
        await _botClient.AnswerCallbackQueryAsync(callbackQuery.Id, "Settings moved to the manage page: send /manage", showAlert: true);
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
                    message.From?.FirstName,
                    isNewBot: _isNewBotInstance
                );

                // A brand-new user starts on the longer-window categories (live: whoever is in
                // them now and later), not the whole roster. Re-sending /start leaves follows alone.
                var starter = await traderService.FollowStarterCategoriesAsync(newUser.Id);
                var followNotice = starter.Count > 0
                    ? $"You're following {string.Join(", ", starter.Select(id => TraderCategories.All.First(c => c.Id == id).Label))}: every trader in those categories, now and later. Change it anytime in /manage."
                    : "Your follows are unchanged. See or change them in /manage.";

                // One-shot free trial: only on the NEW bot, only if this chat has never had
                // one before (TrialExpiresAt still null), and only if they're not already a
                // paying subscriber (no point granting a trial on top of real access). Applies
                // equally to a brand-new signup and an old-bot user migrating over for the
                // first time — both look identical here (TrialExpiresAt null either way).
                var trialNotice = "";
                if (_isNewBotInstance
                    && newUser.TrialExpiresAt == null
                    && !HasActiveSubscription(newUser))
                {
                    var appConfig = scope.ServiceProvider.GetRequiredService<AppConfigService>();
                    var trialHours = await appConfig.GetNewBotTrialHoursAsync();
                    var trialExpiresAt = DateTime.UtcNow.AddHours(trialHours);
                    await userService.GrantTrialAsync(chatId, trialExpiresAt);
                    newUser.TrialExpiresAt = trialExpiresAt;

                    trialNotice = $@"
🎁 *Free trial active* — full, unobfuscated alerts (contract addresses, real trade links) for the next {trialHours} hours. After that, alerts go back to limited details unless you /subscribe.
";
                }

                await _botClient.SendTextMessageAsync(
                    chatId: chatId,
                    text: $@"🎉 Welcome to GROUPCHAT!
{trialNotice}
{followNotice}

Everything else (traders, categories, alert types, chains) is on the manage page: tap below, or send /manage anytime. /help for the rest.",
                    parseMode: ParseMode.Markdown,
                    replyMarkup: await BuildManageButtonAsync(chatId),
                    disableWebPagePreview: true
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
                    text: @"📚 GROUPCHAT

/manage - traders, categories, alert types, chains: all on one page

/top - top tokens (e.g. /top 1h, /top sol 1d)
/subscribe - full alerts and trader stats

Reply to any alert:
unfollow - stop alerts from that trader
setmin 50k - only alert on their trades above $50K (setmin off to clear)",
                    parseMode: ParseMode.Markdown
                );
                break;

            case "/manage":
                await _botClient.SendTextMessageAsync(
                    chatId: chatId,
                    text: "Manage your followed traders and per-trader alert thresholds here:",
                    replyMarkup: await BuildManageButtonAsync(chatId),
                    disableWebPagePreview: true
                );
                break;

            // Moved to the manage page. Typed /unfollow lands here too; replying "unfollow" to an
            // alert is handled earlier (TryHandleNotificationReplyAsync) and still works.
            case "/list":
            case "/mytraders":
            case "/follow":
            case "/unfollow":
            case "/autofollow":
            case "/settings":
            case "/repeatwindow":
            case "/chains":
                await _botClient.SendTextMessageAsync(
                    chatId: chatId,
                    text: "That's on the manage page now: follow traders and categories, alert types, chains and repeat alerts, all in one place.\n\nTip: reply \"unfollow\" or \"setmin 50k\" to any alert to act on that trader.",
                    replyMarkup: await BuildManageButtonAsync(chatId),
                    disableWebPagePreview: true
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
                            text: $"💎 Your payment address is still active — *{expiryDisplay}* left:\n\n`{existing.WalletPublicKey}`\n\nSend *{priceDisplay} SOL* to unlock everything: full contract addresses, live trade links, every call the instant it happens\\. Activates automatically within seconds — refundable within 7 days, zero risk\\.",
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
                        text: $"💎 *Your unique payment address* — generated just for you, live for the next hour:\n\n`{keypair.PublicKey}`\n\nSend *{priceDisplay} SOL* to unlock everything: full contract addresses, live trade links, every call the instant it happens\\. Activates automatically within seconds — refundable within 7 days, zero risk\\.\n\n⏳ Expires in 1h — don't sleep on it\\.",
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

// A distinct concrete type is required here, not just a second factory-based
// registration of TelegramBotPollingService itself — the generic host collapses two
// IHostedService registrations that resolve to the same concrete implementation type
// down to one, silently dropping the second. Subclassing sidesteps that entirely.
public class DeprecatedTelegramBotPollingService : TelegramBotPollingService
{
    public DeprecatedTelegramBotPollingService(
        IOptions<TelegramSettings> settings,
        IServiceProvider serviceProvider,
        ILogger<TelegramBotPollingService> logger,
        IHubContext<DashboardHub> hubContext)
        : base(settings, serviceProvider, logger, hubContext, settings.Value.DeprecatedBotToken, isNewBotInstance: false)
    {
    }
}
