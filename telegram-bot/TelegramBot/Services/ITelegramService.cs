using TelegramBot.Models;

namespace TelegramBot.Services;

public interface ITelegramService
{
    Task SendNotificationToAllUsersAsync(NotificationRequest notification, ContractLookupResult? lookupResult = null, string? traderHandle = null, string? ticker = null, double? marketCap = null, NotificationType notificationType = NotificationType.Unknown, string? tradeId = null);
    Task SendTestMessageAsync(long chatId, string message);
    Task<object> GetUpdatesAsync();
    bool IsConfigured();
    Task EditNotificationMessagesAsync(int notificationId, string contractAddress, Chain chain);
    Task<bool> SendPlainMessageAsync(long chatId, string message);
}
