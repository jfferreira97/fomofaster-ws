namespace TelegramBot.Models;

public class CachedTokenAddress
{
    public int Id { get; set; }
    public string Ticker { get; set; } = string.Empty;
    public string ContractAddress { get; set; } = string.Empty;
    public Chain? Chain { get; set; }
    public DateTime LastAccessed { get; set; }
    public DateTime ExpiresAt { get; set; }
}
