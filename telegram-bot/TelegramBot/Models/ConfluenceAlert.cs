namespace TelegramBot.Models;

public class ConfluenceAlert
{
    public int Id { get; set; }
    public string TokenAddress { get; set; } = string.Empty;
    public string? Ticker { get; set; }
    public int? NetworkId { get; set; }
    public DateTime WindowStartedAt { get; set; }
    public int TraderCount { get; set; }
    public decimal TotalUsd { get; set; }
    public DateTime FiredAt { get; set; }
}
