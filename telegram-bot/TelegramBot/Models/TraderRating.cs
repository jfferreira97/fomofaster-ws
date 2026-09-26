namespace TelegramBot.Models;

// The categorizer's latest verdict on one trader. AutoCategory is always what the numbers say;
// Trader.Category is what's in effect (the same, unless an admin moved the trader by hand).
public class TraderRating
{
    public int TraderId { get; set; }
    public string AutoCategory { get; set; } = TraderCategories.Unrated;

    // Which posts the rating was built from: callout (pump.fun), thesis (FOMO), or buy (FOMO
    // traders without enough theses to rate).
    public string? Basis { get; set; }
    public double? Score { get; set; }                   // composite; higher is better
    public string? Cut { get; set; }                     // hard | soft | null
    public int Calls { get; set; }
    public int Simulated { get; set; }
    public double AlertsPerDay { get; set; }

    // Everything shown on /manage and the dashboard (a TraderIntelEntry's computed fields plus
    // the reasons for a −EV verdict), kept as JSON because it's display-only.
    public string StatsJson { get; set; } = "{}";
    public DateTime ComputedAt { get; set; }

    public Trader Trader { get; set; } = null!;
}
