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

    // Per-trader market cap floor: only notify this user about this trader's alerts on a coin whose market cap (as the
    // alert gives it) is at or above this. Null = no minimum. An alert without a known market cap is not held back.
    public decimal? MinMarketCap { get; set; }

    // Set when a followed category added this follow (its category id); null when the user
    // followed the trader themselves. Unfollowing the category removes only these. Following
    // the trader explicitly claims the row (back to null), so it survives the category going.
    public string? ViaCategory { get; set; }

    // Navigation properties
    public User User { get; set; } = null!;
    public Trader Trader { get; set; } = null!;
}