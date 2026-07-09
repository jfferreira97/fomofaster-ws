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
    private readonly ILogger<NotificationsController> _logger;

    public NotificationsController(
        ITelegramService telegramService,
        ITraderService traderService,
        AppDbContext dbContext,
        IHttpClientFactory httpClientFactory,
        ILogger<NotificationsController> logger)
    {
        _telegramService = telegramService;
        _traderService = traderService;
        _dbContext = dbContext;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

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

    [HttpPost("structured")]
    public async Task<IActionResult> ReceiveStructuredNotification([FromBody] StructuredNotificationRequest req)
    {
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
                message = $"💥 {req.Ticker} thesis by {req.Trader}:{mc}\n\n{req.Comment}\n\nCurrent ${req.Ticker} position by {req.Trader}: ${req.UsdAmount:N0}";
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
                fomoWsTradeId: req.WsId
            );

            await _dbContext.WsEvents
                .Where(e => e.WsId == req.WsId && !e.Handled)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.Handled, true));

            return Ok(new { accepted = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing structured notification (tradeId={TradeId})", req.TradeId);
            return StatusCode(500, new { status = "error", message = "Internal server error" });
        }
    }
}
