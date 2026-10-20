using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using TelegramBot.Data;
using TelegramBot.Models;
using TelegramBot.Services;

namespace TelegramBot.Controllers;

[ApiController]
[Route("api/[controller]")]
public class NotificationsController : ControllerBase
{
    private readonly ITelegramService _telegramService;
    private readonly ITraderService _traderService;
    private readonly AppDbContext _dbContext;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ConfluenceService _confluenceService;
    private readonly AppConfigService _appConfig;
    private readonly ILogger<NotificationsController> _logger;

    public NotificationsController(
        ITelegramService telegramService,
        ITraderService traderService,
        AppDbContext dbContext,
        IHttpClientFactory httpClientFactory,
        ConfluenceService confluenceService,
        AppConfigService appConfig,
        ILogger<NotificationsController> logger)
    {
        _telegramService = telegramService;
        _traderService = traderService;
        _dbContext = dbContext;
        _httpClientFactory = httpClientFactory;
        _confluenceService = confluenceService;
        _appConfig = appConfig;
        _logger = logger;
    }

    // Canary pacing: while PumpTradeAlerts=owner at most one sample every 15s reaches the canary chat.
    private static DateTime _lastCanaryAt = DateTime.MinValue;
    private static readonly object _canaryLock = new();

    private static string FormatMarketCap(double mc) => mc switch
    {
        >= 1_000_000_000 => $"{mc / 1_000_000_000:0.##}b",
        >= 1_000_000     => $"{mc / 1_000_000:0.##}m",
        _                => $"{mc / 1_000:0.##}k"
    };

