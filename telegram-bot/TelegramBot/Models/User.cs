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
}
