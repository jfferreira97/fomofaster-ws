using Microsoft.EntityFrameworkCore;
using TelegramBot.Data;
using TelegramBot.Models;

namespace TelegramBot.Services;

public class UserService : IUserService
{
    private readonly AppDbContext _dbContext;
    private readonly ILogger<UserService> _logger;

    public UserService(AppDbContext dbContext, ILogger<UserService> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<User?> GetUserByChatIdAsync(long chatId)
    {
        return await _dbContext.Users.FirstOrDefaultAsync(u => u.ChatId == chatId);
    }

    public async Task<List<User>> GetAllActiveUsersAsync()
    {
        return await _dbContext.Users.Where(u => u.IsActive).ToListAsync();
    }

    public async Task<List<User>> GetAllUsersAsync()
    {
        return await _dbContext.Users.ToListAsync();
    }

    public async Task<User> AddOrUpdateUserAsync(long chatId, string? username, string? firstName)
    {
        var user = await GetUserByChatIdAsync(chatId);

        if (user == null)
        {
            user = new User
            {
                ChatId = chatId,
                Username = username,
                FirstName = firstName,
                JoinedAt = DateTime.UtcNow,
                IsActive = true,
                AutoFollowNewTraders = true // Default to auto-following new traders on user creation
            };

            _dbContext.Users.Add(user);
            _logger.LogInformation("New user added: ChatId={ChatId}, Username={Username}", chatId, username);
        }
        else
        {
            user.Username = username;
            user.FirstName = firstName;
            user.IsActive = true;
            _logger.LogInformation("User updated: ChatId={ChatId}, Username={Username}", chatId, username);
        }

        await _dbContext.SaveChangesAsync();
        return user;
    }

    public async Task DeactivateUserAsync(long chatId)
    {
        var user = await GetUserByChatIdAsync(chatId);
        if (user != null)
        {
            user.IsActive = false;
            await _dbContext.SaveChangesAsync();
            _logger.LogInformation("User deactivated: ChatId={ChatId}", chatId);
        }
    }

    public async Task GrantRegisteredNurseAsync(long chatId, DateTime expiresAt)
    {
        var user = await GetUserByChatIdAsync(chatId);
        if (user != null)
        {
            user.IsRegisteredNurse = true;
            user.RNExpiresAt = expiresAt;
            await _dbContext.SaveChangesAsync();
            _logger.LogInformation("RN access granted: ChatId={ChatId}, ExpiresAt={ExpiresAt}", chatId, expiresAt);
        }
    }

    public async Task RevokeExpiredSubscriptionsAsync()
    {
        var expired = await _dbContext.Users
            .Where(u => u.IsRegisteredNurse && !u.IsRN4L && u.RNExpiresAt < DateTime.UtcNow)
            .ToListAsync();

        foreach (var user in expired)
        {
            user.IsRegisteredNurse = false;
            user.RNExpiresAt = null;
        }

        if (expired.Count > 0)
        {
            await _dbContext.SaveChangesAsync();
            _logger.LogInformation("Revoked {Count} expired RN subscriptions", expired.Count);
        }
    }
}
