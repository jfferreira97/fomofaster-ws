namespace TelegramBot.Models;

// A buy/sell by a pump.fun trader, as the pump sidecar reads it off pump.fun's "following" alerts feed.
// Pump's counterpart to StructuredNotificationRequest's swap_buy / swap_sell.
public class StructuredPumpTradeRequest
{
    // The trade's own transaction signature: unique per trade, so it is the dedupe key (PumpEvents.ExternalId).
    public required string ExternalId { get; set; }

    public required string ActorHandle { get; set; }
    public string? ActorUserId { get; set; }

    public required string CoinMint { get; set; }
    public required int ChainId { get; set; }
    public required string Symbol { get; set; }
    public double? MarketCap { get; set; }

    public required bool IsBuy { get; set; }
    public required double AmountUsd { get; set; }

    // When the trade happened on-chain, per pump.fun.
    public DateTime CreatedAt { get; set; }
}
