using TelegramBot.Models;

namespace TelegramBot.Services;

public interface IChainSettingsService
{
    // Reads come straight from ChainSettingsCache (in-memory, no DB) — cheap enough to call
    // from a command handler on every /chains invocation.
    Dictionary<Chain, UserChainSetting> GetSettingsForUser(int userId);

    Task<bool> SetDisabledAsync(int userId, Chain chain, bool disabled);
    Task<bool> SetMinMarketCapAsync(int userId, Chain chain, decimal? minMarketCap);
    Task<bool> SetTrendingDisabledAsync(int userId, Chain chain, bool disabled);
}
