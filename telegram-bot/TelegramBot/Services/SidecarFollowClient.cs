using System.Net.Http.Json;
using TelegramBot.Models;

namespace TelegramBot.Services;

// What a sidecar answered to "follow this trader" (see fomo-ws-sidecar/src/control.ts and pump-ws-sidecar/src/control.ts).
// Code: followed | already | not_found | invalid | not_logged_in | rate_limited | busy | error, and here also
// sidecar_down (nothing answered) and timeout. Handle is the platform's own spelling of it; Id its user id (FOMO) or wallet (pump).
public record SidecarFollowResult(bool Ok, string Code, string? Handle, string? Id, string? Message);

// One account a lookup found: FOMO's own user search (fuzzy, several), or pump.fun's exact handle check (one or none).
public record SidecarLookupUser(string Handle, string? Name, string? Pic, double? Followers, bool Verified);
public record SidecarLookupResult(bool Ok, string Code, List<SidecarLookupUser>? Users, string? Message);

// Asks the sidecar logged into that platform to follow a trader, so their trades reach us from now on. The sidecars listen on
// 127.0.0.1 only: FOMO account 2 on :8141, pump.fun on :8142 (Sidecars:FomoControlUrl / Sidecars:PumpControlUrl override).
public class SidecarFollowClient
{
    public const string HttpClientName = "sidecar-follow";

    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<SidecarFollowClient> _logger;

    public SidecarFollowClient(IHttpClientFactory httpFactory, IConfiguration config, ILogger<SidecarFollowClient> logger)
    {
        _httpFactory = httpFactory;
        _config = config;
        _logger = logger;
    }

    private string BaseUrl(Platform platform) => platform == Platform.Pump
        ? _config["Sidecars:PumpControlUrl"] ?? "http://127.0.0.1:8142"
        : _config["Sidecars:FomoControlUrl"] ?? "http://127.0.0.1:8141";

    // Answers kept a minute: everyone typing the same start of a handle asks the platform once.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime At, SidecarLookupResult Result)> LookupCache = new();
    private static readonly TimeSpan LookupTtl = TimeSpan.FromSeconds(60);

    // Which accounts match what the user typed, asked through the logged-in sidecar (read-only, nothing is followed).
    // A failed lookup comes back Ok=false and is not cached.
    public async Task<SidecarLookupResult> LookupAsync(Platform platform, string q, CancellationToken ct)
    {
        var key = $"{platform}|{q.ToLowerInvariant()}";
        if (LookupCache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < LookupTtl)
            return hit.Result;
        try
        {
            var http = _httpFactory.CreateClient(HttpClientName);
            http.Timeout = TimeSpan.FromSeconds(8);
            var body = await http.GetFromJsonAsync<SidecarLookupResult>($"{BaseUrl(platform)}/lookup?q={Uri.EscapeDataString(q)}", ct);
            var result = body ?? new SidecarLookupResult(false, "error", null, null);
            if (result.Ok)
            {
                if (LookupCache.Count > 5_000)
                    foreach (var old in LookupCache.Where(e => DateTime.UtcNow - e.Value.At >= LookupTtl).Select(e => e.Key).ToList())
                        LookupCache.TryRemove(old, out _);
                LookupCache[key] = (DateTime.UtcNow, result);
            }
            else
                _logger.LogInformation("Sidecar lookup {Platform} {Q}: {Code} {Message}", platform, q, result.Code, result.Message);
            return result;
        }
        catch (HttpRequestException)
        {
            return new SidecarLookupResult(false, "sidecar_down", null, null);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new SidecarLookupResult(false, "timeout", null, null);
        }
    }

    public async Task<SidecarFollowResult> FollowAsync(Platform platform, string handle, CancellationToken ct)
    {
        try
        {
            var http = _httpFactory.CreateClient(HttpClientName);
            http.Timeout = TimeSpan.FromSeconds(45);   // follows queue one at a time in the sidecar, 1.5 s apart
            using var res = await http.PostAsJsonAsync($"{BaseUrl(platform)}/follow", new { handle }, ct);
            var body = await res.Content.ReadFromJsonAsync<SidecarFollowResult>(cancellationToken: ct);
            var result = body ?? new SidecarFollowResult(false, "error", null, null, $"HTTP {(int)res.StatusCode}");
            _logger.LogInformation("Sidecar follow {Platform} {Handle}: {Code} {Message}", platform, handle, result.Code, result.Message);
            return result;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("Sidecar follow {Platform} {Handle}: sidecar not answering ({Error})", platform, handle, ex.Message);
            return new SidecarFollowResult(false, "sidecar_down", null, null, ex.Message);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Sidecar follow {Platform} {Handle}: timed out", platform, handle);
            return new SidecarFollowResult(false, "timeout", null, null, null);
        }
    }
}
