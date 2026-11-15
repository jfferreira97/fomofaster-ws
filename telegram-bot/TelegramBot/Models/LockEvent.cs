namespace TelegramBot.Models;

// What each user's lock limits are checked against (User.LockMin*/LockMax*). Null = unknown, and unknown never filters
// anyone out: a slow or missing lookup must not cost an alert.
public record LockFacts(decimal? PercentSupply, double? MarketCapUsd, DateTime? TokenCreatedAt, double? LockDays);

// A new Streamflow lock (token lock or vesting) created on Solana, one row per creation transaction, whether it was
// alerted or skipped (SkipReason says why). See StreamflowLockService.
public class LockEvent
{
    public int Id { get; set; }
    public string Signature { get; set; } = string.Empty;
    public string Mint { get; set; } = string.Empty;
    public string? Symbol { get; set; }
    public string? Name { get; set; }
    public string StreamAccount { get; set; } = string.Empty;   // Streamflow's metadata account: its page is streamflow.finance/.../<this>
    public string? Sender { get; set; }
    public decimal Amount { get; set; }                          // tokens locked (UI units)
    public decimal? PercentSupply { get; set; }                  // of the token's total supply
    public decimal? UsdValue { get; set; }
    public decimal? MarketCapUsd { get; set; }
    public DateTime? StartAt { get; set; }
    public DateTime? CliffAt { get; set; }                       // first tokens claimable
    public DateTime? EndAt { get; set; }                         // everything claimable; null = no fixed end (price-based unlocks)
    public DateTime? PairCreatedAt { get; set; }                 // the token's oldest DexScreener pair
    public DateTime CreatedAt { get; set; }                      // block time of the lock
    public DateTime ReceivedAt { get; set; }
    public string? SkipReason { get; set; }                      // null = alerted
}
