namespace TelegramBot.Models;

public class UserChainSetting
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public Chain Chain { get; set; }

    // true = no notifications on this chain for this user at all, regardless of MinMarketCap.
    public bool IsDisabled { get; set; }

    // Only deliver Buy/Sell/Callout-style notifications on this chain when the token's
    // market cap at notification time is at or above this. Null = no minimum. Mirrors
    // UserTrader.MinValueUsd's semantics but keyed by chain instead of by trader.
    public decimal? MinMarketCap { get; set; }

    // Independent of IsDisabled: lets a user keep a chain fully on for Buy/Sell/Callout while
    // muting just its Trending signal (or vice versa isn't possible — IsDisabled always wins).
    public bool TrendingDisabled { get; set; }

    public User User { get; set; } = null!;
}
