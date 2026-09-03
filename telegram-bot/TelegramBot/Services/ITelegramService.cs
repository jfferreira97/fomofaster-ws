using TelegramBot.Models;

namespace TelegramBot.Services;

public interface ITelegramService
{
    Task SendNotificationToAllUsersAsync(NotificationRequest notification, string? contractAddress = null, Chain? chain = null, string? traderHandle = null, string? ticker = null, double? marketCap = null, NotificationType notificationType = NotificationType.Unknown, string? fomoWsTradeId = null, Platform platform = Platform.Fomo, double? usdAmount = null);
    Task SendTestMessageAsync(long chatId, string message);
    Task<object> GetUpdatesAsync();
    bool IsConfigured();
    Task<bool> SendPlainMessageAsync(long chatId, string message);

    // Bot's @username (no leading @), needed client-side for the Telegram Login Widget's
    // data-telegram-login attribute. Cached after the first lookup.
    Task<string?> GetBotUsernameAsync();
}
