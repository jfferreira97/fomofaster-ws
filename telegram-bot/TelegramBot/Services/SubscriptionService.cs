using System.Globalization;
using Microsoft.EntityFrameworkCore;
using TelegramBot.Data;
using TelegramBot.Models;

namespace TelegramBot.Services;

// What /subscribe (in the bot) and "Subscribe" (on the website) both ask for: the address a user pays to, and how much.
// One place decides the price, reuses an address that is still open and creates the wallet, so the two never drift apart.
// PaymentPollerService is unchanged: it confirms by reading the wallet's balance.
public sealed record PaymentQuote(
    string Status,            // "ok" | "active" (full access, nothing to buy yet) | "no_user"
    decimal Amount,
    string Address,
    DateTime ExpiresAt,
    bool Renewing,
    bool Reused,              // an address opened earlier and still valid
    bool Lifetime,
    DateTime? ActiveUntil)
{
    // The wallet apps' payment link (Solana Pay): opens Phantom/Solflare on a phone with the amount filled in.
    public string SolanaPayUrl => $"solana:{Address}?amount={Amount.ToString("0.########", CultureInfo.InvariantCulture)}&label=GROUPCHAT&message=Full%20access%2C%2030%20days";
}

public sealed class SubscriptionService
{
    private static readonly TimeSpan RenewWindow = TimeSpan.FromDays(3);

    private readonly AppDbContext _db;
    private readonly AppConfigService _config;
    private readonly ReferralService _referrals;

    public SubscriptionService(AppDbContext db, AppConfigService config, ReferralService referrals)
    {
        _db = db; _config = config; _referrals = referrals;
    }

    public async Task<PaymentQuote> GetOrCreateAsync(long chatId)
    {
        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.ChatId == chatId);
        if (user == null) return new PaymentQuote("no_user", 0, "", default, false, false, false, null);

        // Active already: a renewal is only offered in the last 3 days (the new 30 days are added on top of what is left).
        var hasAccess = user.IsRN4L || (user.IsRegisteredNurse && user.RNExpiresAt > DateTime.UtcNow);
        var renewing = false;
        if (hasAccess)
        {
            if (user.IsRN4L || user.RNExpiresAt!.Value - DateTime.UtcNow > RenewWindow)
                return new PaymentQuote("active", 0, "", default, false, false, user.IsRN4L, user.RNExpiresAt);
            renewing = true;
        }

        var existing = await _db.PendingPayments
            .Where(p => p.ChatId == chatId && !p.IsConfirmed && p.ExpiresAt > DateTime.UtcNow)
            .OrderByDescending(p => p.CreatedAt)
            .FirstOrDefaultAsync();
        if (existing != null)
        {
            var amount = existing.AmountSol > 0 ? existing.AmountSol : await _config.GetSubscriptionPriceSolAsync();
            return new PaymentQuote("ok", amount, existing.WalletPublicKey, existing.ExpiresAt, renewing, true, false, user.RNExpiresAt);
        }

        // A person who came in with a referral code and has never paid gets the code's price for this first payment; it is
        // simply the amount, nothing says why.
        var (price, referralCode) = await _referrals.PriceForAsync(chatId, await _config.GetSubscriptionPriceSolAsync());
        var keypair = GenerateSolanaKeypair();
        var pending = new PendingPayment
        {
            ChatId = chatId,
            WalletPublicKey = keypair.PublicKey,
            WalletPrivateKey = keypair.PrivateKey,
            AmountSol = price,
            ReferralCode = referralCode,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(1),
            IsConfirmed = false
        };
        _db.PendingPayments.Add(pending);
        await _db.SaveChangesAsync();
        return new PaymentQuote("ok", price, pending.WalletPublicKey, pending.ExpiresAt, renewing, false, false, user.RNExpiresAt);
    }

    // The address a user is paying to right now, without creating one (what the website shows when it opens).
    public async Task<PaymentQuote?> GetOpenAsync(long chatId)
    {
        var existing = await _db.PendingPayments.AsNoTracking()
            .Where(p => p.ChatId == chatId && !p.IsConfirmed && p.ExpiresAt > DateTime.UtcNow)
            .OrderByDescending(p => p.CreatedAt)
            .FirstOrDefaultAsync();
        if (existing == null) return null;
        var amount = existing.AmountSol > 0 ? existing.AmountSol : await _config.GetSubscriptionPriceSolAsync();
        return new PaymentQuote("ok", amount, existing.WalletPublicKey, existing.ExpiresAt, false, true, false, null);
    }

    public static (string PublicKey, string PrivateKey) GenerateSolanaKeypair()
    {
        var wallet = new Solnet.Wallet.Wallet(Solnet.Wallet.Bip39.WordCount.TwentyFour, Solnet.Wallet.Bip39.WordList.English);
        var account = wallet.Account;
        return (PublicKey: account.PublicKey.Key, PrivateKey: Convert.ToBase64String(account.PrivateKey.KeyBytes));
    }
}
