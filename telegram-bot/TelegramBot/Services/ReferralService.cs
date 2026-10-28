using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using TelegramBot.Data;
using TelegramBot.Models;

namespace TelegramBot.Services;

public enum AttachResult { Attached, AlreadyHasCode, Unknown, Throttled }

public sealed record RefDay(string Day, int Signups, int Subscribed);

public sealed record LedgerMonth(string Month, int NewPayers, decimal FirstSol, decimal RenewalSol, decimal Commission);
public sealed record LedgerCustomer(long ChatId, string? Username, string? FirstName, DateTime ReferredAt, DateTime FirstPaidAt, int Payments, decimal Sol);
public sealed record LedgerPayout(int Id, DateTime At, decimal AmountSol, string Note);
// Owner-only: what a code's people paid, what that earns the referrer, what has been paid out and what is still owed.
public sealed record Ledger(decimal CommissionPercent, bool OnRenewals, decimal FirstSol, decimal RenewalSol, decimal Earned, decimal Paid, decimal Owed,
    List<LedgerMonth> Months, List<LedgerCustomer> Customers, List<LedgerPayout> Payouts);
// SolCollected is only filled for the owner's dashboard; the referrer's own page never gets money.
public sealed record RefStats(string Label, int Signups, int Subscribed, List<RefDay> Days, List<RefDay> Months, decimal? SolCollected);

// Referral codes: who came through which code, what the first month costs them, and the counts behind each code's page.
// Nothing here is mentioned to users anywhere; see ReferralCode.
public sealed class ReferralService
{
    public const int DefaultDiscountPercent = 25;
    private const string CodeAlphabet = "abcdefghjkmnpqrstuvwxyz23456789";   // no 0/o/1/l/i: codes get read out and retyped
    private const string TokenAlphabet = "abcdefghijklmnopqrstuvwxyz0123456789";

    private static readonly Regex StartCode = new(@"(?:^|_)r_([a-z0-9]{3,16})(?=_|$)", RegexOptions.Compiled);

    // Wrong guesses per chat: 5 an hour, so nobody can walk through the code space from /ref.
    private static readonly ConcurrentDictionary<long, (DateTime Since, int Misses)> Misses = new();

    private readonly AppDbContext _db;
    private readonly ILogger<ReferralService> _logger;

    public ReferralService(AppDbContext db, ILogger<ReferralService> logger) { _db = db; _logger = logger; }

    // "ABC-123 " -> "abc123"; null when it cannot be a code at all.
    public static string? Normalize(string? raw)
    {
        var s = new string((raw ?? "").Where(char.IsAsciiLetterOrDigit).ToArray()).ToLowerInvariant();
        return s.Length is >= 3 and <= 16 ? s : null;
    }

    // The code carried by a /start payload: "r_abc123", "web_hero_r_abc123_u_x_v_ab12cd34"...
    public static string? FromStartPayload(string? payload)
    {
        var m = StartCode.Match((payload ?? "").ToLowerInvariant());
        return m.Success ? m.Groups[1].Value : null;
    }

    public async Task<AttachResult> AttachAsync(long chatId, string? raw)
    {
        var code = Normalize(raw);
        var now = DateTime.UtcNow;
        if (Misses.TryGetValue(chatId, out var m) && now - m.Since < TimeSpan.FromHours(1) && m.Misses >= 5) return AttachResult.Throttled;

        var rc = code == null ? null : await _db.ReferralCodes.AsNoTracking().FirstOrDefaultAsync(c => c.Code == code && c.IsActive);
        if (rc == null)
        {
            Misses.AddOrUpdate(chatId, _ => (now, 1), (_, old) => now - old.Since < TimeSpan.FromHours(1) ? (old.Since, old.Misses + 1) : (now, 1));
            return AttachResult.Unknown;
        }

        var user = await _db.Users.FirstOrDefaultAsync(u => u.ChatId == chatId);
        if (user == null) return AttachResult.Unknown;
        if (!string.IsNullOrEmpty(user.ReferredByCode)) return AttachResult.AlreadyHasCode;

        user.ReferredByCode = rc.Code;
        user.ReferredAt = now;
        await _db.SaveChangesAsync();
        _logger.LogInformation("Referral: chat {ChatId} came in with code {Code}", chatId, rc.Code);
        return AttachResult.Attached;
    }

    // What the first /subscribe of this user costs. Anyone with a live code who has never had a paid month gets the code's discount;
    // every later payment (renewals included) is the plain price. The amount is rounded UP to two decimals because that is what
    // the bot shows, so the number on screen is the number the payment is checked against.
    public async Task<(decimal Amount, string? Code)> PriceForAsync(long chatId, decimal basePrice)
    {
        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.ChatId == chatId);
        if (user == null || string.IsNullOrEmpty(user.ReferredByCode)) return (basePrice, null);
        if (user.IsRN4L || user.IsRegisteredNurse || user.RNExpiresAt != null) return (basePrice, null);
        if (await _db.PendingPayments.AsNoTracking().AnyAsync(p => p.ChatId == chatId && p.IsConfirmed)) return (basePrice, null);

        var rc = await _db.ReferralCodes.AsNoTracking().FirstOrDefaultAsync(c => c.Code == user.ReferredByCode && c.IsActive);
        if (rc == null || rc.DiscountPercent <= 0) return (basePrice, null);

