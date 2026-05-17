namespace TelegramBot.Models;

public class Notification
{
    public int Id { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? Ticker { get; set; }
    public string? Trader { get; set; }
    public bool HasCA { get; set; }
    public string? ContractAddress { get; set; }
    public Chain? Chain { get; set; }
    public DateTime SentAt { get; set; }

    // Lookup tracking
    public ContractAddressSource? ContractAddressSource { get; set; }
    public int TimesCacheHit { get; set; }
    public int TimesDexScreenerApiHit { get; set; }
    public int TimesHeliusApiHit { get; set; }
    public TimeSpan? LookupDuration { get; set; }
    public bool WasRetried { get; set; }  // True if contract was found via retry service

    // Market data at notification time
    public decimal? MarketCapAtNotification { get; set; }

    // DexScreener lookup diagnostics (JSON array of LookupCandidate)
    public string? LookupDiagnostics { get; set; }

    public NotificationType Type { get; set; } = NotificationType.Unknown;

    // WS-sourced rows: unique trade ID from FOMO's feed (null for Android-sourced rows)
    public string? TradeId { get; set; }
}

public enum NotificationType
{
    Buy,
    Sell,
    Deposit,
    Thesis,
    Verified,
    Unknown
}