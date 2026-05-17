using Microsoft.EntityFrameworkCore;
using TelegramBot.Data;
using TelegramBot.Models;

namespace TelegramBot.Services;

public class AppConfigService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<AppConfigService> _logger;

    private static readonly Dictionary<string, string> Defaults = new()
    {
        ["TreasuryWalletAddress"]  = "",
        ["SubscriptionPriceSol"]   = "0.3",
    };

    public AppConfigService(IServiceProvider serviceProvider, ILogger<AppConfigService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task<string?> GetAsync(string key)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var config = await db.AppConfigs.FirstOrDefaultAsync(c => c.Key == key);
        return config?.Value;
    }

    public async Task SetAsync(string key, string value, string? description = null)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var config = await db.AppConfigs.FirstOrDefaultAsync(c => c.Key == key);

        if (config == null)
        {
            config = new AppConfig { Key = key, Description = description };
            db.AppConfigs.Add(config);
        }

        config.Value = value;
        config.UpdatedAt = DateTime.UtcNow;
        if (description != null) config.Description = description;

        await db.SaveChangesAsync();
        _logger.LogInformation("Config updated: {Key}", key);
    }

    public async Task<List<AppConfig>> GetAllAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.AppConfigs.OrderBy(c => c.Key).ToListAsync();
    }

    public async Task SeedDefaultsAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        foreach (var (key, defaultValue) in Defaults)
        {
            if (!await db.AppConfigs.AnyAsync(c => c.Key == key))
            {
                db.AppConfigs.Add(new AppConfig
                {
                    Key = key,
                    Value = defaultValue,
                    Description = key switch
                    {
                        "TreasuryWalletAddress" => "Solana wallet address to sweep payments into",
                        "SubscriptionPriceSol"  => "SOL required for a 30-day subscription",
                        _ => null
                    },
                    UpdatedAt = DateTime.UtcNow
                });
            }
        }

        await db.SaveChangesAsync();
    }

    public async Task<string?> GetTreasuryWalletAsync() => await GetAsync("TreasuryWalletAddress");

    public async Task<decimal> GetSubscriptionPriceSolAsync()
    {
        var raw = await GetAsync("SubscriptionPriceSol");
        return decimal.TryParse(raw, out var val) ? val : 0.3m;
    }
}
