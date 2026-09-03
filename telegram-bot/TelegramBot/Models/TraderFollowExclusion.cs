namespace TelegramBot.Models;

// A sticky "don't auto-follow this trader for this user" marker, created whenever a user
// explicitly unfollows a trader. Autofollow-driven follow paths (new-trader broadcast,
// any bulk/backfill follow) must check this and skip the pair; an explicit single-trader
// follow always removes the marker, since that's the user opting back in on purpose.
public class TraderFollowExclusion
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public int TraderId { get; set; }
    public DateTime ExcludedAt { get; set; }
}
