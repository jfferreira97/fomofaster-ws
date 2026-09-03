namespace TelegramBot.Models;

// Row shape for the trader-management page: the full (optionally platform-filtered)
// trader roster, annotated with this specific user's follow state.
public record TraderBrowseEntry(
    int Id,
    string Handle,
    Platform Platform,
    bool IsPumpVerified,
    bool IsFollowing,
    decimal? MinValueUsd);
