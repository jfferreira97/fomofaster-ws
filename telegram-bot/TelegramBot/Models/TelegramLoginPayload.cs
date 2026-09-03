using System.Text.Json.Serialization;

namespace TelegramBot.Models;

// Shape of the object the Telegram Login Widget passes to its onauth callback.
// Field names are fixed by Telegram's widget protocol (snake_case), not ours.
public class TelegramLoginPayload
{
    public required long Id { get; set; }

    [JsonPropertyName("first_name")]
    public string? FirstName { get; set; }

    [JsonPropertyName("last_name")]
    public string? LastName { get; set; }

    public string? Username { get; set; }

    [JsonPropertyName("photo_url")]
    public string? PhotoUrl { get; set; }

    [JsonPropertyName("auth_date")]
    public required long AuthDate { get; set; }

    public required string Hash { get; set; }
}
