using TelegramBot.Models;

namespace TelegramBot.Services;

public interface ITraderService
{
    Task<Trader?> GetTraderByHandleIgnoreCaseAsync(string handle);
    Task<Trader?> GetTraderByIdAsync(int traderId);
    Task<List<Trader>> GetAllTradersAsync();
    Task<List<Trader>> GetPublicTradersAsync();
    Task<List<Trader>> GetTradersByUserIdAsync(int userId);
    Task<List<Trader>> GetPublicTradersByUserIdAsync(int userId);
    Task<Trader> AddOrUpdateTraderAsync(string handle, bool isHidden = false);
    Task<bool> SetTraderHiddenAsync(int traderId, bool isHidden);
    Task<bool> FollowTraderAsync(int userId, int traderId);
    Task<bool> FollowTraderByHandleAsync(int userId, string handle);
    Task<bool> UnfollowTraderAsync(int userId, int traderId);
    Task<bool> UnfollowTraderByHandleAsync(int userId, string handle);
    Task<bool> IsFollowingAsync(int userId, int traderId);
    Task<List<int>> GetFollowerUserIdsForTraderAsync(int traderId);
    Task<List<int>> GetFollowerUserIdsForTraderHandleAsync(string handle);
    Task<int> FollowAllTradersAsync(int userId);
    Task<int> FollowAllPublicTradersAsync(int userId);
    Task<int> FollowAllHiddenTradersAsync(int userId);
    Task<int> UnfollowAllTradersAsync(int userId);
    Task<bool> DeleteTraderAsync(int traderId);
    Task<bool> DeleteTraderByHandleAsync(string handle);
}
