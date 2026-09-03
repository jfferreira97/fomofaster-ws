namespace TelegramBot.Models;

// A user-submitted "please add this trader" request from the manage page. Persisted (not just
// in-memory) so the per-user rate limit survives a backend restart and doubles as an audit log
// of who suggested what, when.
public class SuggestedTrader
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string Handle { get; set; } = string.Empty;
    public Platform Platform { get; set; }
    public DateTime CreatedAt { get; set; }
}
