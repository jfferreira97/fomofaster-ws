namespace TelegramBot.Models;

public class PumpEvent
{
    public int Id { get; set; }

    /// Dedup key: callout.calloutId (kind=callout), repost.id (kind=repost),
    /// or reply.reply.id (kind=reply) — see PumpNotificationsController for the unwrap.
    public string ExternalId { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty; // callout | repost | reply
    public string? ActorUserId { get; set; }
    public string? ActorHandle { get; set; }
    public string? CoinMint { get; set; }
    public int? ChainId { get; set; }
    public string? Symbol { get; set; }
    public DateTime? CreatedAt { get; set; }
    public DateTime ReceivedAt { get; set; }
    public string RawJson { get; set; } = string.Empty;
    public bool Handled { get; set; } = false;
}
