using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TelegramBot.Data;
using TelegramBot.Models;
using TelegramBot.Services;

namespace TelegramBot.Controllers;

// Admin side of trader categories, behind the dashboard (not exposed through Caddy): the board
// of every trader by category, moving one by hand, and kicking off a categorizer run.
[ApiController]
[Route("api/dashboard")]
public class TraderCategoriesController : ControllerBase
{
    private readonly AppDbContext _dbContext;
    private readonly TraderCategorizerService _categorizer;
    private readonly TraderIntelService _intel;
    private readonly ILogger<TraderCategoriesController> _logger;

    public TraderCategoriesController(AppDbContext dbContext, TraderCategorizerService categorizer, TraderIntelService intel,
        ILogger<TraderCategoriesController> logger)
    {
        _dbContext = dbContext;
        _categorizer = categorizer;
        _intel = intel;
        _logger = logger;
    }

    [HttpGet("categories")]
    public async Task<IActionResult> GetBoard()
    {
        var traders = await _dbContext.Traders.AsNoTracking().OrderBy(t => t.Handle).ToListAsync();
        var ratings = await _dbContext.TraderRatings.AsNoTracking().ToDictionaryAsync(r => r.TraderId);
        var directFollowers = await _dbContext.UserTraders.GroupBy(ut => ut.TraderId)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count);
        var categoryFollowers = await _dbContext.UserCategoryFollows.GroupBy(f => f.Category)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count);
        var status = _categorizer.Status;

        return Ok(new
        {
            status = "success",
            categorizer = status,
            categories = TraderCategories.All.Select(c => new
            {
                id = c.Id, label = c.Label, emoji = c.Emoji, description = c.Description,
                followers = categoryFollowers.GetValueOrDefault(c.Id)
            }),
            traders = traders.Select(t =>
            {
                var r = ratings.GetValueOrDefault(t.Id);
                var stats = TraderCategorizerService.ParseStats(r?.StatsJson);
                return new
                {
                    id = t.Id,
                    handle = t.Handle,
                    platform = t.Platform.ToString(),
                    category = TraderCategories.Effective(t.Category),
                    autoCategory = r?.AutoCategory,
                    isManual = t.IsManuallyRecategorized,
                    categorizedAt = t.CategorizedAt,
                    basis = r?.Basis,
                    score = r?.Score,
                    cut = r?.Cut,
                    calls = r?.Calls ?? 0,
                    simulated = r?.Simulated ?? 0,
                    alertsPerDay = r?.AlertsPerDay ?? 0,
                    summary = stats.Summary,
                    reasons = stats.Reasons,
                    avgPerCall = stats.AvgPerCall,
                    scoreRank = stats.ScoreRank,
                    hit2xIn24h = stats.Hit2xIn24h,
                    stopOut30 = stats.StopOut30,
                    medianMcap = stats.MedianMcap,
                    ratedAt = r?.ComputedAt,
                    followers = directFollowers.GetValueOrDefault(t.Id),
                    avatar = _intel.Get(t.Platform, t.Handle)?.Avatar
                };
            })
        });
    }

    // Move a trader by hand. The job won't touch their category again until it's reset:
    // Category = null (or "auto") hands them back to the job at whatever it last computed.
    [HttpPost("traders/{traderId:int}/category")]
    public async Task<IActionResult> SetCategory(int traderId, [FromBody] SetTraderCategoryRequest request)
    {
        var trader = await _dbContext.Traders.FindAsync(traderId);
        if (trader == null)
            return NotFound(new { status = "error", message = "No such trader" });

        var reset = string.IsNullOrEmpty(request.Category) || request.Category == "auto";
        if (!reset && !TraderCategories.IsValid(request.Category))
            return BadRequest(new { status = "error", message = "Unknown category" });

        var from = trader.Category;
        if (reset)
        {
            var rating = await _dbContext.TraderRatings.FindAsync(traderId);
            trader.Category = rating?.AutoCategory ?? TraderCategories.Unrated;
            trader.IsManuallyRecategorized = false;
        }
        else
        {
            trader.Category = request.Category;
            trader.IsManuallyRecategorized = true;
        }
        trader.CategorizedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Admin moved trader {Handle} ({Platform}): {From} -> {To}{Manual}",
            trader.Handle, trader.Platform, from, trader.Category, reset ? " (back to automatic)" : " (manual)");
        return Ok(new { status = "success", category = trader.Category, isManual = trader.IsManuallyRecategorized });
    }

    [HttpPost("categorizer/run")]
    public IActionResult RunCategorizer()
    {
        var started = _categorizer.TriggerNow();
        return Ok(new { status = "success", started, categorizer = _categorizer.Status });
    }
}

public record SetTraderCategoryRequest(string? Category);
