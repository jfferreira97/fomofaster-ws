namespace TelegramBot.Models;

public class UserTrader
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public int TraderId { get; set; }
    public DateTime FollowedAt { get; set; }

    // Per-trader alert floor: only notify this user about this trader's Buy/Sell/Callout
    // activity when the trade's USD value is at or above this. Null = no minimum (alert
    // on everything from this trader), matching the pre-existing default behavior.
    public decimal? MinValueUsd { get; set; }

    // Navigation properties
    public User User { get; set; } = null!;
    public Trader Trader { get; set; } = null!;
}