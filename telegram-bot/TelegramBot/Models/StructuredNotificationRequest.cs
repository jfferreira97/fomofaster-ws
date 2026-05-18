namespace TelegramBot.Models;

public class StructuredNotificationRequest
{
    public required string WsId { get; set; }
    public required string TradeId { get; set; }
    public required string Trader { get; set; }
    public required string Ticker { get; set; }
    public required string ContractAddress { get; set; }
    public required int NetworkId { get; set; }
    public required string Side { get; set; }
    public required string WsType { get; set; }
    public required double UsdAmount { get; set; }
    public double? MarketCap { get; set; }
    public string? Comment { get; set; }
    public DateTime CreatedAt { get; set; }

    // Raw WS metadata
    public string? UserId { get; set; }
    public string? DisplayName { get; set; }
    public string? ProfilePictureLink { get; set; }
    public double? Price { get; set; }
    public double? Equity { get; set; }
}
