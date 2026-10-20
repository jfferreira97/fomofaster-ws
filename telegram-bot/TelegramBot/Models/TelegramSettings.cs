namespace TelegramBot.Models;

public class TelegramSettings
{
    public string BotToken { get; set; } = string.Empty;
    public string AdminBotToken { get; set; } = string.Empty;

    // Old bot's token, kept alive in parallel during the relaunch migration so existing
    // users aren't cut off mid-transition. BotToken above is the new/current bot going
    // forward. Empty = single-bot mode, same as before this feature existed.
    public string DeprecatedBotToken { get; set; } = string.Empty;
    public int OwnerUserId { get; set; } = 1;

    // The project's X (Twitter) account, shown in the welcome message and /help. Kept out of the repo on purpose:
    // set it as a user secret (Telegram:XUrl) or the environment variable Telegram__XUrl. Empty = the line is left out.
    public string XUrl { get; set; } = string.Empty;
}
