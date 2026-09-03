using Microsoft.EntityFrameworkCore;
using TelegramBot.Data;
using TelegramBot.Models;

namespace TelegramBot.Services;

public class ChainSettingsService : IChainSettingsService
{
    private readonly AppDbContext _dbContext;
    private readonly ChainSettingsCache _cache;
    private readonly ILogger<ChainSettingsService> _logger;

    public ChainSettingsService(AppDbContext dbContext, ChainSettingsCache cache, ILogger<ChainSettingsService> logger)
    {
        _dbContext = dbContext;
        _cache = cache;
        _logger = logger;
    }

    public Dictionary<Chain, UserChainSetting> GetSettingsForUser(int userId)
    {
        var result = new Dictionary<Chain, UserChainSetting>();
        foreach (var chain in Enum.GetValues<Chain>())
        {
            if (_cache.GetForChain(chain).TryGetValue(userId, out var setting))
                result[chain] = setting;
        }
        return result;
    }

    public async Task<bool> SetDisabledAsync(int userId, Chain chain, bool disabled)
    {
        var setting = await GetOrCreateAsync(userId, chain);
        setting.IsDisabled = disabled;
        await _dbContext.SaveChangesAsync();
        _cache.Upsert(setting);

        _logger.LogInformation("User {UserId} set chain {Chain} IsDisabled={Disabled}", userId, chain, disabled);
        return true;
    }

    public async Task<bool> SetMinMarketCapAsync(int userId, Chain chain, decimal? minMarketCap)
    {
        var setting = await GetOrCreateAsync(userId, chain);
        setting.MinMarketCap = minMarketCap;
        await _dbContext.SaveChangesAsync();
        _cache.Upsert(setting);

        _logger.LogInformation("User {UserId} set chain {Chain} MinMarketCap={MinMarketCap}", userId, chain, minMarketCap);
        return true;
    }

    public async Task<bool> SetTrendingDisabledAsync(int userId, Chain chain, bool disabled)
    {
        var setting = await GetOrCreateAsync(userId, chain);
        setting.TrendingDisabled = disabled;
        await _dbContext.SaveChangesAsync();
        _cache.Upsert(setting);

        _logger.LogInformation("User {UserId} set chain {Chain} TrendingDisabled={Disabled}", userId, chain, disabled);
        return true;
    }

    private async Task<UserChainSetting> GetOrCreateAsync(int userId, Chain chain)
    {
        var existing = await _dbContext.UserChainSettings
            .FirstOrDefaultAsync(s => s.UserId == userId && s.Chain == chain);
        if (existing != null)
            return existing;

        var created = new UserChainSetting { UserId = userId, Chain = chain };
        _dbContext.UserChainSettings.Add(created);
        return created;
    }
}
