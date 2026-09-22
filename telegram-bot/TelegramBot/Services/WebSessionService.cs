using System.Security.Cryptography;
using System.Text;

namespace TelegramBot.Services;

// Hand-rolled, stateless tokens for the manage page: "purpose.chatId.expiryUnix.signature",
// HMAC-signed with a secret generated once via AppConfigService. No server-side session
// table needed — validity is just "signature checks out and hasn't expired".
// "s1" is the session cookie, "l1" the /manage link token; the purpose tag keeps one from
// being used as the other.
public class WebSessionService
{
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(30);
    private static readonly TimeSpan LoginLinkLifetime = TimeSpan.FromDays(30);
    private const string SessionPurpose = "s1";
    private const string LoginLinkPurpose = "l1";

    public const string CookieName = "gc_session";
    public const string LegacyCookieName = "fomo_session";

    private readonly AppConfigService _appConfig;

    public WebSessionService(AppConfigService appConfig)
    {
        _appConfig = appConfig;
    }

    public Task<string> CreateTokenAsync(long chatId) =>
        CreateAsync(SessionPurpose, chatId, SessionLifetime);

    public Task<long?> ValidateTokenAsync(string? token) =>
        ValidateAsync(SessionPurpose, token);

    public Task<string> CreateLoginLinkTokenAsync(long chatId) =>
        CreateAsync(LoginLinkPurpose, chatId, LoginLinkLifetime);

    public Task<long?> ValidateLoginLinkTokenAsync(string? token) =>
        ValidateAsync(LoginLinkPurpose, token);

    private async Task<string> CreateAsync(string purpose, long chatId, TimeSpan lifetime)
    {
        var expiresAtUnix = DateTimeOffset.UtcNow.Add(lifetime).ToUnixTimeSeconds();
        var payload = $"{purpose}.{chatId}.{expiresAtUnix}";
        var secret = await _appConfig.GetOrCreateWebSessionSecretAsync();
        return $"{payload}.{Sign(payload, secret)}";
    }

    private async Task<long?> ValidateAsync(string purpose, string? token)
    {
        if (string.IsNullOrEmpty(token))
            return null;

        var parts = token.Split('.');
        if (parts.Length != 4)
            return null;

        if (!string.Equals(parts[0], purpose, StringComparison.Ordinal))
            return null;

        if (!long.TryParse(parts[1], out var chatId) || !long.TryParse(parts[2], out var expiresAtUnix))
            return null;

        var payload = $"{parts[0]}.{parts[1]}.{parts[2]}";
        var secret = await _appConfig.GetOrCreateWebSessionSecretAsync();
        var expectedSig = Sign(payload, secret);

        if (!SignaturesMatch(parts[3], expectedSig))
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
