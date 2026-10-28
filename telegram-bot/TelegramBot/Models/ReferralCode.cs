namespace TelegramBot.Models;

// A code someone shares so the people it brings can be counted. Nothing in the bot, the site or the terms mentions it:
// it only travels as a link parameter (t.me/...?start=r_<code>, site ?r=<code>) or a hidden /ref command.
public class ReferralCode
{
    public int Id { get; set; }

    // Lower-case letters and digits, 3 to 16 characters. Matched case-insensitively.
    public string Code { get; set; } = string.Empty;

    // Whose code it is, for the dashboard only.
    public string Label { get; set; } = string.Empty;

    // Off the first month's price for a user who has never paid. The amount is baked into the PendingPayment when the
    // address is created, so changing this later does not touch addresses that are already open.
    public int DiscountPercent { get; set; } = 25;

    // The secret in the referrer's private stats link (groupchat-bot.tech/r/<token>). Counts only.
    public string StatsToken { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }

    // What the owner pays this referrer out of what their people pay. Owner-only bookkeeping: the referrer never sees it and
    // the percent is whatever the owner agreed with them (0 = not set).
    public decimal CommissionPercent { get; set; }

    // Whether renewals (every payment after a person's first) count toward the commission, or only the first payment.
    public bool CommissionOnRenewals { get; set; } = true;
}

// One payout the owner made to a referrer (paid by hand, off-system). The dashboard only records it so "owed" is earned minus paid.
public class ReferralPayout
{
    public int Id { get; set; }
    public int ReferralCodeId { get; set; }
    public DateTime At { get; set; }
    public decimal AmountSol { get; set; }
    public string Note { get; set; } = string.Empty;
}
