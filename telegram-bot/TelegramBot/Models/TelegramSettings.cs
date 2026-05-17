namespace TelegramBot.Models;

public class TelegramSettings
{
    public string BotToken { get; set; } = string.Empty;
    public string AdminBotToken { get; set; } = string.Empty;
    public int OwnerUserId { get; set; } = 1;
}
