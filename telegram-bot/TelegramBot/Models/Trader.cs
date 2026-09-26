namespace TelegramBot.Models;

public class Trader
{
    public int Id { get; set; }
    public string Handle { get; set; } = string.Empty; // e.g., "frankdegods"
    public Platform Platform { get; set; } = Platform.Fomo;
    public DateTime FirstSeenAt { get; set; }
    public DateTime LastSeenAt { get; set; }

    // Effective category (see TraderCategories): what /manage shows and what category followers
    // are routed by. Set by TraderCategorizerService from the trader's own record unless an admin
    // moved them by hand (IsManuallyRecategorized), in which case the job leaves it alone and only
    // refreshes TraderRating.AutoCategory. Null = never categorized, treated as "unrated".
    public string? Category { get; set; }
    public bool IsManuallyRecategorized { get; set; }
    public DateTime? CategorizedAt { get; set; }

    // Pump traders: public PnL re-pulled daily by TraderCategorizerService from pump.fun's own
    // per-wallet PnL leaderboard and portfolio APIs (their numbers, not ours).
    public string? Wallet { get; set; }
    public string? PumpUserId { get; set; }
    public double? Pnl30dUsd { get; set; }
    public double? Pnl7dUsd { get; set; }
    public int? PnlRank30d { get; set; }
    public double? PortfolioPnlUsd { get; set; }
    public DateTime? PnlUpdatedAt { get; set; }

    // FOMO traders: our own count, rebuilt daily from the buys and sells we've captured since
    // FirstSeenAt. Realized profit on positions they opened and closed since then; FOMO has no
    // public PnL we can read without a logged-in session.
    public double? OwnRealizedUsd { get; set; }
    public int? OwnClosedPositions { get; set; }
    public DateTime? OwnPnlSince { get; set; }   // first day counted: FomoRealizedPnlStartDate or FirstSeenAt, whichever is later
}
