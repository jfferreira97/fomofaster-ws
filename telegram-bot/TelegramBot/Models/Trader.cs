namespace TelegramBot.Models;

public class Trader
{
    public int Id { get; set; }
    public string Handle { get; set; } = string.Empty; // e.g., "frankdegods"
    public DateTime FirstSeenAt { get; set; }
    public DateTime LastSeenAt { get; set; }
}
