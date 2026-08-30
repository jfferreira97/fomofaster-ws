using TelegramBot.Models;

namespace TelegramBot.Services;

public interface ITraderService
{
    Task<Trader?> GetTraderByHandleIgnoreCaseAsync(string handle, Platform platform = Platform.Fomo);
    Task<Trader?> GetTraderByIdAsync(int traderId);
    Task<List<Trader>> GetAllTradersAsync();
    Task<List<Trader>> GetTradersByUserIdAsync(int userId);
    Task<Trader> AddOrUpdateTraderAsync(string handle, Platform platform = Platform.Fomo);
    Task<int> BulkRegisterTradersAsync(IEnumerable<string> handles, Platform platform);
    Task<bool> FollowTraderAsync(int userId, int traderId);
    Task<bool> FollowTraderByHandleAsync(int userId, string handle, Platform platform = Platform.Fomo);
    Task<bool> UnfollowTraderAsync(int userId, int traderId);
    Task<bool> UnfollowTraderByHandleAsync(int userId, string handle, Platform platform = Platform.Fomo);
    Task<bool> IsFollowingAsync(int userId, int traderId);
    Task<List<int>> GetFollowerUserIdsForTraderAsync(int traderId);
    Task<List<int>> GetFollowerUserIdsForTraderHandleAsync(string handle, Platform platform = Platform.Fomo);
    Task<int> FollowAllTradersAsync(int userId);
    Task<int> UnfollowAllTradersAsync(int userId);
    Task<bool> DeleteTraderAsync(int traderId);
    Task<bool> DeleteTraderByHandleAsync(string handle, Platform platform = Platform.Fomo);
}
