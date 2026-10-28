using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TelegramBot.Data;
using TelegramBot.Models;
using TelegramBot.Services;

namespace TelegramBot.Controllers;

// The owner's side of referral codes (dashboard Referrals tab). Like the rest of /api/dashboard it has no login: the backend only
// listens on 127.0.0.1 and Caddy's allow-list never exposes it.
[ApiController]
[Route("api/dashboard/referrals")]
public class ReferralAdminController : ControllerBase
{
    private const string BotLink = "https://t.me/GROUPCHAT_ALERTS_BOT";
    private const string SiteLink = "https://groupchat-bot.tech";

    private readonly AppDbContext _db;
    private readonly ReferralService _referrals;

    public ReferralAdminController(AppDbContext db, ReferralService referrals) { _db = db; _referrals = referrals; }

    public sealed record CreateRequest(string? Label, string? Code, int? DiscountPercent);
    public sealed record ActiveRequest(bool Active);
    public sealed record DiscountRequest(int Percent);
    public sealed record CommissionRequest(decimal Percent, bool OnRenewals);
    public sealed record PayoutRequest(decimal AmountSol, string? Note, DateTime? At);

    [HttpGet]
    public async Task<IActionResult> List()
    {
        var codes = await _db.ReferralCodes.AsNoTracking().OrderByDescending(c => c.CreatedAt).ToListAsync();
        var rows = new List<object>();
        foreach (var c in codes)
        {
            var s = await _referrals.StatsAsync(c, withMoney: true);
            var l = await _referrals.LedgerAsync(c);
            rows.Add(new
            {
                id = c.Id, code = c.Code, label = c.Label, discountPercent = c.DiscountPercent, active = c.IsActive, createdAt = c.CreatedAt,
                signups = s.Signups, subscribed = s.Subscribed, solCollected = s.SolCollected,
                commissionPercent = c.CommissionPercent, commissionOnRenewals = c.CommissionOnRenewals,
                earned = l.Earned, paid = l.Paid, owed = l.Owed,
                botLink = $"{BotLink}?start=r_{c.Code}", siteLink = $"{SiteLink}/?r={c.Code}", statsLink = $"{SiteLink}/r/{c.StatsToken}",
            });
        }
        return Ok(new { status = "success", codes = rows });
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateRequest req)
    {
        var label = (req.Label ?? "").Trim();
        if (label.Length is < 1 or > 80) return BadRequest(new { status = "error", message = "Give the code a label (who it is for), up to 80 characters." });

        string? code;
        if (string.IsNullOrWhiteSpace(req.Code))
        {
            code = ReferralService.NewCode();
            while (await _db.ReferralCodes.AnyAsync(c => c.Code == code)) code = ReferralService.NewCode();
        }
        else
        {
            code = ReferralService.Normalize(req.Code);
            if (code == null) return BadRequest(new { status = "error", message = "A code is 3 to 16 letters or digits." });
            if (await _db.ReferralCodes.AnyAsync(c => c.Code == code)) return Conflict(new { status = "error", message = "That code already exists." });
        }

        var pct = req.DiscountPercent ?? ReferralService.DefaultDiscountPercent;
        if (pct is < 0 or > 90) return BadRequest(new { status = "error", message = "The discount is 0 to 90 percent." });

        _db.ReferralCodes.Add(new ReferralCode
        {
            Code = code, Label = label, DiscountPercent = pct, IsActive = true, CreatedAt = DateTime.UtcNow, StatsToken = ReferralService.NewToken(),
        });
        await _db.SaveChangesAsync();
        return Ok(new { status = "success", code });
    }

    // The full books of one code: month by month, who paid, what is owed and what has been paid out.
    [HttpGet("{id:int}/ledger")]
    public async Task<IActionResult> GetLedger(int id)
    {
        var c = await _db.ReferralCodes.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (c == null) return NotFound();
        return Ok(new { status = "success", ledger = await _referrals.LedgerAsync(c) });
    }

    [HttpPost("{id:int}/commission")]
    public async Task<IActionResult> SetCommission(int id, [FromBody] CommissionRequest req)
    {
        if (req.Percent < 0 || req.Percent > 100) return BadRequest(new { status = "error", message = "The commission is 0 to 100 percent." });
        var c = await _db.ReferralCodes.FirstOrDefaultAsync(x => x.Id == id);
        if (c == null) return NotFound();
        c.CommissionPercent = Math.Round(req.Percent, 2);
        c.CommissionOnRenewals = req.OnRenewals;
        await _db.SaveChangesAsync();
        return Ok(new { status = "success" });
    }

    [HttpPost("{id:int}/payouts")]
    public async Task<IActionResult> AddPayout(int id, [FromBody] PayoutRequest req)
    {
        if (req.AmountSol <= 0 || req.AmountSol > 100000) return BadRequest(new { status = "error", message = "Give the amount paid, in SOL." });
        if (!await _db.ReferralCodes.AnyAsync(x => x.Id == id)) return NotFound();
        _db.ReferralPayouts.Add(new ReferralPayout
        {
            ReferralCodeId = id, AmountSol = Math.Round(req.AmountSol, 4), Note = (req.Note ?? "").Trim()[..Math.Min(200, (req.Note ?? "").Trim().Length)],
            At = req.At?.ToUniversalTime() ?? DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();
        return Ok(new { status = "success" });
    }

    [HttpDelete("{id:int}/payouts/{payoutId:int}")]
    public async Task<IActionResult> DeletePayout(int id, int payoutId)
    {
        var p = await _db.ReferralPayouts.FirstOrDefaultAsync(x => x.Id == payoutId && x.ReferralCodeId == id);
        if (p == null) return NotFound();
        _db.ReferralPayouts.Remove(p);
        await _db.SaveChangesAsync();
        return Ok(new { status = "success" });
    }

    [HttpPost("{id:int}/active")]
    public async Task<IActionResult> SetActive(int id, [FromBody] ActiveRequest req)
    {
        var c = await _db.ReferralCodes.FirstOrDefaultAsync(x => x.Id == id);
        if (c == null) return NotFound();
        c.IsActive = req.Active;
        await _db.SaveChangesAsync();
        return Ok(new { status = "success" });
    }

    [HttpPost("{id:int}/discount")]
    public async Task<IActionResult> SetDiscount(int id, [FromBody] DiscountRequest req)
    {
        if (req.Percent is < 0 or > 90) return BadRequest(new { status = "error", message = "The discount is 0 to 90 percent." });
        var c = await _db.ReferralCodes.FirstOrDefaultAsync(x => x.Id == id);
        if (c == null) return NotFound();
        c.DiscountPercent = req.Percent;
        await _db.SaveChangesAsync();
        return Ok(new { status = "success" });
    }
}
