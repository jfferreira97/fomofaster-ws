namespace TelegramBot.Models;

public class StructuredPumpNotificationRequest
{
    public required string ExternalId { get; set; }
    public required string Kind { get; set; } // callout | repost | reply

    // Who performed the action (post/repost/reply) — this is who follow-filtering
    // is keyed on, not necessarily who wrote the original thesis (see OriginalAuthorHandle).
    public required string ActorHandle { get; set; }
    public string? ActorUserId { get; set; }

    public required string CoinMint { get; set; }
    public required int ChainId { get; set; }
    public required string Symbol { get; set; }
    public double? MarketCap { get; set; }

    // Original callout attribution — same as ActorHandle for kind=callout,
    // but distinct for repost/reply where the thesis being surfaced isn't the actor's own.
    public string? OriginalAuthorHandle { get; set; }
    public string? Thesis { get; set; }
    public double? PositionCostBasisUsd { get; set; }

    // reply-only
    public string? ReplyContent { get; set; }

    public DateTime CreatedAt { get; set; }
}
