namespace TelegramBot.Models;

// Shape of Intel/trader-intel.json — produced offline by scripts/trader-audit (follower-outcome
// simulation over price candles + public profile data), read-only at runtime. Keyed by
// platform + handle rather than Trader.Id so the file survives a trader being deleted and
// re-added, and so it can be generated without access to the live DB. Since categories moved
// into the DB (TraderCategorizerService), the file is only the source of the public-profile
// fields (PnL, followers, links, avatar); TraderIntelService.Compose overlays the live rating.
public class TraderIntelFile
{
    public DateTime GeneratedAt { get; set; }
    public string? Window { get; set; }
    public List<TraderIntelEntry> Traders { get; set; } = new();
}

public class TraderIntelEntry
{
    public string Handle { get; set; } = string.Empty;
    public Platform Platform { get; set; }

    // A TraderCategories id: scalper | flipper | runner | swing | degen | noise | unrated
    public string Style { get; set; } = "unrated";
    public string? Summary { get; set; }

    // Composite ranking (higher is better; held up out of sample). Null = not enough data to
    // rate. Cut = "hard" | "soft" | null. Reasons explain a −EV (noise) verdict.
    public double? Score { get; set; }
    public string? Cut { get; set; }
    public List<string>? Reasons { get; set; }

    // Which posts the rating comes from: callout | thesis | buy
    public string? Basis { get; set; }
    public DateTime? RatedAt { get; set; }

    public int Calls { get; set; }
    public double AlertsPerDay { get; set; }

    // Categories
    public double? MedianMcap { get; set; }
    public string? CapTier { get; set; }      // micro | small | mid | large
    public string? ChainFocus { get; set; }   // SOL | ROBINHOOD | BNB | BASE | ETH | ARC | Multi
    public string? Speed { get; set; }        // minutes | hours | day

    // Follower outcomes (entered on the alert, ~1 minute late), last 30 days
    public double? Hit50In15m { get; set; }
    public double? Hit2xIn24h { get; set; }
    public double? StopOut30 { get; set; }
    public double? MedianMinutesToPeak { get; set; }
    public double? AvgPerCall { get; set; }

    // Sortable scores (see TraderRatingStats): 0-100 percentile of the composite score and of
    // each ingredient, plus the plain-unit numbers behind them. Simulated = calls the score is
    // built on; small samples are shown as low-confidence.
    public int? ScoreRank { get; set; }
    public int? Hit2xRank { get; set; }
    public int? Return1hRank { get; set; }
    public int? StopOutRank { get; set; }
    public int? AvgRank { get; set; }
    public double? MedianReturn1h { get; set; }
    public int? Simulated { get; set; }

    // The trader's own record: rebuilt round trips (FOMO) or public profile (Pump)
    public double? OwnWinRate { get; set; }
    public double? MedianHoldMinutes { get; set; }
    public double? RealizedPnlUsd { get; set; }     // FOMO: our own count since PnlSince
    public DateTime? PnlSince { get; set; }          // FOMO: when the bot added them
    public int? OwnClosedPositions { get; set; }
    public string? BestWindow { get; set; }          // 15m | 1h | 4h | 24h
    public double? PlayEv { get; set; }              // avg result per alert, played their category's way
    public int? PlayCalls { get; set; }
    public double? OwnHoldMinutes { get; set; }
    public int? PnlRank { get; set; }

    // Public on-chain PnL (pump.fun's per-wallet PnL leaderboard) and profile
    public double? Pnl30dUsd { get; set; }
    public double? Pnl7dUsd { get; set; }
    public int? PnlRank30d { get; set; }
    public double? PortfolioPnlUsd { get; set; }
    public double? Callout2xRate { get; set; }   // pump's own 2x rate for their callouts
    public int? Followers { get; set; }
    public string? X { get; set; }
    public string? Wallet { get; set; }
    public string? Avatar { get; set; }

    // Most recent replayed calls (newest first) for the trader profile card. Only sent by the
    // per-trader endpoint; the list endpoint strips them to keep the roster payload small.
    public List<RecentCall>? RecentCalls { get; set; }

    public TraderIntelEntry WithoutCalls()
    {
        var copy = (TraderIntelEntry)MemberwiseClone();
        copy.RecentCalls = null;
        return copy;
    }
}

public class RecentCall
{
    public DateTime T { get; set; }
    public string? Ticker { get; set; }
    public string? Chain { get; set; }
    public double? Mcap { get; set; }
    public double Peak24h { get; set; }       // highest price within 24h, as a multiple of the follower's entry
    public double R1h { get; set; }           // return one hour after entry
    public int MinutesToPeak { get; set; }
}
