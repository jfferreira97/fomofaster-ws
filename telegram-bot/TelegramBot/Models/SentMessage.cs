namespace TelegramBot.Models;

public class SentMessage
{
    public int Id { get; set; }
    public int NotificationId { get; set; }
    public long ChatId { get; set; }
    public int MessageId { get; set; }
    public DateTime SentAt { get; set; }
    public bool IsManuallyEdited { get; set; }
    public bool IsSystemEdited { get; set; }
    public DateTime? EditedAt { get; set; }

    // Navigation property
    public Notification Notification { get; set; } = null!;
}
