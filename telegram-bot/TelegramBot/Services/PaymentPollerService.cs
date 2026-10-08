using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;
using System.Text.Json;
using TelegramBot.Data;

namespace TelegramBot.Services;

public class PaymentPollerService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ITelegramService _telegramService;
    private readonly ILogger<PaymentPollerService> _logger;
    private readonly HttpClient _httpClient;

    private const string SolanaRpcUrl = "https://api.mainnet-beta.solana.com";
    private const long LamportsPerSol = 1_000_000_000;

    // chatId → wallet public key for active pending payments (refreshed each poll cycle)
    public ConcurrentDictionary<long, string> PendingWalletCache { get; } = new();

    public PaymentPollerService(
        IServiceProvider serviceProvider,
        ITelegramService telegramService,
        ILogger<PaymentPollerService> logger,
        IHttpClientFactory httpClientFactory)
    {
        _serviceProvider = serviceProvider;
        _telegramService = telegramService;
        _logger = logger;
        _httpClient = httpClientFactory.CreateClient();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("PaymentPollerService started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollPendingPaymentsAsync();
                await RevokeExpiredSubscriptionsAsync();
                await NotifyExpiredTrialsAsync();
                await SendRemindersAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in PaymentPollerService loop");
            }

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    private static string Money(decimal v) => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
    private static string UtcText(DateTime d) => d.ToString("yyyy-MM-dd HH:mm") + " UTC";
    private static string Remaining(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" : $"{Math.Max(1, (int)t.TotalMinutes)}m";

    // wallet id -> when its balance was last read. Wallets still inside their 1 h window are read every cycle;
    // older ones (watched for 24 h so a late transfer still counts) only once a minute, to spare the public RPC.
    private readonly Dictionary<long, DateTime> _lastChecked = new();

    private async Task PollPendingPaymentsAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var appConfig = scope.ServiceProvider.GetRequiredService<AppConfigService>();

        var priceSol = await appConfig.GetSubscriptionPriceSolAsync();
        TelegramService.SubscribeFooter = $"Full ticker, size and contract: /subscribe ({Money(priceSol)} SOL / 30 days)";

        var now = DateTime.UtcNow;
        var pending = await dbContext.PendingPayments
            .Where(p => !p.IsConfirmed && p.ExpiresAt > now.AddHours(-24))
            .ToListAsync();

        // Refresh in-memory cache so TelegramService can read it without DB hits
        PendingWalletCache.Clear();
        foreach (var p in pending.Where(p => p.ExpiresAt > now))
            PendingWalletCache[p.ChatId] = p.WalletPublicKey;

        if (pending.Count == 0) { _lastChecked.Clear(); return; }

        foreach (var payment in pending)
        {
            try
            {
                var inWindow = payment.ExpiresAt > now;
                if (!inWindow && _lastChecked.TryGetValue(payment.Id, out var last) && now - last < TimeSpan.FromSeconds(60)) continue;
                _lastChecked[payment.Id] = now;

                // The price the user was shown when the wallet was created, not whatever the config says today.
                var required = (long)((payment.AmountSol > 0 ? payment.AmountSol : priceSol) * LamportsPerSol);
                var balance = await GetSolanaBalanceAsync(payment.WalletPublicKey);
                if (balance < required) continue;

                var user = await dbContext.Users.FirstOrDefaultAsync(u => u.ChatId == payment.ChatId);
                if (user == null)
                {
                    // Nobody to grant access to: leave the payment unconfirmed so it is not silently marked as done.
                    _logger.LogError("Funded wallet {Wallet} has no matching user (ChatId={ChatId}); left unconfirmed", payment.WalletPublicKey, payment.ChatId);
                    continue;
                }

                var grantAt = DateTime.UtcNow;
                // Stack on top of any time still left, so paying early never loses days.
                var start = user.RNExpiresAt is DateTime current && current > grantAt ? current : grantAt;
                var expiresAt = start.AddDays(30);

                // Claim the payment row and grant access in ONE transaction. The claim only succeeds for a row that is
                // still unconfirmed, so a second poller (restart / deploy overlap) cannot grant the same payment twice,
                // and a crash cannot leave a confirmed payment without access.
                await using var tx = await dbContext.Database.BeginTransactionAsync();
                var claimed = await dbContext.PendingPayments
                    .Where(p => p.Id == payment.Id && !p.IsConfirmed)
                    .ExecuteUpdateAsync(s => s.SetProperty(p => p.IsConfirmed, true).SetProperty(p => p.ConfirmedAt, (DateTime?)grantAt));
                if (claimed == 0) { await tx.RollbackAsync(); continue; }

                user.IsRegisteredNurse = true;
                user.RNExpiresAt = expiresAt;
                user.TrialExpiryNotified = true;   // someone who paid must never get the "trial has ended" notice later
                await dbContext.SaveChangesAsync();
                await tx.CommitAsync();
                _lastChecked.Remove(payment.Id);

                try { await scope.ServiceProvider.GetRequiredService<ActiveUserCache>().RefreshNowAsync(); }
                catch (Exception ex) { _logger.LogWarning(ex, "Active user cache refresh after payment failed"); }

                await _telegramService.SendPlainMessageAsync(
                    payment.ChatId,
                    $"✅ Payment received, thank you. Full access is on until {UtcText(expiresAt)} (30 days).\n\nFrom your next alert you'll see the full ticker, buy size and contract. It doesn't renew by itself; I'll remind you 3 days before it ends. Adjust who you follow: /manage"
                );

                _logger.LogInformation("RN access granted for ChatId={ChatId}, wallet={Wallet}, until {Until}", payment.ChatId, payment.WalletPublicKey, expiresAt);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to check balance for wallet {Wallet}", payment.WalletPublicKey);
            }
        }
    }

    private async Task RevokeExpiredSubscriptionsAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var appConfig = scope.ServiceProvider.GetRequiredService<AppConfigService>();

        var expired = await dbContext.Users
            .Where(u => u.IsRegisteredNurse && !u.IsRN4L && u.RNExpiresAt < DateTime.UtcNow)
            .ToListAsync();
        if (expired.Count == 0) return;

        var chats = expired.Select(u => u.ChatId).ToList();
        foreach (var user in expired)
        {
            user.IsRegisteredNurse = false;
            user.RNExpiresAt = null;
            user.TrialExpiryNotified = true;   // the trial notice is irrelevant to someone who already had a subscription
        }

        // Save first, notify after: if the save fails nothing is sent, so the notice can never repeat every tick.
        await dbContext.SaveChangesAsync();
        try { await scope.ServiceProvider.GetRequiredService<ActiveUserCache>().RefreshNowAsync(); } catch { /* next scheduled refresh will catch up */ }

        var price = Money(await appConfig.GetSubscriptionPriceSolAsync());
        foreach (var chatId in chats)
        {
            await _telegramService.SendPlainMessageAsync(
                chatId,
                $"Your GROUPCHAT access has ended. Alerts continue, with the ticker, buy size, contract and trade links hidden.\n\n{price} SOL gets you 30 more days: /subscribe"
            );
        }
        _logger.LogInformation("Revoked {Count} expired RN subscriptions", expired.Count);
    }

    // Runs on the same 5-second tick as the rest of this loop — no separate timer, no new
    // BackgroundService. The query is a narrow indexed filter (TrialExpiresAt has a partial
    // index) that only ever matches trials that expired since the last tick and haven't been
    // notified yet, so cost stays O(recently-expired), never O(all users).
    private async Task NotifyExpiredTrialsAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var now = DateTime.UtcNow;
        var justExpired = await dbContext.Users
            .Where(u => u.TrialExpiresAt != null && u.TrialExpiresAt < now && !u.TrialExpiryNotified
                     && !u.IsRN4L && !(u.IsRegisteredNurse && u.RNExpiresAt > now))
            .ToListAsync();
        if (justExpired.Count == 0) return;

        var chats = justExpired.Select(u => u.ChatId).ToList();
        foreach (var user in justExpired) user.TrialExpiryNotified = true;
        await dbContext.SaveChangesAsync();   // save first, notify after

        var appConfig = scope.ServiceProvider.GetRequiredService<AppConfigService>();
        var price = Money(await appConfig.GetSubscriptionPriceSolAsync());
        foreach (var chatId in chats)
        {
            await _telegramService.SendPlainMessageAsync(
                chatId,
                $"Your free trial has ended.\n\nAlerts still arrive, but the ticker, buy size, contract and trade links are now hidden.\n\nFull access, plus the playbook on every trader, is {price} SOL for 30 days, no auto-renew. /subscribe gives you a payment address."
            );
        }
        _logger.LogInformation("Notified {Count} user(s) their trial expired", justExpired.Count);
    }

    // One-time reminders, tracked in a small table created here (no EF migration): the last quarter of a free trial,
    // and 3 days / 1 day before a paid subscription ends. INSERT OR IGNORE claims a reminder before it is sent,
    // so a restart can never send the same one twice.
    private bool _reminderTableReady;

    private static async Task<bool> ClaimReminderAsync(AppDbContext db, long chatId, string kind, string key)
    {
        var at = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
        var n = await db.Database.ExecuteSqlInterpolatedAsync(
            $@"INSERT OR IGNORE INTO ""BotReminders"" (""ChatId"",""Kind"",""Key"",""At"") VALUES ({chatId},{kind},{key},{at})");
        return n == 1;
    }

    private async Task SendRemindersAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var appConfig = scope.ServiceProvider.GetRequiredService<AppConfigService>();

        if (!_reminderTableReady)
        {
            await db.Database.ExecuteSqlRawAsync(@"CREATE TABLE IF NOT EXISTS ""BotReminders"" (
                ""ChatId"" INTEGER NOT NULL, ""Kind"" TEXT NOT NULL, ""Key"" TEXT NOT NULL, ""At"" TEXT NOT NULL,
                PRIMARY KEY (""ChatId"", ""Kind"", ""Key""))");
            _reminderTableReady = true;
        }

        var now = DateTime.UtcNow;
        var budget = 20;   // at most this many reminders per 5-second tick

        var trialHours = await appConfig.GetNewBotTrialHoursAsync();
        var window = TimeSpan.FromHours(Math.Max(1, trialHours) * 0.25);
        var trialUsers = await db.Users.AsNoTracking()
            .Where(u => u.IsActive && u.TrialExpiresAt != null && u.TrialExpiresAt > now && u.TrialExpiresAt < now + window
                     && !u.IsRN4L && !(u.IsRegisteredNurse && u.RNExpiresAt > now))
            .Select(u => new { u.ChatId, u.TrialExpiresAt })
            .ToListAsync();

        var renewing = await db.Users.AsNoTracking()
            .Where(u => u.IsActive && u.IsRegisteredNurse && !u.IsRN4L && u.RNExpiresAt != null && u.RNExpiresAt > now && u.RNExpiresAt < now.AddDays(3))
            .Select(u => new { u.ChatId, u.RNExpiresAt })
            .ToListAsync();

        var priceSol = await appConfig.GetSubscriptionPriceSolAsync();
        var price = Money(priceSol);

        foreach (var u in trialUsers)
        {
            if (budget <= 0) return;
            var ends = u.TrialExpiresAt!.Value;
            if (!await ClaimReminderAsync(db, u.ChatId, "trial", ends.ToString("yyyyMMddHHmm"))) continue;
            budget--;
            await _telegramService.SendPlainMessageAsync(u.ChatId,
                $"Your free trial ends soon.\n\nAfter that, alerts keep coming but the ticker, buy size, contract and trade links are hidden.\n\nTo keep the full version and unlock the playbook on every trader: {price} SOL for 30 days, no auto-renew. /subscribe");
        }

        foreach (var u in renewing)
        {
            if (budget <= 0) return;
            var ends = u.RNExpiresAt!.Value;
            var oneDay = ends - now <= TimeSpan.FromDays(1);
            if (!await ClaimReminderAsync(db, u.ChatId, oneDay ? "renew1" : "renew3", ends.ToString("yyyyMMdd"))) continue;
            budget--;
            await _telegramService.SendPlainMessageAsync(u.ChatId, oneDay
                ? $"Your full access ends tomorrow ({UtcText(ends)}).\n\nRenewing adds 30 days on top of what you have left: {price} SOL. /subscribe"
                : $"Your full access ends in about 3 days ({UtcText(ends)}).\n\nRenewing adds 30 days on top of what you have left, so nothing is lost: {price} SOL. /subscribe");
        }

        // Abandoned checkout: someone opened /subscribe, got an address and has not paid. One plain nudge 2 to 20 hours
        // later (the address is still watched), and never more than one per person every 3 days.
        var cutNew = now.AddHours(-2);
        var cutOld = now.AddHours(-20);
        var open = await db.PendingPayments.AsNoTracking()
            .Where(p => !p.IsConfirmed && p.CreatedAt <= cutNew && p.CreatedAt > cutOld)
            .ToListAsync();
        foreach (var p in open.GroupBy(x => x.ChatId).Select(g => g.OrderByDescending(x => x.Id).First()))
        {
            if (budget <= 0) return;
            var buyer = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.ChatId == p.ChatId);
            if (buyer == null || !buyer.IsActive || buyer.IsRN4L) continue;
            if (buyer.IsRegisteredNurse && buyer.RNExpiresAt > now) continue;
            var since = now.AddDays(-3).ToString("yyyy-MM-dd HH:mm:ss");
            var recent = await db.Database.SqlQuery<int>(
                $@"SELECT COUNT(*) AS ""Value"" FROM ""BotReminders"" WHERE ""ChatId"" = {p.ChatId} AND ""Kind"" = 'checkout' AND ""At"" > {since}").FirstAsync();
            if (recent > 0) continue;
            if (!await ClaimReminderAsync(db, p.ChatId, "checkout", p.Id.ToString())) continue;
            budget--;
            await _telegramService.SendPlainMessageAsync(p.ChatId,
                $"You opened /subscribe but haven't paid yet. Send {Money(p.AmountSol > 0 ? p.AmountSol : priceSol)} SOL to this address and full access starts right away, with the ticker, size, contract and the playbook on every trader:\n\n{p.WalletPublicKey}\n\nThe address stays open a little longer. Questions? Just reply here.");
        }
    }

    private async Task<long> GetSolanaBalanceAsync(string publicKey)
    {
        var payload = new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "getBalance",
            @params = new[] { publicKey }
        };

        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        using var rpcTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var response = await _httpClient.PostAsync(SolanaRpcUrl, content, rpcTimeout.Token);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(rpcTimeout.Token);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("result").GetProperty("value").GetInt64();
    }
}
