namespace TelegramBot.Models;

public record TraderSeedEntry(string Handle, bool IsVerified = false);

public record BulkRegisterResult(int Added, int Updated);
