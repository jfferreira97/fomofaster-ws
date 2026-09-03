using System.Security.Cryptography;
using System.Text;

namespace TelegramBot.Services;

// Hand-rolled, stateless session token for the manage page: "chatId.expiryUnix.signature",
// HMAC-signed with a secret generated once via AppConfigService. No server-side session
// table needed — validity is just "signature checks out and hasn't expired".
public class WebSessionService
{
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(30);
    public const string CookieName = "fomo_session";

    private readonly AppConfigService _appConfig;

    public WebSessionService(AppConfigService appConfig)
    {
        _appConfig = appConfig;
    }

    public async Task<string> CreateTokenAsync(long chatId)
    {
        var expiresAtUnix = DateTimeOffset.UtcNow.Add(SessionLifetime).ToUnixTimeSeconds();
        var payload = $"{chatId}.{expiresAtUnix}";
        var secret = await _appConfig.GetOrCreateWebSessionSecretAsync();
        return $"{payload}.{Sign(payload, secret)}";
    }

    public async Task<long?> ValidateTokenAsync(string? token)
    {
        if (string.IsNullOrEmpty(token))
            return null;

        var parts = token.Split('.');
        if (parts.Length != 3)
            return null;

        if (!long.TryParse(parts[0], out var chatId) || !long.TryParse(parts[1], out var expiresAtUnix))
            return null;

        var payload = $"{parts[0]}.{parts[1]}";
        var secret = await _appConfig.GetOrCreateWebSessionSecretAsync();
        var expectedSig = Sign(payload, secret);

        if (!SignaturesMatch(parts[2], expectedSig))
            return null;

        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expiresAtUnix)
            return null;

        return chatId;
    }

    private static string Sign(string payload, byte[] secret)
    {
        using var hmac = new HMACSHA256(secret);
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToBase64String(hash).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private static bool SignaturesMatch(string provided, string expected)
    {
        var providedBytes = Encoding.UTF8.GetBytes(provided);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        if (providedBytes.Length != expectedBytes.Length)
            return false;
        return CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
    }
}
