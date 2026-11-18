namespace TelegramBot.Models;

// Row shape for the trader-management page: the full (optionally platform-filtered)
// trader roster, annotated with this specific user's follow state.
public record TraderBrowseEntry(
    int Id,
    string Handle,
    Platform Platform,
    bool IsFollowing,
    decimal? MinValueUsd,
    string Category,
    bool IsMuted,
    string? ViaCategory,
    decimal? MinMarketCap);

// A follower's two per-trader floors, read on every alert from that trader.
public readonly record struct FollowerFloors(decimal? MinValueUsd, decimal? MinMarketCap);
