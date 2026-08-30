namespace TelegramBot.Models;

public class Notification
{
    public int Id { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? Ticker { get; set; }
    public string? Trader { get; set; }
    public string? ContractAddress { get; set; }
    public Chain? Chain { get; set; }
    public DateTime SentAt { get; set; }
    public decimal? MarketCapAtNotification { get; set; }
    public NotificationType Type { get; set; } = NotificationType.Unknown;
    public Platform Platform { get; set; } = Platform.Fomo;
    public string? FK_WsEvent_WsId { get; set; }
}

public enum NotificationType
{
    // FOMO
    Buy,
    Sell,
    Deposit,
    Thesis,
    Verified,
    CUSTOM_Trending, // custom notification fired as a ConfluenceAlert, not an event from the fomo WS feed

    // Pump — kept distinct from the FOMO types above rather than aliased onto
    // them (e.g. Callout != Thesis), since the two platforms' event shapes
    // and attribution rules (see Repost/Reply) genuinely differ.
    Callout,
    Repost,
    Reply,

    Unknown
}
