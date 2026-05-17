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
    private const long RequiredLamports = 200_000_000; // 0.2 SOL

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
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in PaymentPollerService loop");
            }

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    private async Task PollPendingPaymentsAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var pending = await dbContext.PendingPayments
            .Where(p => !p.IsConfirmed && p.ExpiresAt > DateTime.UtcNow)
            .ToListAsync();

        // Refresh in-memory cache so TelegramService can read it without DB hits
        PendingWalletCache.Clear();
        foreach (var p in pending)
            PendingWalletCache[p.ChatId] = p.WalletPublicKey;

        if (pending.Count == 0) return;

        foreach (var payment in pending)
        {
            try
            {
                var balance = await GetSolanaBalanceAsync(payment.WalletPublicKey);
                if (balance >= RequiredLamports)
                {
                    payment.IsConfirmed = true;
                    payment.ConfirmedAt = DateTime.UtcNow;
                    await dbContext.SaveChangesAsync();

                    var userService = scope.ServiceProvider.GetRequiredService<IUserService>();
                    var expiresAt = DateTime.UtcNow.AddDays(30);
                    await userService.GrantRegisteredNurseAsync(payment.ChatId, expiresAt);

                    await _telegramService.SendPlainMessageAsync(
                        payment.ChatId,
                        $"✅ Payment confirmed! You now have full access for 30 days (until {expiresAt:yyyy-MM-dd}). Enjoy."
                    );

                    _logger.LogInformation("RN access granted for ChatId={ChatId}, wallet={Wallet}", payment.ChatId, payment.WalletPublicKey);
                }
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
        var userService = scope.ServiceProvider.GetRequiredService<IUserService>();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var expired = await dbContext.Users
            .Where(u => u.IsRegisteredNurse && !u.IsRN4L && u.RNExpiresAt < DateTime.UtcNow)
            .ToListAsync();

        foreach (var user in expired)
        {
            user.IsRegisteredNurse = false;
            user.RNExpiresAt = null;

            await _telegramService.SendPlainMessageAsync(
                user.ChatId,
                "Your FomoFaster subscription has expired. Use /subscribe to renew."
            );
        }

        if (expired.Count > 0)
        {
            await dbContext.SaveChangesAsync();
            _logger.LogInformation("Revoked {Count} expired RN subscriptions", expired.Count);
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
        var response = await _httpClient.PostAsync(SolanaRpcUrl, content);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("result").GetProperty("value").GetInt64();
    }
}
