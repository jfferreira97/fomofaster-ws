using TelegramBot.Models;

namespace TelegramBot.Services;

public interface ITraderService
{
    Task<Trader?> GetTraderByHandleIgnoreCaseAsync(string handle, Platform platform = Platform.Fomo);
    Task<Trader?> GetTraderByIdAsync(int traderId);
    Task<List<Trader>> GetAllTradersAsync();
    Task<List<Trader>> GetTradersByUserIdAsync(int userId);
    Task<Trader> AddOrUpdateTraderAsync(string handle, Platform platform = Platform.Fomo);
    Task<BulkRegisterResult> BulkRegisterTradersAsync(IEnumerable<TraderSeedEntry> traders, Platform platform);
    Task<bool> FollowTraderAsync(int userId, int traderId);
    Task<bool> FollowTraderByHandleAsync(int userId, string handle, Platform platform = Platform.Fomo);

    // Follow, but only if the user hasn't explicitly unfollowed this exact trader before.
    // Use for every automatic/bulk follow path (new-trader autofollow, follow-all) — never
    // for a single-trader action the user directly clicked, which should always go through
    // FollowTraderAsync so an explicit re-follow can clear the exclusion.
    Task<bool> TryAutoFollowTraderAsync(int userId, int traderId);

    Task<bool> UnfollowTraderAsync(int userId, int traderId);
    Task<bool> UnfollowTraderByHandleAsync(int userId, string handle, Platform platform = Platform.Fomo);
    Task<bool> IsFollowingAsync(int userId, int traderId);
    Task<List<int>> GetFollowerUserIdsForTraderAsync(int traderId);
    Task<List<int>> GetFollowerUserIdsForTraderHandleAsync(string handle, Platform platform = Platform.Fomo);

    // UserId -> that user's per-trader MinValueUsd (null = no minimum) for every follower
    // of the given trader. Superset of GetFollowerUserIdsForTraderHandleAsync's keys.
    Task<Dictionary<int, decimal?>> GetFollowerThresholdsForTraderHandleAsync(string handle, Platform platform = Platform.Fomo);

    Task<int> FollowAllTradersAsync(int userId);
    Task<int> UnfollowAllTradersAsync(int userId);
    Task<bool> DeleteTraderAsync(int traderId);
    Task<bool> DeleteTraderByHandleAsync(string handle, Platform platform = Platform.Fomo);

    // Set or clear (null) this user's alert floor for one trader they follow. Returns false
    // if the user doesn't currently follow that trader.
    Task<bool> SetThresholdAsync(int userId, int traderId, decimal? minValueUsd);

    // Full (optionally platform-filtered) trader roster annotated with this user's follow
    // state, for the trader-management page.
    Task<List<TraderBrowseEntry>> GetBrowseListAsync(int userId, Platform? platform);
}
