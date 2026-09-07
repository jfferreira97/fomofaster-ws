namespace TelegramBot.Models;

public class User
{
    public int Id { get; set; }
    public long ChatId { get; set; }
    public string? Username { get; set; }
    public string? FirstName { get; set; }
    public DateTime JoinedAt { get; set; }
    public bool IsActive { get; set; }

    // Touched on every inbound message/button tap (see TelegramBotPollingService.HandleUpdateAsync).
    // IsActive only tells you Telegram-confirmed-unreachable (blocked/deleted); this is the only
    // signal for "still reachable but hasn't touched the bot in months."
    public DateTime? LastActiveAt { get; set; }

    // Auto-follow: whether newly-discovered traders on each platform get auto-followed.
    // AutoFollowFomoTraders was formerly the single global AutoFollowNewTraders column —
    // renamed (not a new column) so existing users' preference carries over as their FOMO
    // setting. AutoFollowPumpTraders is genuinely new and defaults off (see migration).
    public bool AutoFollowFomoTraders { get; set; }
    public bool AutoFollowPumpTraders { get; set; }

    // Notification-type preferences. FOMO's Buy and Sell are deliberately one combined
    // toggle, not two — they're the same "trade activity" concern to a subscriber.
    public bool NotifyFomoBuySell { get; set; } = true;
    public bool NotifyFomoThesis { get; set; } = true;

    // Covers Callout, Repost, AND Reply — bundled under one toggle by design, since to a
    // subscriber they're all just "pump activity from people you follow."
    public bool NotifyPumpCallouts { get; set; } = true;
    // Only meaningful when NotifyPumpCallouts is on: restrict to IsPumpVerified traders.
    public bool PumpVerifiedOnly { get; set; } = false;

    public bool NotifyTrending { get; set; } = true;

    public int RepeatWindowMinutes { get; set; } = 0;
    public bool IsRegisteredNurse { get; set; }
    public bool IsRN4L { get; set; }
    public DateTime? RNExpiresAt { get; set; }

    // Set the moment this chat sends anything to the NEW bot (the GROUPCHAT relaunch,
    // migrating off the old bot token). Same ChatId either way — Telegram private-chat
    // ids are per-user, not per-bot — so this is purely a routing flag: false = still
    // only reachable via the deprecated bot, true = message via the new bot instead.
    public bool IsOnNewBot { get; set; }

    // One-shot free trial granted on a user's first /start on the NEW bot only (see
    // TelegramBotPollingService), regardless of whether they're a brand-new signup or an
    // old-bot user migrating over. Null = never granted. Non-null and in the future = full
    // (non-obfuscated) alerts, same as an active subscription — see TelegramService's
    // HasFullAccess. Non-null and in the past = trial used up; never re-granted since the
    // grant check requires this to still be null.
    public DateTime? TrialExpiresAt { get; set; }

    // Flips true once PaymentPollerService has sent the "your trial ended" notice for this
    // expiry, so that one-time message doesn't re-fire on every subsequent poll tick.
    public bool TrialExpiryNotified { get; set; }
}
