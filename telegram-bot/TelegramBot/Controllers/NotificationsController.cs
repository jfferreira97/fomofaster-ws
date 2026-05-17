using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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
    private readonly ILogger<NotificationsController> _logger;

    public NotificationsController(
        ITelegramService telegramService,
        ITraderService traderService,
        AppDbContext dbContext,
        ILogger<NotificationsController> logger)
    {
        _telegramService = telegramService;
        _traderService = traderService;
        _dbContext = dbContext;
        _logger = logger;
    }

    [HttpPost("structured")]
    public async Task<IActionResult> ReceiveStructuredNotification([FromBody] StructuredNotificationRequest req)
    {
        try
        {
            _logger.LogInformation("WS STRUCTURED NOTIFICATION: {Side} {Ticker} by @{Trader} (tradeId={TradeId})",
                req.Side, req.Ticker, req.Trader, req.TradeId);

            bool alreadyExists = await _dbContext.Notifications.AnyAsync(n => n.TradeId == req.TradeId);
            if (alreadyExists)
            {
                _logger.LogInformation("Duplicate tradeId {TradeId}, skipping", req.TradeId);
                return Ok(new { accepted = false, reason = "duplicate" });
            }

            var chain = req.NetworkId switch
            {
                1399811149 => (Chain?)Chain.SOL,
                56         => (Chain?)Chain.BNB,
                8453       => (Chain?)Chain.BASE,
                143        => (Chain?)Chain.MONAD,
                _          => null
            };

            var notifType = req.Comment != null
                ? NotificationType.Thesis
                : req.Side switch
                {
                    "swap_buy"      => NotificationType.Buy,
                    "swap_sell"     => NotificationType.Sell,
                    "swap_withdraw" => NotificationType.Deposit,
                    _               => NotificationType.Unknown
                };

            var sideWord = req.Side switch
            {
                "swap_buy"      => "bought",
                "swap_sell"     => "sold",
                "swap_withdraw" => "deposited",
                _               => "traded"
            };

            var message = $"@{req.Trader} {sideWord} ${req.UsdAmount:N0} of ${req.Ticker}";
            if (!string.IsNullOrEmpty(req.Comment))
                message += $"\n\n{req.Comment}";

            await _traderService.AddOrUpdateTraderAsync(req.Trader);

            await _telegramService.SendNotificationToAllUsersAsync(
                new NotificationRequest { Message = message },
                contractAddress: req.ContractAddress,
                chain: chain,
                traderHandle: req.Trader,
                ticker: req.Ticker,
                marketCap: req.MarketCap,
                notificationType: notifType,
                tradeId: req.TradeId
            );

            return Ok(new { accepted = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing structured notification (tradeId={TradeId})", req.TradeId);
            return StatusCode(500, new { status = "error", message = "Internal server error" });
        }
    }
}
