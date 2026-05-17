namespace TelegramBot.Models;

public class StructuredNotificationRequest
{
    public required string TradeId { get; set; }
    public required string Trader { get; set; }
    public required string Ticker { get; set; }
    public required string ContractAddress { get; set; }
    public required int NetworkId { get; set; }
    public required string Side { get; set; }  // "swap_buy" | "swap_sell" | "swap_withdraw" | "thesis"
    public required double UsdAmount { get; set; }
    public double? MarketCap { get; set; }
    public string? Comment { get; set; }       // thesis commentary
    public DateTime CreatedAt { get; set; }
}
