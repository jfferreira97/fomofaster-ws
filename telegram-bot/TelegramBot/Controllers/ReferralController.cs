using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TelegramBot.Data;
using TelegramBot.Services;

namespace TelegramBot.Controllers;

// The referrer's own page (groupchat-bot.tech/r/<token>): how many people came in through their code and how many of those
// subscribed. Counts only: no money, no names, no chat ids. The token in the link is the only credential.
[ApiController]
[Route("api/referral")]
public class ReferralController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ReferralService _referrals;

    public ReferralController(AppDbContext db, ReferralService referrals) { _db = db; _referrals = referrals; }

    [HttpGet("{token}")]
    public async Task<IActionResult> Get(string token)
    {
        Response.Headers["Cache-Control"] = "no-store";
        Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
        if (token.Length is < 16 or > 64) return NotFound();

        var rc = await _db.ReferralCodes.AsNoTracking().FirstOrDefaultAsync(c => c.StatsToken == token);
        if (rc == null) return NotFound();

        var s = await _referrals.StatsAsync(rc, withMoney: false);
        return Ok(new { label = s.Label, signups = s.Signups, subscribed = s.Subscribed, days = s.Days, months = s.Months });
    }
}
