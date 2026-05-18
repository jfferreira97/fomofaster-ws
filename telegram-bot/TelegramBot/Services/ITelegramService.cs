using TelegramBot.Models;

namespace TelegramBot.Services;

public interface ITelegramService
{
    Task SendNotificationToAllUsersAsync(NotificationRequest notification, string? contractAddress = null, Chain? chain = null, string? traderHandle = null, string? ticker = null, double? marketCap = null, NotificationType notificationType = NotificationType.Unknown, string? fomoWsTradeId = null);
    Task SendTestMessageAsync(long chatId, string message);
    Task<object> GetUpdatesAsync();
    bool IsConfigured();
    Task<bool> SendPlainMessageAsync(long chatId, string message);
}
