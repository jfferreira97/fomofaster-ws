namespace TelegramBot.Models;

public class ContractAddressRetryItem
{
    public int NotificationId { get; set; }
    public string? Ticker { get; set; }
    public string? Trader { get; set; }
    public double? MarketCap { get; set; }
    public DateTime EnqueuedAt { get; set; }
    public int RetryCount { get; set; }
    public DateTime NextRetryAt { get; set; }
}
