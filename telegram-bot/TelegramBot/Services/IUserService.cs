using TelegramBot.Models;

namespace TelegramBot.Services;

public interface IUserService
{
    Task<User?> GetUserByChatIdAsync(long chatId);
    Task<List<User>> GetAllActiveUsersAsync();
    Task<List<User>> GetAllUsersAsync();
    Task<User> AddOrUpdateUserAsync(long chatId, string? username, string? firstName);
    Task DeactivateUserAsync(long chatId);
    Task GrantRegisteredNurseAsync(long chatId, DateTime expiresAt);
    Task RevokeExpiredSubscriptionsAsync();
}
