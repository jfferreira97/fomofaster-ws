namespace TelegramBot.Models;

public class WsEvent
{
    public int Id { get; set; }
    public string WsId { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string? UserId { get; set; }
    public string? UserHandle { get; set; }
    public string? DisplayName { get; set; }
    public string? TradeId { get; set; }
    public string? TokenAddress { get; set; }
    public int? NetworkId { get; set; }
    public string? Ticker { get; set; }
    public DateTime? CreatedAt { get; set; }
    public DateTime ReceivedAt { get; set; }
    public decimal? Equity { get; set; }
    public decimal? Price { get; set; }
    public decimal? MarketCap { get; set; }
    public decimal? UsdAmount { get; set; }
    // Milestone-specific (body fields)
    public string? Tag { get; set; }
    public decimal? TotalCostBasis { get; set; }
    public decimal? TotalPnlUsd { get; set; }
    public decimal? TotalPercentagePnl { get; set; }
    public DateTime? EntryTime { get; set; }
    public bool? ShowAbsolutePnl { get; set; }
    public string RawJson { get; set; } = string.Empty;
    public bool Handled { get; set; } = false;
}
