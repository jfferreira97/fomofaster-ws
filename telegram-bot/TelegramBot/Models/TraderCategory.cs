namespace TelegramBot.Models;

// The fixed set of trader categories. Ids are what's stored (Trader.Category,
// TraderRating.AutoCategory, UserCategoryFollow.Category); labels and blurbs are what users see.
// TraderCategorizerService assigns them from each trader's follower outcomes, an admin can
// override one by hand from the dashboard, and /manage users follow whole categories.
//
// A rated trader's category is the take-profit window that has paid best for someone acting on
// their calls, measured from the moment of the call. Market cap is a filter on /manage, not a
// category.
public record TraderCategoryInfo(string Id, string Label, string Emoji, string Description);

public static class TraderCategories
{
    public const string Scalper = "scalper";   // best out within 15 minutes
    public const string Hourly = "hourly";     // best out within the hour
    public const string Flipper = "flipper";   // best out within 4 hours
    public const string Runner = "runner";     // best held through the day
    public const string Holder = "holder";     // holds their own buys for days, and it pays
    public const string Noise = "noise";
    public const string Unrated = "unrated";

    // Display order: longest window first, then the ones most users shouldn't follow.
    public static readonly IReadOnlyList<TraderCategoryInfo> All = new[]
    {
        new TraderCategoryInfo(Runner, "Day holds", "📈", "Their calls keep climbing through the day: hold for 3x."),
        new TraderCategoryInfo(Holder, "Holders", "💎", "They hold for days and it pays off. Slow money."),
        new TraderCategoryInfo(Flipper, "4h flips", "🔁", "Best taken within 4 hours of the call: aim for 2x."),
        new TraderCategoryInfo(Hourly, "1h flips", "⏱", "Best taken within an hour of the call: +50%, out."),
        new TraderCategoryInfo(Scalper, "15m flips", "⚡", "Best taken within 15 minutes of the call: +30%, out."),
        new TraderCategoryInfo(Noise, "−EV", "🔇", "Acting on their calls lost money over the last 30 days."),
        new TraderCategoryInfo(Unrated, "Unrated", "🆕", "Not enough recent calls to score yet."),
    };

    private static readonly HashSet<string> Ids = All.Select(c => c.Id).ToHashSet();

    public static bool IsValid(string? id) => id != null && Ids.Contains(id);

    public static string Effective(string? id) => IsValid(id) ? id! : Unrated;

    // −EV and Unrated can be filtered on but not followed as a group.
    public static bool IsFollowable(string? id) => IsValid(id) && id != Noise && id != Unrated;
}
