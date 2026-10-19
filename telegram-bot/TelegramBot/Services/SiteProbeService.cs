using System.Diagnostics;

namespace TelegramBot.Services;

// Once a minute, asks for the website twice: through the real public path (internet -> Cloudflare -> tunnel ->
// Caddy -> here) and straight at the local Caddy. The two together tell the Monitoring tab whether a problem is
// the tunnel/Cloudflare or this machine. Read-only GETs of the landing page; nothing is written anywhere.
public class SiteProbeService : BackgroundService
{
    private const string PublicUrl = "https://groupchat-bot.tech/";
    private const string LocalUrl = "http://127.0.0.1:8080/";
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(60);

    private readonly IHttpClientFactory _http;
    private readonly MonitorStore _store;
    private readonly ILogger<SiteProbeService> _logger;

    public SiteProbeService(IHttpClientFactory http, MonitorStore store, ILogger<SiteProbeService> logger)
    {
        _http = http;
        _store = store;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); } catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.WhenAll(Probe("public", PublicUrl, null, stoppingToken), Probe("local", LocalUrl, "groupchat-bot.tech", stoppingToken));
            try { await Task.Delay(Every, stoppingToken); } catch (OperationCanceledException) { break; }
        }
    }

    private async Task Probe(string name, string url, string? hostHeader, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var client = _http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd("groupchat-monitor/1.0");
            if (hostHeader != null) req.Headers.Host = hostHeader;
            using var res = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            _store.RecordProbe(name, new ProbeRecord(DateTime.UtcNow, (int)res.StatusCode, (int)sw.ElapsedMilliseconds, null));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _store.RecordProbe(name, new ProbeRecord(DateTime.UtcNow, 0, (int)sw.ElapsedMilliseconds, ex.GetType().Name));
            _logger.LogDebug(ex, "Site probe {Name} failed", name);
        }
    }
}
