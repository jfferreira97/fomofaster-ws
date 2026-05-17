namespace TelegramBot.Models;

public class UserTrader
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public int TraderId { get; set; }
    public DateTime FollowedAt { get; set; }

    // Navigation properties
    public User User { get; set; } = null!;
    public Trader Trader { get; set; } = null!;
}