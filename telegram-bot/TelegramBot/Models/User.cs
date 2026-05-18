namespace TelegramBot.Models;

public class User
{
    public int Id { get; set; }
    public long ChatId { get; set; }
    public string? Username { get; set; }
    public string? FirstName { get; set; }
    public DateTime JoinedAt { get; set; }
    public bool IsActive { get; set; }
    public bool AutoFollowNewTraders { get; set; }
    public bool IsRegisteredNurse { get; set; }
    public bool IsRN4L { get; set; }
    public DateTime? RNExpiresAt { get; set; }
}