        var pct = Math.Clamp(rc.DiscountPercent, 1, 90);
        var amount = Math.Ceiling(basePrice * (100 - pct) / 100m * 100m) / 100m;
        amount = Math.Clamp(amount, 0.01m, basePrice);
        return amount < basePrice ? (amount, rc.Code) : (basePrice, null);
    }

    private sealed record Counted(long ChatId, DateTime At, decimal Amount, bool IsFirst);
    private sealed record Referred(long ChatId, string? Username, string? FirstName, DateTime At);

    // The one rule behind every number: a referrer is credited with a person's payments made AFTER the code reached that person
    // (the first code wins, and a payment made before it is nobody's commission). The first payment a person ever made is the
    // "first" one; every later one is a renewal.
    private async Task<(List<Referred> Users, List<Counted> Payments)> LoadAsync(string code)
    {
        var users = await _db.Users.AsNoTracking()
            .Where(u => u.ReferredByCode == code)
            .Select(u => new Referred(u.ChatId, u.Username, u.FirstName, u.ReferredAt ?? u.JoinedAt))
            .ToListAsync();

        var paid = await (from p in _db.PendingPayments.AsNoTracking()
                          join u in _db.Users.AsNoTracking() on p.ChatId equals u.ChatId
                          where p.IsConfirmed && u.ReferredByCode == code
                          select new { p.ChatId, At = p.ConfirmedAt ?? p.CreatedAt, p.AmountSol }).ToListAsync();

        var since = users.ToDictionary(u => u.ChatId, u => u.At);
        var firstEver = paid.GroupBy(p => p.ChatId).ToDictionary(g => g.Key, g => g.Min(p => p.At));
        var counted = paid.Where(p => p.At >= since[p.ChatId])
            .Select(p => new Counted(p.ChatId, p.At, p.AmountSol, p.At == firstEver[p.ChatId]))
            .OrderBy(p => p.At).ToList();
        return (users, counted);
    }

    public async Task<RefStats> StatsAsync(ReferralCode rc, bool withMoney)
    {
        var (users, payments) = await LoadAsync(rc.Code);

        // "subscribed" counts people (from their first counted payment), not payments.
        var firstPaid = payments.GroupBy(p => p.ChatId).ToDictionary(g => g.Key, g => g.Min(p => p.At));

        var today = DateTime.UtcNow.Date;
        var days = Enumerable.Range(0, 30).Select(i => today.AddDays(-29 + i)).Select(d => new RefDay(
            d.ToString("yyyy-MM-dd"),
            users.Count(u => u.At.Date == d),
            firstPaid.Values.Count(t => t.Date == d))).ToList();

        var thisMonth = new DateTime(today.Year, today.Month, 1);
        var months = Enumerable.Range(0, 6).Select(i => thisMonth.AddMonths(-5 + i)).Select(m => new RefDay(
            m.ToString("yyyy-MM"),
            users.Count(u => u.At.Year == m.Year && u.At.Month == m.Month),
            firstPaid.Values.Count(t => t.Year == m.Year && t.Month == m.Month))).ToList();

        return new RefStats(rc.Label, users.Count, firstPaid.Count, days, months, withMoney ? payments.Sum(p => p.Amount) : null);
    }

    public async Task<Ledger> LedgerAsync(ReferralCode rc)
    {
        var (users, payments) = await LoadAsync(rc.Code);
        var pct = rc.CommissionPercent;
        decimal Comm(decimal first, decimal renewal) => Math.Round(pct / 100m * (first + (rc.CommissionOnRenewals ? renewal : 0m)), 4);

        var firstSol = payments.Where(p => p.IsFirst).Sum(p => p.Amount);
        var renewalSol = payments.Where(p => !p.IsFirst).Sum(p => p.Amount);

        var months = payments.GroupBy(p => p.At.ToString("yyyy-MM")).OrderByDescending(g => g.Key).Select(g =>
        {
            var f = g.Where(p => p.IsFirst).Sum(p => p.Amount);
            var r = g.Where(p => !p.IsFirst).Sum(p => p.Amount);
            return new LedgerMonth(g.Key, g.Count(p => p.IsFirst), f, r, Comm(f, r));
        }).ToList();

        var byChat = users.ToDictionary(u => u.ChatId);
        var customers = payments.GroupBy(p => p.ChatId).Select(g => new LedgerCustomer(
            g.Key, byChat[g.Key].Username, byChat[g.Key].FirstName, byChat[g.Key].At,
            g.Min(p => p.At), g.Count(), g.Sum(p => p.Amount))).OrderByDescending(c => c.FirstPaidAt).ToList();

        var payouts = await _db.ReferralPayouts.AsNoTracking().Where(p => p.ReferralCodeId == rc.Id)
            .OrderByDescending(p => p.At).ToListAsync();
        var paid = payouts.Sum(p => p.AmountSol);
        var earned = Comm(firstSol, renewalSol);
        return new Ledger(pct, rc.CommissionOnRenewals, firstSol, renewalSol, earned, paid, earned - paid, months, customers,
            payouts.Select(p => new LedgerPayout(p.Id, p.At, p.AmountSol, p.Note)).ToList());
    }

    public static string NewCode() => Random(CodeAlphabet, 6);
    public static string NewToken() => Random(TokenAlphabet, 32);

    private static string Random(string alphabet, int length)
    {
        var chars = new char[length];
        for (var i = 0; i < length; i++) chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        return new string(chars);
    }
}
