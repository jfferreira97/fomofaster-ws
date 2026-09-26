namespace TelegramBot.Models;

// A live subscription to a trader category: the user gets alerts from whoever is in that
// category at the time of the alert, so traders moved in or out (by the categorizer or by
// hand) start or stop reaching them without anyone's follow list changing. A
// TraderFollowExclusion for a trader mutes them even when their category is followed.
public class UserCategoryFollow
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string Category { get; set; } = string.Empty;
    public DateTime FollowedAt { get; set; }
}
