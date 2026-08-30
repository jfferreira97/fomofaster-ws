namespace TelegramBot.Models;

public class Trader
{
    public int Id { get; set; }
    public string Handle { get; set; } = string.Empty; // e.g., "frankdegods"
    public Platform Platform { get; set; } = Platform.Fomo;
    public DateTime FirstSeenAt { get; set; }
    public DateTime LastSeenAt { get; set; }

    // Pump.fun's own "verified" badge (from pnl-leaderboard's isVerified field) — Fomo
    // traders are never marked verified here, this is a Pump-only concept.
    public bool IsPumpVerified { get; set; } = false;
}
