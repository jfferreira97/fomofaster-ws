using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TelegramBot.Data;
using TelegramBot.Models;

namespace TelegramBot.Controllers;

[ApiController]
[Route("api/[controller]")]
public class KnownTokensController : ControllerBase
{
    private readonly AppDbContext _dbContext;
    private readonly ILogger<KnownTokensController> _logger;

    public KnownTokensController(AppDbContext dbContext, ILogger<KnownTokensController> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    // GET: api/knowntokens
    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var tokens = await _dbContext.KnownTokens.ToListAsync();
        return Ok(tokens);
    }

    // GET: api/knowntokens/{id}
    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(int id)
    {
        var token = await _dbContext.KnownTokens.FindAsync(id);
        if (token == null)
        {
            return NotFound(new { message = $"Known token with ID {id} not found" });
        }
        return Ok(token);
    }

    // POST: api/knowntokens
    [HttpPost]
    public async Task<IActionResult> Add([FromBody] KnownToken knownToken)
    {
        try
        {
            // Check if symbol already exists
            var existing = await _dbContext.KnownTokens
                .Where(kt => kt.Symbol == knownToken.Symbol)
                .FirstOrDefaultAsync();

            if (existing != null)
            {
                return BadRequest(new { message = $"Token with symbol {knownToken.Symbol} already exists" });
            }

            _dbContext.KnownTokens.Add(knownToken);
            await _dbContext.SaveChangesAsync();

            // Refresh the cache so new token is immediately available
            NotificationsController.RefreshKnownTokensCache();

            _logger.LogInformation("Added known token: {Symbol} with contract {Contract}", knownToken.Symbol, knownToken.ContractAddress);

            return CreatedAtAction(nameof(GetById), new { id = knownToken.Id }, knownToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding known token");
            return StatusCode(500, new { message = "Internal server error" });
        }
    }

    // PUT: api/knowntokens/{id}
    [HttpPut("{id}")]
    public async Task<IActionResult> Update(int id, [FromBody] KnownToken updatedToken)
    {
        try
        {
            var token = await _dbContext.KnownTokens.FindAsync(id);
            if (token == null)
            {
                return NotFound(new { message = $"Known token with ID {id} not found" });
            }

            // Update properties
            token.Symbol = updatedToken.Symbol;
            token.ContractAddress = updatedToken.ContractAddress;
            token.MinMarketCap = updatedToken.MinMarketCap;
            token.Chain = updatedToken.Chain;

            await _dbContext.SaveChangesAsync();

            // Refresh the cache so updates are immediately reflected
            NotificationsController.RefreshKnownTokensCache();

            _logger.LogInformation("Updated known token: {Symbol}", token.Symbol);

            return Ok(token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating known token");
            return StatusCode(500, new { message = "Internal server error" });
        }
    }

    // DELETE: api/knowntokens/{id}
    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id)
    {
        try
        {
            var token = await _dbContext.KnownTokens.FindAsync(id);
            if (token == null)
            {
                return NotFound(new { message = $"Known token with ID {id} not found" });
            }

            _dbContext.KnownTokens.Remove(token);
            await _dbContext.SaveChangesAsync();

            // Refresh the cache so deletions are immediately reflected
            NotificationsController.RefreshKnownTokensCache();

            _logger.LogInformation("Deleted known token: {Symbol}", token.Symbol);

            return Ok(new { message = $"Deleted token {token.Symbol}" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting known token");
            return StatusCode(500, new { message = "Internal server error" });
        }
    }

    // POST: api/knowntokens/refresh-cache
    [HttpPost("refresh-cache")]
    public IActionResult RefreshCache()
    {
        NotificationsController.RefreshKnownTokensCache();
        _logger.LogInformation("Manually refreshed known tokens cache");
        return Ok(new { message = "Cache refreshed successfully" });
    }
}
