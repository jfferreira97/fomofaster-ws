using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using TelegramBot.Data;
using TelegramBot.Models;

namespace TelegramBot.Services;

// The web twin of the Telegram alert stream, served at /feed. TelegramService publishes every
// alert here the moment its recipient list is settled, before the Telegram fan-out starts, so the
// page never waits on the Telegram API or the send rate limiter.
//
// Parity with Telegram: a logged-in viewer (the /manage session cookie) only receives alerts they
// would have been sent (their follows, chain and type settings), and sees the full version only
// with full access; everyone else gets the redacted version, same as the bot's free tier. The
// anyone without full access (logged out, or logged in on the free plan) gets the stream in real time, hidden exactly the way the
// Telegram bot hides it for free users: coin name -> "coin", contract address removed, the amount of a buy hidden. The server builds
// a separate payload for them, so nothing hidden is ever sent to the browser. The trader name stays, as on Telegram.
public class FeedService
{
    private const int RingSize = 400;
    private const int Backlog = 80;
    private const int MaxAnonymousClients = 400;
    private const int MaxClientsPerUser = 4;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record Entry(long Id, long Ts, IReadOnlySet<long> Recipients, string Full, string Rated, string Free);

    // Trader score percentile (the pill on /manage), shown only to paying members. Refreshed in
    // the background so the publish path never waits on the DB.
    private sealed record Score(int Rank, bool Weak);
    private volatile Dictionary<string, Score> _scores = new();
    private DateTime _scoresAt = DateTime.MinValue;
    private int _scoresLoading;
    private const int ConfidentCalls = 20; // same threshold manage.html fades scores under
    // Contract -> coin picture, learned from every alert that carries one.
    private readonly ConcurrentDictionary<string, string> _tokenImages = new();

