namespace TelegramBot.Models;

public class Notification
{
    public int Id { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? Ticker { get; set; }
    public string? Trader { get; set; }
    public bool HasCA { get; set; }
    public string? ContractAddress { get; set; }
    public Chain? Chain { get; set; }
    public DateTime SentAt { get; set; }
    public decimal? MarketCapAtNotification { get; set; }
    public NotificationType Type { get; set; } = NotificationType.Unknown;
    public string? TradeId { get; set; }
}

public enum NotificationType
{
    Buy,
    Sell,
    Deposit,
    Thesis,
    Verified,
    Unknown
}