    private async Task<double?> FetchMarketCapAsync(string contractAddress)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            var url = $"https://api.dexscreener.com/latest/dex/tokens/{contractAddress}";
            var json = await client.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("pairs", out var pairs) || pairs.ValueKind != JsonValueKind.Array)
                return null;

            double? best = null;
            double bestLiquidity = -1;
            foreach (var pair in pairs.EnumerateArray())
            {
                var liq = pair.TryGetProperty("liquidity", out var liqEl)
                    && liqEl.TryGetProperty("usd", out var liqUsd)
                    && liqUsd.ValueKind == JsonValueKind.Number
                    ? liqUsd.GetDouble() : 0;

                if (pair.TryGetProperty("marketCap", out var mc) && mc.ValueKind == JsonValueKind.Number && liq > bestLiquidity)
                {
                    best = mc.GetDouble();
                    bestLiquidity = liq;
                }
            }
            return best;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DexScreener fetch failed for {ContractAddress}", contractAddress);
            return null;
        }
    }

    // One string out of a stored raw event (the feed shows its pictures), null when absent or malformed.
    private static string? RawString(string? raw, params string[] path)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var el = doc.RootElement;
            foreach (var p in path)
                if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(p, out el)) return null;
            return el.ValueKind == JsonValueKind.String ? el.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    [HttpPost("structured")]
    public async Task<IActionResult> ReceiveStructuredNotification([FromBody] StructuredNotificationRequest req)
    {
        var arrivedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        try
        {
            _logger.LogInformation("WS STRUCTURED NOTIFICATION: {Side} {Ticker} by @{Trader} (wsId={WsId} tradeId={TradeId})",
                req.Side, req.Ticker, req.Trader, req.WsId, req.TradeId);

            var wsEvent = await _dbContext.WsEvents.FirstOrDefaultAsync(e => e.WsId == req.WsId);
            if (wsEvent?.Handled == true)
            {
                _logger.LogInformation("WsEvent {WsId} already handled, skipping", req.WsId);
                return Ok(new { accepted = false, reason = "duplicate" });
            }

            var chain = ChainInfo.FromNetworkId(req.NetworkId);
            if (chain is null)
                _logger.LogWarning("Unmapped networkId {NetworkId} on {Ticker} ({ContractAddress}) — the alert still ships, but without chain links or per-chain settings. Add it to ChainInfo.",
                    req.NetworkId, req.Ticker, req.ContractAddress);

            var notifType = req.WsType switch
            {
                "thesis"        => NotificationType.Thesis,
                "swap_buy"      => NotificationType.Buy,
                "swap_sell"     => NotificationType.Sell,
                "swap_withdraw" => NotificationType.Sell,
                "transfer_out"  => NotificationType.Sell,
                _               => NotificationType.Unknown
            };

            string message;
            double? notifMarketCap;
            if (req.WsType == "thesis")
            {
                notifMarketCap = await FetchMarketCapAsync(req.ContractAddress);
                var mc = notifMarketCap.HasValue ? $" (${FormatMarketCap(notifMarketCap.Value)} MC)" : "";
                message = $"{req.Ticker} thesis by {req.Trader}:{mc}\n\n{req.Comment}\n\nCurrent ${req.Ticker} position by {req.Trader}: ${req.UsdAmount:N0}";
            }
            else if (req.Side == "transfer_out" || req.Side == "swap_withdraw")
            {
                notifMarketCap = req.MarketCap;
                var mc = notifMarketCap.HasValue ? $" at ${FormatMarketCap(notifMarketCap.Value)} MC" : "";
                var still = req.Equity.HasValue && req.Equity.Value > 0
                    ? $" (still holding ${req.Equity.Value:N0})"
                    : "";
                message = $"{req.Ticker}{mc} 🔴 @{req.Trader} withdrew ${req.UsdAmount:N0}{still}";
            }
            else
            {
                notifMarketCap = req.MarketCap;
                var sideWord = req.Side switch
                {
                    "swap_buy"  => "bought",
                    "swap_sell" => "sold",
                    _           => "traded"
                };
                var emoji = req.Side == "swap_buy" ? "🟢" : "🔴";
                var mc = notifMarketCap.HasValue ? $" at ${FormatMarketCap(notifMarketCap.Value)} MC" : "";
                message = $"{req.Ticker}{mc} {emoji} {req.Trader} {sideWord} ${req.UsdAmount:0.##}";
            }

            await _traderService.AddOrUpdateTraderAsync(req.Trader);

            await _telegramService.SendNotificationToAllUsersAsync(
                new NotificationRequest { Message = message },
                contractAddress: req.ContractAddress,
                chain: chain,
                traderHandle: req.Trader,
                ticker: req.Ticker,
                marketCap: notifMarketCap,
                notificationType: notifType,
                fomoWsTradeId: req.WsId,
                usdAmount: req.UsdAmount,
                tokenImage: RawString(wsEvent?.RawJson, "tokenImageUrl"),
                traderImage: req.ProfilePictureLink ?? RawString(wsEvent?.RawJson, "profilePictureLink"),
                trace: new AlertTrace(null, null, null, arrivedMs)
            );

            await _dbContext.WsEvents
                .Where(e => e.WsId == req.WsId && !e.Handled)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.Handled, true));

            // Non-blocking hand-off to confluence detection (TRENDING alerts)
            _confluenceService.Enqueue(new ConfluenceTradeEvent(
                Type: req.WsType,
                Platform: Platform.Fomo,
                Trader: req.Trader,
                Ticker: req.Ticker,
                TokenAddress: req.ContractAddress,
                NetworkId: req.NetworkId,
                UsdAmount: (decimal)req.UsdAmount,
                MarketCap: req.MarketCap.HasValue ? (decimal)req.MarketCap.Value : null,
                OccurredAt: DateTime.UtcNow));

            return Ok(new { accepted = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing structured notification (tradeId={TradeId})", req.TradeId);
            return StatusCode(500, new { status = "error", message = "Internal server error" });
        }
    }

    // Pump transactions: the counterpart to "structured" above (FOMO swap_buy / swap_sell). Same message
    // shape, same fan-out (follow filter, per-trader MinValueUsd, chain settings, repeat window, tiers);
    // the per-user toggle is NotifyPumpBuySell. Rollout is gated by AppConfig PumpTradeAlerts (off|owner|on).
    [HttpPost("pump-trade")]
    public async Task<IActionResult> ReceiveStructuredPumpTrade([FromBody] StructuredPumpTradeRequest req)
    {
        var arrivedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();   // stage stamp: this request reached the backend
        try
        {
            var mode = (await _appConfig.GetAsync("PumpTradeAlerts"))?.Trim().ToLowerInvariant() ?? "owner";
            if (mode == "off")
                return Ok(new { accepted = false, reason = "disabled" });

            var pumpEvent = await _dbContext.PumpEvents.FirstOrDefaultAsync(e => e.ExternalId == req.ExternalId);
            if (pumpEvent?.Handled == true)
                return Ok(new { accepted = false, reason = "duplicate" });

            var chain = ChainInfo.FromNetworkId(req.ChainId);
            // Every live trade tells the trending alerts where this token is right now.
            if (req.MarketCap is > 0) _confluenceService.NoteMarketCap(req.CoinMint, (decimal)req.MarketCap.Value);
            var sideWord = req.IsBuy ? "bought" : "sold";
            var emoji = req.IsBuy ? "🟢" : "🔴";
            var mc = req.MarketCap.HasValue ? $" at ${FormatMarketCap(req.MarketCap.Value)} MC" : "";
            var message = $"{req.Symbol}{mc} {emoji} {req.ActorHandle} {sideWord} ${req.AmountUsd:0.##}";

            _logger.LogInformation("PUMP TRADE [{Mode}]: {Message} (tx={Tx})", mode, message, req.ExternalId);

            if (mode != "on")
            {
                // Canary: nobody but the configured chat sees anything, and only a sample of it.
                var canary = await _appConfig.GetAsync("PumpTradeCanaryChatId");
                var send = false;
                lock (_canaryLock)
                {
                    if ((DateTime.UtcNow - _lastCanaryAt).TotalSeconds >= 15) { _lastCanaryAt = DateTime.UtcNow; send = true; }
                }
                if (send && long.TryParse(canary, out var canaryChat))
                {
                    var ageS = Math.Max(0, (DateTime.UtcNow - req.CreatedAt).TotalSeconds);
                    await _telegramService.SendPlainMessageAsync(canaryChat,
                        $"🧪 canary, only you see this ({ageS:0.#}s after the trade)\n💊 | {message}\n\nContract: {req.CoinMint}");
                }
            }
            else
            {
                // Pump buys count towards TRENDING next to FOMO buys and Pump callouts; sells only turn a counted trader's
                // bubble red, exactly as on FOMO (non-blocking).
                _confluenceService.Enqueue(new ConfluenceTradeEvent(
                    Type: req.IsBuy ? "swap_buy" : "swap_sell",
                    Platform: Platform.Pump,
                    Trader: req.ActorHandle,
                    Ticker: req.Symbol,
                    TokenAddress: req.CoinMint,
                    NetworkId: req.ChainId,
                    UsdAmount: (decimal)req.AmountUsd,
                    MarketCap: req.MarketCap.HasValue ? (decimal)req.MarketCap.Value : null,
                    OccurredAt: DateTime.UtcNow));

                // Deliberately no AddOrUpdateTraderAsync here: a trade must never create a trader (that would
                // auto-follow it for everyone with auto-follow on). Unknown traders simply have no followers.
                await _telegramService.SendNotificationToAllUsersAsync(
                    new NotificationRequest { Message = message },
                    contractAddress: req.CoinMint,
                    chain: chain,
                    traderHandle: req.ActorHandle,
                    ticker: req.Symbol,
                    marketCap: req.MarketCap,
                    notificationType: req.IsBuy ? NotificationType.Buy : NotificationType.Sell,
                    platform: Platform.Pump,
                    usdAmount: req.AmountUsd,
                    tokenImage: RawString(pumpEvent?.RawJson, "coinImage"),
                    traderImage: RawString(pumpEvent?.RawJson, "author", "profileImage"),
                    trace: new AlertTrace(req.Via, new DateTimeOffset(DateTime.SpecifyKind(req.CreatedAt, DateTimeKind.Utc)).ToUnixTimeMilliseconds(), req.RecvAtMs, arrivedMs)
                );
            }

            await _dbContext.PumpEvents
                .Where(e => e.ExternalId == req.ExternalId && !e.Handled)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.Handled, true));

            return Ok(new { accepted = true, mode });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing pump trade (tx={Tx})", req.ExternalId);
            return StatusCode(500, new { status = "error", message = "Internal server error" });
        }
    }

    [HttpPost("pump-structured")]
    public async Task<IActionResult> ReceiveStructuredPumpNotification([FromBody] StructuredPumpNotificationRequest req)
    {
        var arrivedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        try
        {
            _logger.LogInformation("PUMP STRUCTURED NOTIFICATION: {Kind} {Symbol} by @{Actor} (externalId={ExternalId})",
                req.Kind, req.Symbol, req.ActorHandle, req.ExternalId);

            var pumpEvent = await _dbContext.PumpEvents.FirstOrDefaultAsync(e => e.ExternalId == req.ExternalId);
            if (pumpEvent?.Handled == true)
            {
                _logger.LogInformation("PumpEvent {ExternalId} already handled, skipping", req.ExternalId);
                return Ok(new { accepted = false, reason = "duplicate" });
            }

            var chain = ChainInfo.FromNetworkId(req.ChainId);
            if (chain is null)
                _logger.LogWarning("Unmapped chainId {ChainId} on {Symbol} ({CoinMint}) — the alert still ships, but without chain links or per-chain settings. Add it to ChainInfo.",
                    req.ChainId, req.Symbol, req.CoinMint);
            var mc = req.MarketCap.HasValue ? $" (${FormatMarketCap(req.MarketCap.Value)} MC)" : "";

            var notifType = req.Kind switch
            {
                "callout" => NotificationType.Callout,
                "repost"  => NotificationType.Repost,
                "reply"   => NotificationType.Reply,
                _         => NotificationType.Unknown
            };

            string message = req.Kind switch
            {
                "callout" =>
                    $"{req.Symbol} callout by {req.ActorHandle}:{mc}\n\n{req.Thesis}\n\nCurrent ${req.Symbol} position by {req.ActorHandle}: ${req.PositionCostBasisUsd ?? 0:N0}",
                "repost" =>
                    $"{req.Symbol}{mc} 🔁 {req.ActorHandle} reposted {req.OriginalAuthorHandle}'s callout:\n\n{req.Thesis}",
                "reply" =>
                    $"{req.Symbol}{mc} 💬 {req.ActorHandle} replied on {req.OriginalAuthorHandle}'s callout:\n\n{req.ReplyContent}",
                _ => $"{req.Symbol}{mc} — {req.ActorHandle}"
            };

            await _traderService.AddOrUpdateTraderAsync(req.ActorHandle, Platform.Pump);

            await _telegramService.SendNotificationToAllUsersAsync(
                new NotificationRequest { Message = message },
                contractAddress: req.CoinMint,
                chain: chain,
                traderHandle: req.ActorHandle,
                ticker: req.Symbol,
                marketCap: req.MarketCap,
                notificationType: notifType,
                platform: Platform.Pump,
                usdAmount: req.PositionCostBasisUsd,
                tokenImage: RawString(pumpEvent?.RawJson, "coinImage"),
                traderImage: RawString(pumpEvent?.RawJson, "author", "profileImage"),
                trace: new AlertTrace("callout", new DateTimeOffset(DateTime.SpecifyKind(req.CreatedAt, DateTimeKind.Utc)).ToUnixTimeMilliseconds(), null, arrivedMs)
            );

            // Non-blocking hand-off to confluence detection (TRENDING alerts) — callouts
            // only; repost/reply aren't "an account calling something out" in their own right.
            if (req.Kind == "callout")
            {
                _confluenceService.Enqueue(new ConfluenceTradeEvent(
                    Type: "callout",
                    Platform: Platform.Pump,
                    Trader: req.ActorHandle,
                    Ticker: req.Symbol,
                    TokenAddress: req.CoinMint,
                    NetworkId: req.ChainId,
                    UsdAmount: (decimal)(req.PositionCostBasisUsd ?? 0),
                    MarketCap: req.MarketCap.HasValue ? (decimal)req.MarketCap.Value : null,
                    OccurredAt: DateTime.UtcNow));
            }

            await _dbContext.PumpEvents
                .Where(e => e.ExternalId == req.ExternalId && !e.Handled)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.Handled, true));

            return Ok(new { accepted = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing pump structured notification (externalId={ExternalId})", req.ExternalId);
            return StatusCode(500, new { status = "error", message = "Internal server error" });
        }
    }
}