    // Pictures for the anonymous preview go through us: the real URLs give things away (FOMO coin image URLs contain the
    // contract address, pump profile image URLs contain the trader's user id), so the browser only ever sees an opaque id.
    private byte[] _imgSecret = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
    private readonly ConcurrentDictionary<string, string> _imgMap = new();
    private readonly ConcurrentDictionary<string, (byte[] Data, string Type, DateTime At)> _imgCache = new();
    private readonly SemaphoreSlim _imgGate = new(6);
    private const int MaxImageBytes = 1_500_000;
    private static readonly HttpClient ImgHttp = new(new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(5) }) { Timeout = TimeSpan.FromSeconds(8) };
    private void RememberToken(string? ca, string? img)
    {
        if (string.IsNullOrEmpty(ca) || img is null) return;
        if (_tokenImages.Count > 3000) _tokenImages.Clear();
        _tokenImages[ca] = img;
    }

    private static string ScoreKey(Platform p, string handle) => $"{(int)p}:{handle.ToLowerInvariant()}";

    private sealed class Client
    {
        public long? ChatId;
        public readonly DateTime Since = DateTime.UtcNow;
        public readonly SemaphoreSlim Signal = new(0);
    }

    private readonly object _lock = new();
    private readonly List<Entry> _ring = new();
    private long _nextId;
    private readonly ConcurrentDictionary<Guid, Client> _clients = new();

    private readonly ActiveUserCache _activeUsers;
    private readonly IServiceProvider _services;
    private readonly ILogger<FeedService> _logger;
    private readonly TraderIntelService _intel;
    private readonly AppConfigService _appConfig;

    public FeedService(ActiveUserCache activeUsers, IServiceProvider services, ILogger<FeedService> logger, TraderIntelService intel, AppConfigService appConfig)
    {
        _activeUsers = activeUsers;
        _services = services;
        _logger = logger;
        _intel = intel;
        _appConfig = appConfig;
    }

    // Same rule as TelegramService.HasFullAccess.
    public static bool HasFullAccess(User u) =>
        u.IsRN4L
        || (u.IsRegisteredNurse && u.RNExpiresAt > DateTime.UtcNow)
        || (u.TrialExpiresAt.HasValue && u.TrialExpiresAt.Value > DateTime.UtcNow);

    public bool IsFull(long chatId) =>
        _activeUsers.GetActiveUsers().FirstOrDefault(u => u.ChatId == chatId) is { } u && HasFullAccess(u);

    // Same rule as ManageController.HasRatings: scores are for paying members only, not the trial.
    private bool IsRated(long chatId) =>
        _activeUsers.GetActiveUsers().FirstOrDefault(u => u.ChatId == chatId) is { } u && (u.IsRN4L || u.IsRegisteredNurse);

    private void RefreshScoresIfStale()
    {
        if (DateTime.UtcNow - _scoresAt < TimeSpan.FromMinutes(5)) return;
        if (Interlocked.Exchange(ref _scoresLoading, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try { await LoadScoresAsync(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Feed score refresh failed"); }
            finally { _scoresAt = DateTime.UtcNow; Interlocked.Exchange(ref _scoresLoading, 0); }
        });
    }

    public async Task LoadScoresAsync()
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rows = await db.TraderRatings.AsNoTracking()
            .Select(r => new { r.Trader.Handle, r.Trader.Platform, r.Simulated, r.StatsJson })
            .ToListAsync();
        var map = new Dictionary<string, Score>();
        foreach (var r in rows)
        {
            var rank = TraderCategorizerService.ParseStats(r.StatsJson).ScoreRank;
            if (rank is int rk) map[ScoreKey(r.Platform, r.Handle)] = new Score(rk, r.Simulated < ConfidentCalls);
        }
        _scores = map;
    }

    public void Publish(Notification n, IEnumerable<long> recipientChatIds, double? usdAmount, string? tokenImage = null, string? traderImage = null)
    {
        try
        {
            RefreshScoresIfStale();
            RememberToken(n.ContractAddress, SafeImage(tokenImage));
            Entry entry;
            lock (_lock)
            {
                var id = ++_nextId;
                entry = Build(id, n, recipientChatIds.ToHashSet(), usdAmount, tokenImage, traderImage);
                Add(entry);
            }
            foreach (var c in _clients.Values) c.Signal.Release();
        }
        catch (Exception ex)
        {
            // The feed must never be able to break the Telegram path.
            _logger.LogError(ex, "Feed publish failed");
        }
    }

    // Warm the ring after a restart so /feed has history immediately. Notifications only keep who
    // they were sent to in SentMessages, so recipients are rebuilt from there.
    public async Task SeedAsync()
    {
        try { _imgSecret = System.Security.Cryptography.HMACSHA256.HashData(await _appConfig.GetOrCreateWebSessionSecretAsync(), System.Text.Encoding.UTF8.GetBytes("feed-img")); }
        catch (Exception ex) { _logger.LogWarning(ex, "Feed image key load failed; using a per-process key"); }
        try { await LoadScoresAsync(); _scoresAt = DateTime.UtcNow; }
        catch (Exception ex) { _logger.LogWarning(ex, "Feed score load failed"); }

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var notes = await db.Notifications.AsNoTracking()
            .OrderByDescending(n => n.Id).Take(RingSize).ToListAsync();
        if (notes.Count == 0) return;

        var ids = notes.Select(n => n.Id).ToList();
        var sent = await db.SentMessages.AsNoTracking()
            .Where(s => ids.Contains(s.NotificationId))
            .Select(s => new { s.NotificationId, s.ChatId })
            .ToListAsync();
        var byNote = sent.GroupBy(s => s.NotificationId).ToDictionary(g => g.Key, g => g.Select(s => s.ChatId).ToHashSet());

        // Pictures for history: FOMO from the stored WsEvent, Pump from the stored PumpEvent.
        var wsIds = notes.Where(n => n.FK_WsEvent_WsId != null).Select(n => n.FK_WsEvent_WsId!).Distinct().ToList();
        var wsRows = await db.WsEvents.AsNoTracking().Where(w => wsIds.Contains(w.WsId)).Select(w => new { w.WsId, w.RawJson }).ToListAsync();
        var wsImg = wsRows.GroupBy(w => w.WsId).ToDictionary(g => g.Key,
            g => (Tok: RawString(g.First().RawJson, "tokenImageUrl"), Face: RawString(g.First().RawJson, "profilePictureLink")));
        var since = notes.Min(n => n.SentAt).AddMinutes(-2);
        var pumpRows = await db.PumpEvents.AsNoTracking().Where(e => e.ReceivedAt >= since)
            .Select(e => new { e.ActorHandle, e.CoinMint, e.RawJson }).ToListAsync();
        var pumpImg = pumpRows.GroupBy(e => (e.ActorHandle ?? "").ToLowerInvariant() + "|" + e.CoinMint).ToDictionary(g => g.Key,
            g => (Tok: RawString(g.Last().RawJson, "coinImage"), Face: RawString(g.Last().RawJson, "author", "profileImage")));

        lock (_lock)
        {
            foreach (var n in notes.OrderBy(n => n.Id))
            {
                (string? Tok, string? Face) img = default;
                if (n.Platform == Platform.Pump)
                    pumpImg.TryGetValue((n.Trader ?? "").ToLowerInvariant() + "|" + n.ContractAddress, out img);
                else if (n.FK_WsEvent_WsId != null)
                    wsImg.TryGetValue(n.FK_WsEvent_WsId, out img);
                RememberToken(n.ContractAddress, SafeImage(img.Tok));
                Add(Build(++_nextId, n, byNote.GetValueOrDefault(n.Id) ?? new HashSet<long>(), SizeFromMessage(n), img.Tok, img.Face));
            }
        }
        _logger.LogInformation("Feed seeded with {Count} alert(s)", notes.Count);
    }

    // Who is on the feed right now (logged-in people only), and since when.
    public List<(long ChatId, DateTime Since)> LiveUsers() =>
        _clients.Values.Where(c => c.ChatId is not null).Select(c => (c.ChatId!.Value, c.Since)).ToList();

    // Streams to the response until the client leaves. chatId null = anonymous demo.
    public async Task StreamAsync(HttpResponse res, long? chatId, long lastEventId, CancellationToken ct)
    {
        if (chatId is null ? _clients.Values.Count(c => c.ChatId is null) >= MaxAnonymousClients
                           : _clients.Values.Count(c => c.ChatId == chatId) >= MaxClientsPerUser)
        {
            res.StatusCode = 429;
            return;
        }

        res.Headers.ContentType = "text/event-stream";
        res.Headers.CacheControl = "no-store";
        res.Headers["X-Accel-Buffering"] = "no";
        res.Headers.ContentEncoding = "identity"; // keeps Caddy's encode from buffering the stream

        var key = Guid.NewGuid();
        var client = new Client { ChatId = chatId };
        _clients[key] = client;
        try
        {
            var full = chatId is { } c && IsFull(c);
            var tier = chatId is null ? "demo" : full ? "full" : "free";
            await Write(res, null, "hello", JsonSerializer.Serialize(new { tier, delaySec = 0 }, Json), ct);

            var cursor = lastEventId;
            var first = true;
            while (!ct.IsCancellationRequested)
            {
                List<Entry> batch;
                lock (_lock)
                {
                    IEnumerable<Entry> q = _ring.Where(e => e.Id > cursor);
                    if (chatId is { } me) q = q.Where(e => e.Recipients.Contains(me));
                    batch = q.ToList();
                    if (first && lastEventId == 0 && batch.Count > Backlog) batch = batch.Skip(batch.Count - Backlog).ToList();
                    // Advance past everything seen, including entries that were not meant for this client.
                    cursor = Math.Max(cursor, _ring.Count > 0 ? _ring[^1].Id : cursor);
                }

                var nowFull = chatId is { } cid && IsFull(cid);
                var nowRated = chatId is { } rid && IsRated(rid);
                foreach (var e in batch)
                    await Write(res, e.Id, first && lastEventId == 0 ? "backlog" : "alert",
                        nowRated ? e.Rated : nowFull ? e.Full : e.Free, ct);   // anyone without full access gets the same as the Telegram free tier
                first = false;

                // Every client is woken by Publish; the timeout only drives the keepalive.
                await client.Signal.WaitAsync(TimeSpan.FromSeconds(15), ct);
                if (client.Signal.CurrentCount == 0)
                    await res.WriteAsync(": hb\n\n", ct); // keepalive through proxies
                while (client.Signal.CurrentCount > 0) await client.Signal.WaitAsync(0, ct);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _clients.TryRemove(key, out _);
        }
    }

    private static async Task Write(HttpResponse res, long? id, string ev, string data, CancellationToken ct)
    {
        var s = (id is null ? "" : $"id: {id}\n") + $"event: {ev}\ndata: {data}\n\n";
        await res.WriteAsync(s, ct);
        await res.Body.FlushAsync(ct);
    }

    private void Add(Entry e)
    {
        _ring.Add(e);
        if (_ring.Count > RingSize) _ring.RemoveRange(0, _ring.Count - RingSize);
    }

    // History does not keep the dollar amount, but the message text has it.
    private static readonly Regex TradeSize = new(@"(?:bought|sold|withdrew|traded) \$([\d,]+(?:\.\d+)?)", RegexOptions.Compiled);
    private static readonly Regex PositionSize = new(@"position by [^\n]*?: \$([\d,]+(?:\.\d+)?)", RegexOptions.Compiled);
    private static double? SizeFromMessage(Notification n)
    {
        var rx = n.Type is NotificationType.Buy or NotificationType.Sell ? TradeSize
               : n.Type is NotificationType.Callout or NotificationType.Thesis ? PositionSize : null;
        var m = rx?.Match(n.Message ?? "");
        return m is { Success: true } && double.TryParse(m.Groups[1].Value.Replace(",", ""), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static string? RawString(string? raw, params string[] path)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var el = doc.RootElement;
            foreach (var key in path)
                if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(key, out el)) return null;
            return el.ValueKind == JsonValueKind.String ? el.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    // Opaque, stable id for an upstream picture; the id -> URL map lives only here.
    private string? ProxyUrl(string? url)
    {
        if (url is null) return null;
        var hash = System.Security.Cryptography.HMACSHA256.HashData(_imgSecret, System.Text.Encoding.UTF8.GetBytes(url));
        var id = Convert.ToBase64String(hash, 0, 12).Replace('+', '-').Replace('/', '_');
        if (_imgMap.Count > 6000) _imgMap.Clear();
        _imgMap[id] = url;
        return "/api/feed/img/" + id;
    }

    private static bool ImageHostOk(Uri u) =>
        u.Scheme == Uri.UriSchemeHttps
        && (ImageHosts.Contains(u.Host, StringComparer.OrdinalIgnoreCase) || u.Host.EndsWith(".ipfs.dweb.link", StringComparison.OrdinalIgnoreCase));

    // Serves a picture for an opaque id. The upstream URL comes only from our own map (never from the request) and must
    // be on the allow-list, redirects are followed by hand and re-checked, and only small real images are returned.
    public async Task<(byte[] Data, string Type)?> GetImageAsync(string id)
    {
        if (!_imgMap.TryGetValue(id, out var url)) return null;
        if (_imgCache.TryGetValue(id, out var hit) && DateTime.UtcNow - hit.At < (hit.Data.Length == 0 ? TimeSpan.FromMinutes(2) : TimeSpan.FromHours(6)))
            return hit.Data.Length == 0 ? null : (hit.Data, hit.Type);

        if (!await _imgGate.WaitAsync(TimeSpan.FromSeconds(3))) return null;
        try
        {
            (byte[] Data, string Type)? got = null;
            try { got = await FetchImageAsync(url); }
            catch (Exception ex) { _logger.LogDebug(ex, "Feed picture fetch failed"); }
            if (_imgCache.Count > 150) foreach (var k in _imgCache.OrderBy(x => x.Value.At).Take(50).Select(x => x.Key).ToList()) _imgCache.TryRemove(k, out _);
            _imgCache[id] = got is { } g ? (g.Data, g.Type, DateTime.UtcNow) : (Array.Empty<byte>(), "", DateTime.UtcNow);
            return got;
        }
        finally { _imgGate.Release(); }
    }

    private static async Task<(byte[] Data, string Type)?> FetchImageAsync(string url)
    {
        var u = new Uri(url);
        for (var hop = 0; hop < 4; hop++)
        {
            if (!ImageHostOk(u)) return null;
            using var req = new HttpRequestMessage(HttpMethod.Get, u);
            req.Headers.UserAgent.ParseAdd("Mozilla/5.0");
            using var resp = await ImgHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            if ((int)resp.StatusCode is >= 300 and < 400 && resp.Headers.Location is { } loc) { u = loc.IsAbsoluteUri ? loc : new Uri(u, loc); continue; }
            if (!resp.IsSuccessStatusCode) return null;
            var type = resp.Content.Headers.ContentType?.MediaType ?? "";
            if (type is not ("image/png" or "image/jpeg" or "image/webp" or "image/gif")) return null;   // never svg or html
            if (resp.Content.Headers.ContentLength is > MaxImageBytes) return null;
            await using var s = await resp.Content.ReadAsStreamAsync();
            var buf = new MemoryStream(); var chunk = new byte[16384]; int n;
            while ((n = await s.ReadAsync(chunk)) > 0) { buf.Write(chunk, 0, n); if (buf.Length > MaxImageBytes) return null; }
            return (buf.ToArray(), type);
        }
        return null;
    }

    // Images are hot-linked by the browser, so only https from the hosts the two feeds actually use.
    private static readonly string[] ImageHosts =
    {
        "prod-fomo-profile-pics.s3.amazonaws.com", "socialimages.pump.fun", "pump.mypinata.cloud",
        "ipfs.io", "token-media.defined.fi", "pbs.twimg.com"
    };

    private static string? SafeImage(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > 500) return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps) return null;
        return ImageHosts.Contains(u.Host, StringComparer.OrdinalIgnoreCase) ? u.AbsoluteUri : null;
    }

    private Entry Build(long id, Notification n, IReadOnlySet<long> recipients, double? usd, string? tokenImage = null, string? traderImage = null)
    {
        var ts = new DateTimeOffset(DateTime.SpecifyKind(n.SentAt, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        var chain = n.Chain;
        var ca = n.ContractAddress;
        var mc = n.MarketCapAtNotification is { } m ? (double?)(double)m : null;
        var isFomo = n.Platform != Platform.Pump;

        string? profile = null;
        if (!string.IsNullOrEmpty(n.Trader) && Regex.IsMatch(n.Trader, @"^[A-Za-z0-9_.\-]+$"))
            profile = isFomo
                ? $"https://fomo.family/profile/{Uri.EscapeDataString(n.Trader)}"
                : $"https://pump.fun/profile/{Uri.EscapeDataString(n.Trader)}";

        object? links = null;
        if (!string.IsNullOrEmpty(ca) && chain.HasValue)
            links = new
            {
                axiom = ChainInfo.AxiomUrl(chain.Value, ca),
                padre = ChainInfo.PadreUrl(chain.Value, ca),
                dex = ChainInfo.DexScreenerUrl(chain.Value, ca),
                gmgn = chain.Value == Chain.SOL ? $"https://gmgn.ai/sol/token/{ca}" : null,
                photon = chain.Value == Chain.SOL ? $"https://photon-sol.tinyastro.io/en/lp/{ca}" : null,
            };

        // Pictures. The coin image goes only into the full/rated versions: on the free and demo
        // feeds the coin is hidden, and its image would give it away. The trader's picture is
        // not secret (the name is shown there too), with the intel file as the fallback for
        // FOMO traders whose live events carry none.
        var coinImg = SafeImage(tokenImage)
            ?? (!string.IsNullOrEmpty(ca) && _tokenImages.TryGetValue(ca, out var known) ? known : null);
        var faceImg = SafeImage(traderImage)
            ?? (string.IsNullOrEmpty(n.Trader) ? null : SafeImage(_intel.Get(n.Platform, n.Trader)?.Avatar));

        var type = n.Type.ToString();
        var fullObj = new
        {
            id, ts, type, platform = isFomo ? "fomo" : "pump", chain = chain?.ToString(),
            trader = n.Trader, profile, ticker = n.Ticker, ca, mc, usd,
            msg = n.Message, links, locked = false, img = coinImg, av = faceImg
        };

        // Same card as Full plus the score pill, for paying members only.
        var score = !string.IsNullOrEmpty(n.Trader) && _scores.TryGetValue(ScoreKey(n.Platform, n.Trader), out var sc) ? sc : null;
        var ratedObj = new
        {
            id, ts, type, platform = isFomo ? "fomo" : "pump", chain = chain?.ToString(),
            trader = n.Trader, profile, ticker = n.Ticker, ca, mc, usd,
            msg = n.Message, links, locked = false, img = coinImg, av = faceImg,
            rank = score?.Rank, weak = score?.Weak
        };

        // Free tier, same rules as the Telegram bot's free message (TelegramService.BuildObfuscatedText): the ticker becomes "coin",
        // the contract is left out, "bought $X" becomes "bought ****"; the "@" in front of the trader's name goes. Sizes of sells,
        // callouts and trending lines stay, as on Telegram. Pictures stay, through opaque links to this server (the real URLs
        // contain the contract address and the trader's user id).
        var freeMsg = n.Message ?? "";
        if (!string.IsNullOrEmpty(n.Trader))
            freeMsg = Regex.Replace(freeMsg, Regex.Escape($"@{n.Trader}"), n.Trader, RegexOptions.IgnoreCase);
        if (!string.IsNullOrEmpty(n.Ticker))
            freeMsg = Regex.Replace(freeMsg, @"(?<![A-Za-z0-9])" + Regex.Escape(n.Ticker) + @"(?![A-Za-z0-9])", "coin", RegexOptions.IgnoreCase);
        freeMsg = Regex.Replace(freeMsg, @"(?<=bought )\$[\d,]+\.?\d*", "****");

        var freeObj = new
        {
            id, ts, type, platform = isFomo ? "fomo" : "pump", chain = chain?.ToString(),
            trader = n.Trader, profile, ticker = (string?)null, ca = (string?)null,
            mc, usd = n.Type == NotificationType.Buy ? (double?)null : usd, msg = freeMsg, links = (object?)null, locked = true,
            img = ProxyUrl(coinImg), av = ProxyUrl(faceImg)
        };

        return new Entry(id, ts, recipients, JsonSerializer.Serialize(fullObj, Json), JsonSerializer.Serialize(ratedObj, Json), JsonSerializer.Serialize(freeObj, Json));
    }
}
