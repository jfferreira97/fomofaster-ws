using System.Buffers.Binary;
using System.Globalization;
using System.Net.WebSockets;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using TelegramBot.Data;
using TelegramBot.Models;

namespace TelegramBot.Services;

// Lock alerts (the old FasterAlerts bot, brought in): listens to Streamflow's two Solana programs over a websocket (outbound
// only, logsSubscribe), and for each new lock reads the transaction (token, amount, who locked), Streamflow's metadata account
// (start, cliff, end), the token's supply and its DexScreener market, saves a LockEvent and sends a CUSTOM_Lock alert through
// the normal pipeline, where each user's lock limits (User.Lock*) apply and free users get the hidden version.
// Helius when Helius:ApiKey is set, the public Solana RPC otherwise. Locks:Enabled=false turns it off.
public class StreamflowLockService : BackgroundService
{
    private const string StrmProgram = "strmRqUCoQUgGUan5YhzUZa6KqdzwX5L6FpUxfmKg5m";   // Streamflow; the second one (aligned unlocks) calls into it
    private static readonly string[] Programs = { StrmProgram, "aSTRM2NKoKxNnkmLWk9sz3k74gKBk9t7bpPrTGxMszH" };
    // Streamflow's metadata account, checked against live locks on 2026-11-15: created_at @9, end_time @33, mint @177,
    // escrow @209, start_time @409, cliff @441 (all u64 little-endian unix seconds). The old bot's 44/420/452 no longer match.
    private const int OffEnd = 33, OffStart = 409, OffCliff = 441;
    private static readonly Regex CreateLog = new(@"Instruction: Create", RegexOptions.Compiled);
    private static readonly Regex ApiKeyInText = new(@"api-key=[^&\s""]+", RegexOptions.Compiled);
    private static readonly TimeSpan SilenceLimit = TimeSpan.FromMinutes(10);   // claims arrive every few minutes: this long silent = a dead socket

    private readonly IServiceProvider _services;
    private readonly IConfiguration _config;
    private readonly ILogger<StreamflowLockService> _logger;
    private readonly string _wsUrl, _rpcUrl;
    private readonly bool _helius;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private readonly SemaphoreSlim _work = new(2);
    private readonly HashSet<string> _seen = new();

    // For the logs and the dashboard.
    public long Heard, Creates, Alerted, Skipped;
    public DateTime? LastEventAt, LastLockAt;
    public bool Connected;

    public StreamflowLockService(IServiceProvider services, IConfiguration config, ILogger<StreamflowLockService> logger)
    {
        _services = services;
        _config = config;
        _logger = logger;
        var key = config["Helius:ApiKey"];
        _helius = !string.IsNullOrWhiteSpace(key);
        _wsUrl = _helius ? $"wss://mainnet.helius-rpc.com/?api-key={key}" : "wss://api.mainnet-beta.solana.com";
        _rpcUrl = _helius ? $"https://mainnet.helius-rpc.com/?api-key={key}" : "https://api.mainnet-beta.solana.com";
    }

    private static string Redact(string? s) => ApiKeyInText.Replace(s ?? "", "api-key=***");

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (string.Equals(_config["Locks:Enabled"], "false", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("Lock alerts are off (Locks:Enabled=false)");
            return;
        }
        try { await Task.Delay(TimeSpan.FromSeconds(10), ct); } catch (OperationCanceledException) { return; }
        var backoff = TimeSpan.FromSeconds(2);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ListenAsync(ct);
                backoff = TimeSpan.FromSeconds(2);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _logger.LogWarning("Lock listener dropped ({Error}), reconnecting in {Seconds}s", Redact(ex.Message), backoff.TotalSeconds);
            }
            Connected = false;
            try { await Task.Delay(backoff, ct); } catch (OperationCanceledException) { return; }
            backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 60));
        }
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        using var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        using (var connect = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connect.CancelAfter(TimeSpan.FromSeconds(15));
            await ws.ConnectAsync(new Uri(_wsUrl), connect.Token);
        }
        for (var i = 0; i < Programs.Length; i++)
        {
            var sub = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = i + 1, method = "logsSubscribe", @params = new object[] { new { mentions = new[] { Programs[i] } }, new { commitment = "confirmed" } } });
            await ws.SendAsync(Encoding.UTF8.GetBytes(sub), WebSocketMessageType.Text, true, ct);
        }
        Connected = true;
        _logger.LogInformation("Lock listener connected ({Source}), watching Streamflow", _helius ? "Helius" : "public Solana RPC");

        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (ws.State == WebSocketState.Open)
        {
            message.SetLength(0);
            using (var read = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                read.CancelAfter(SilenceLimit);
                try
                {
                    WebSocketReceiveResult r;
                    do
                    {
                        r = await ws.ReceiveAsync(buffer, read.Token);
                        if (r.MessageType == WebSocketMessageType.Close) throw new WebSocketException("closed by the server");
                        message.Write(buffer, 0, r.Count);
                    } while (!r.EndOfMessage);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException($"nothing heard for {SilenceLimit.TotalMinutes} minutes");
                }
            }
            OnMessage(message.GetBuffer().AsSpan(0, (int)message.Length), ct);
        }
    }

    private void OnMessage(ReadOnlySpan<byte> raw, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw.ToArray());
            var root = doc.RootElement;
            if (!root.TryGetProperty("method", out var m) || m.GetString() != "logsNotification") return;
            var value = root.GetProperty("params").GetProperty("result").GetProperty("value");
            Heard++;
            LastEventAt = DateTime.UtcNow;
            if (value.TryGetProperty("err", out var err) && err.ValueKind != JsonValueKind.Null) return;
            var isCreate = value.TryGetProperty("logs", out var logs) && logs.ValueKind == JsonValueKind.Array
                && logs.EnumerateArray().Any(l => CreateLog.IsMatch(l.GetString() ?? ""));
            if (!isCreate) return;
            var sig = value.GetProperty("signature").GetString();
            if (string.IsNullOrEmpty(sig)) return;
            lock (_seen)
            {
                if (_seen.Count > 20_000) _seen.Clear();
                if (!_seen.Add(sig)) return;   // both programs report a lock made through the second one
            }
            Creates++;
            _ = Task.Run(() => HandleAsync(sig, ct), ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Lock listener: unreadable message ({Error})", ex.Message);
        }
    }

    // One lock creation, start to alert. Also usable by hand for a past transaction (TestAsync).
    private async Task HandleAsync(string sig, CancellationToken ct, bool send = true)
    {
        await _work.WaitAsync(ct);
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (send && await db.LockEvents.AnyAsync(e => e.Signature == sig, ct)) return;
            var ev = await BuildAsync(sig, ct);
            if (ev == null) return;
            if (!send) { _lastTest = ev; return; }

            db.LockEvents.Add(ev);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException) { return; }   // the other program's report of the same lock got there first
            LastLockAt = DateTime.UtcNow;
            if (ev.SkipReason != null)
            {
                Skipped++;
                _logger.LogInformation("Lock {Sig}: skipped ({Reason}) {Symbol} {Amount} tokens", sig[..8], ev.SkipReason, ev.Symbol ?? ev.Mint[..6], ev.Amount);
                return;
            }
            await SendAsync(scope, ev);
            Alerted++;
        }
        catch (Exception ex)
        {
            _logger.LogError("Lock {Sig}: failed ({Error})", sig.Length > 8 ? sig[..8] : sig, Redact(ex.Message));
        }
        finally { _work.Release(); }
    }

    private LockEvent? _lastTest;

    // Dashboard / tests: build the lock alert for a past creation transaction without saving or sending it.
    public async Task<(LockEvent? Event, string? Text)> TestAsync(string sig, CancellationToken ct)
    {
        _lastTest = null;
        await HandleAsync(sig, ct, send: false);
        var ev = _lastTest;
        return (ev, ev == null ? null : MessageText(ev));
    }

    private async Task<LockEvent?> BuildAsync(string sig, CancellationToken ct)
    {
        JsonElement? tx = null;
        for (var i = 0; i < 6 && tx == null; i++)
        {
            if (i > 0) await Task.Delay(TimeSpan.FromSeconds(1 + i), ct);
            tx = await RpcAsync("getTransaction", new object[] { sig, new { encoding = "jsonParsed", maxSupportedTransactionVersion = 0, commitment = "confirmed" } }, ct);
        }
        if (tx is not { } t) { _logger.LogWarning("Lock {Sig}: transaction not found", sig[..8]); return null; }
        var p = ParseCreate(t);
        var ev = new LockEvent
        {
            Signature = sig,
            ReceivedAt = DateTime.UtcNow,
            CreatedAt = t.TryGetProperty("blockTime", out var bt) && bt.ValueKind == JsonValueKind.Number ? DateTimeOffset.FromUnixTimeSeconds(bt.GetInt64()).UtcDateTime : DateTime.UtcNow,
        };
        if (p == null) { ev.Mint = "?"; ev.SkipReason = "not a lock"; return ev; }
        ev.Mint = p.Value.Mint;
        ev.Amount = p.Value.Amount;
        ev.Sender = p.Value.Sender;
        ev.StreamAccount = p.Value.Metadata ?? "";

        if (p.Value.Metadata != null)
        {
            var dates = await ReadDatesAsync(p.Value.Metadata, ct);
            ev.StartAt = dates.Start;
            ev.CliffAt = dates.Cliff;
            ev.EndAt = dates.End;
        }

        var supply = await SupplyAsync(ev.Mint, ct);
        if (supply is > 0) ev.PercentSupply = Math.Round(ev.Amount / supply.Value * 100m, 6);

        // A token with no market is nothing a trader can act on. New pairs can take a moment to show up on DexScreener.
        Market? market = null;
        foreach (var wait in new[] { 0, 5, 15 })
        {
            if (wait > 0) await Task.Delay(TimeSpan.FromSeconds(wait), ct);
            market = await MarketAsync(ev.Mint, ct);
            if (market != null) break;
        }
        if (market == null) { ev.SkipReason = "no market"; return ev; }
        ev.Symbol = market.Symbol;
        ev.Name = market.Name;
        ev.MarketCapUsd = market.MarketCap is double mc ? (decimal)mc : null;
        ev.UsdValue = market.Price is double price ? Math.Round(ev.Amount * (decimal)price, 2) : null;
        ev.PairCreatedAt = market.PairCreatedAt;
        _images[ev.Mint] = market.Image;
        if (ev.Amount <= 0) ev.SkipReason = "nothing locked";
        return ev;
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string?> _images = new();

    private async Task SendAsync(IServiceScope scope, LockEvent ev)
    {
        var telegram = scope.ServiceProvider.GetRequiredService<ITelegramService>();
        var image = _images.TryRemove(ev.Mint, out var img) ? img : null;
        var facts = new LockFacts(ev.PercentSupply, ev.MarketCapUsd is decimal mc ? (double)mc : null, ev.PairCreatedAt, LockDays(ev));
        var link = ExtraLinks(ev);
        scope.ServiceProvider.GetRequiredService<FeedService>().RememberLock(ev);   // the feed row's details
        await telegram.SendNotificationToAllUsersAsync(
            new NotificationRequest { Message = MessageText(ev) },
            contractAddress: ev.Mint, chain: Chain.SOL, ticker: ev.Symbol,
            marketCap: ev.MarketCapUsd is decimal m ? (double)m : null,
            notificationType: NotificationType.CUSTOM_Lock, platform: Platform.Fomo,
            tokenImage: image, feedUsd: ev.UsdValue is decimal u ? (double)u : null,
            lockFacts: facts, extraLink: link);
        _logger.LogInformation("Lock alert: {Pct}% of {Symbol} for {Days} days ({Sig})", ev.PercentSupply?.ToString("0.##", CultureInfo.InvariantCulture) ?? "?", ev.Symbol, LockDays(ev)?.ToString("0", CultureInfo.InvariantCulture) ?? "?", ev.Signature[..8]);
    }

    // Solscan | Streamflow, as the old bot ended its alert (after our usual Axiom / Terminal / Dexscreener).
    public static string ExtraLinks(LockEvent ev) =>
        $"[Solscan](https://solscan.io/tx/{ev.Signature})"
        + (string.IsNullOrEmpty(ev.StreamAccount) ? "" : $" | [Streamflow](https://app.streamflow.finance/contract/solana/mainnet/{ev.StreamAccount})");

    // Days from the lock until everything is claimable (null = no fixed end: price-based unlocks).
    private static double? LockDays(LockEvent ev) =>
        ev.EndAt is DateTime end ? Math.Max(0, (end - ev.CreatedAt).TotalDays)
        : ev.CliffAt is DateTime cliff ? Math.Max(0, (cliff - ev.CreatedAt).TotalDays) : null;

    // Plain text (the pipeline escapes it): "🔒 4.2% of $SYM supply locked · $12.3K · $85K MC" and the unlock line.
    // The old FasterAlerts bot's wording, which the owner liked (TelegramService bolds "… LOCKED" and the duration, and puts the
    // contract between the two lines):
    //   🔒 2.42% of $SYM ($37.52k MC) LOCKED · 2d 4h old
    //   ⏰ For: 16d | Until Dec 1, 2026 05:00 UTC
    // The unlock is the first moment tokens can be claimed: the cliff when there is one, the end otherwise. No fixed end
    // (price-based "aligned" unlocks) reads "See Streamflow", as it did there.
    public static string MessageText(LockEvent ev)
    {
        var inv = CultureInfo.InvariantCulture;
        var pct = ev.PercentSupply is decimal p && p > 0 ? p.ToString("0.00", inv) + "%" : "?%";
        var sym = string.IsNullOrWhiteSpace(ev.Symbol) ? "token" : ev.Symbol;
        var mc = ev.MarketCapUsd is decimal m && m > 0 ? $" (${OldMc(m)} MC)" : "";
        var age = ev.PairCreatedAt is DateTime born ? $" · {OldAge(born)}" : "";
        var head = $"🔒 {pct} of ${sym}{mc} LOCKED{age}";

        DateTime? unlock = ev.CliffAt is DateTime c && ev.StartAt is DateTime s && c > s ? c : ev.EndAt;
        var when = unlock is DateTime u
            ? $"⏰ For: {OldTimeUntil(u)} | Until {u.ToString("MMM d, yyyy HH:mm", inv)} UTC"
            : "⏰ Until: See Streamflow";
        return head + "\n" + when;
    }

    // FasterAlerts' own formats, kept as they were.
    private static string OldMc(decimal mc) => mc switch
    {
        >= 1_000_000_000 => (mc / 1_000_000_000).ToString("0.##", CultureInfo.InvariantCulture) + "b",
        >= 1_000_000 => (mc / 1_000_000).ToString("0.##", CultureInfo.InvariantCulture) + "m",
        _ => (mc / 1_000).ToString("0.##", CultureInfo.InvariantCulture) + "k",
    };

    private static string OldAge(DateTime createdAt)
    {
        var diff = DateTime.UtcNow - createdAt;
        int days = (int)diff.TotalDays, hours = diff.Hours, minutes = diff.Minutes;
        if (days >= 1) return hours > 0 ? $"{days}d {hours}h old" : $"{days}d old";
        if (hours >= 1) return minutes > 0 ? $"{hours}h {minutes}m old" : $"{hours}h old";
        return $"{Math.Max(0, minutes)}min old";
    }

    private static string OldTimeUntil(DateTime unlock)
    {
        var diff = unlock - DateTime.UtcNow;
        if (diff <= TimeSpan.Zero) return "imminent";
        int totalDays = (int)diff.TotalDays, hours = diff.Hours, minutes = diff.Minutes;
        if (totalDays >= 60) { int months = totalDays / 30, days = totalDays % 30; return days > 0 ? $"{months}mo {days}d" : $"{months}mo"; }
        if (totalDays >= 1) return hours > 0 ? $"{totalDays}d {hours}h" : $"{totalDays}d";
        return minutes > 0 ? $"{hours}h {minutes}m" : $"{hours}h";
    }

    // From the confirmed transaction: the escrow (the token account that received the tokens and owns itself), the token,
    // the amount, who locked it (the account the tokens left) and Streamflow's new metadata account.
    private static (string Mint, decimal Amount, string? Sender, string? Metadata)? ParseCreate(JsonElement tx)
    {
        if (!tx.TryGetProperty("meta", out var meta) || meta.ValueKind != JsonValueKind.Object) return null;
        if (meta.TryGetProperty("err", out var err) && err.ValueKind != JsonValueKind.Null) return null;
        var message = tx.GetProperty("transaction").GetProperty("message");
        var keys = message.GetProperty("accountKeys").EnumerateArray()
            .Select(k => k.ValueKind == JsonValueKind.String ? k.GetString()! : k.GetProperty("pubkey").GetString()!).ToList();

        IEnumerable<JsonElement> All()
        {
            foreach (var ix in message.GetProperty("instructions").EnumerateArray()) yield return ix;
            if (meta.TryGetProperty("innerInstructions", out var inner) && inner.ValueKind == JsonValueKind.Array)
                foreach (var g in inner.EnumerateArray())
                    foreach (var ix in g.GetProperty("instructions").EnumerateArray()) yield return ix;
        }
        string? metadata = null;
        foreach (var ix in All())
        {
            if (ix.TryGetProperty("program", out var prog) && prog.GetString() == "system"
                && ix.TryGetProperty("parsed", out var parsed) && parsed.ValueKind == JsonValueKind.Object
                && parsed.TryGetProperty("type", out var type) && type.GetString() == "createAccount"
                && parsed.GetProperty("info").TryGetProperty("owner", out var owner) && owner.GetString() == StrmProgram)
            {
                metadata = parsed.GetProperty("info").GetProperty("newAccount").GetString();
                break;
            }
        }

        static Dictionary<int, (string Mint, string? Owner, BigInteger Raw, int Decimals)> Balances(JsonElement meta, string name)
        {
            var map = new Dictionary<int, (string, string?, BigInteger, int)>();
            if (!meta.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array) return map;
            foreach (var b in arr.EnumerateArray())
            {
                var amt = b.GetProperty("uiTokenAmount");
                map[b.GetProperty("accountIndex").GetInt32()] = (
                    b.GetProperty("mint").GetString()!,
                    b.TryGetProperty("owner", out var o) ? o.GetString() : null,
                    BigInteger.Parse(amt.GetProperty("amount").GetString()!, CultureInfo.InvariantCulture),
                    amt.GetProperty("decimals").GetInt32());
            }
            return map;
        }
        var pre = Balances(meta, "preTokenBalances");
        var post = Balances(meta, "postTokenBalances");
        var deltas = post.Select(kv => (Index: kv.Key, kv.Value.Mint, kv.Value.Owner, kv.Value.Decimals,
                Delta: kv.Value.Raw - (pre.TryGetValue(kv.Key, out var b) ? b.Raw : BigInteger.Zero)))
            .ToList();
        var escrows = deltas
            .Where(d => d.Delta > 0 && d.Index < keys.Count && d.Owner == keys[d.Index])
            .OrderByDescending(d => d.Delta)
            .ToList();
        if (escrows.Count == 0) return null;
        var e = escrows[0];
        var sender = deltas.Where(d => d.Mint == e.Mint && d.Delta < 0).OrderBy(d => d.Delta).Select(d => d.Owner).FirstOrDefault() ?? keys.FirstOrDefault();
        var amount = (decimal)e.Delta / (decimal)Math.Pow(10, e.Decimals);
        return (e.Mint, amount, sender, metadata);
    }

    private async Task<(DateTime? Start, DateTime? Cliff, DateTime? End)> ReadDatesAsync(string account, CancellationToken ct)
    {
        for (var i = 0; i < 3; i++)
        {
            if (i > 0) await Task.Delay(TimeSpan.FromSeconds(1), ct);
            var r = await RpcAsync("getAccountInfo", new object[] { account, new { encoding = "base64", commitment = "confirmed" } }, ct);
            if (r is not { } res || !res.TryGetProperty("value", out var v) || v.ValueKind != JsonValueKind.Object) continue;
            var bytes = Convert.FromBase64String(v.GetProperty("data")[0].GetString()!);
            if (bytes.Length < OffCliff + 8) return (null, null, null);
            static DateTime? Ts(byte[] b, int off)
            {
                var s = BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(off, 8));
                return s is > 1_577_836_800UL and < 4_102_444_800UL ? DateTimeOffset.FromUnixTimeSeconds((long)s).UtcDateTime : null;   // 2020..2100
            }
            return (Ts(bytes, OffStart), Ts(bytes, OffCliff), Ts(bytes, OffEnd));
        }
        return (null, null, null);
    }

    private async Task<decimal?> SupplyAsync(string mint, CancellationToken ct)
    {
        var r = await RpcAsync("getTokenSupply", new object[] { mint }, ct);
        return r is { } res && res.TryGetProperty("value", out var v)
            && decimal.TryParse(v.GetProperty("uiAmountString").GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : null;
    }

    private sealed record Market(string? Symbol, string? Name, double? Price, double? MarketCap, DateTime? PairCreatedAt, string? Image);

    // The token's most liquid DexScreener pair (where it is the base token), and the oldest pair's age.
    private async Task<Market?> MarketAsync(string mint, CancellationToken ct)
    {
        try
        {
            using var res = await _http.GetAsync($"https://api.dexscreener.com/tokens/v1/solana/{mint}", ct);
            if (!res.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            var pairs = doc.RootElement.EnumerateArray()
                .Where(pr => pr.TryGetProperty("baseToken", out var bt) && bt.GetProperty("address").GetString() == mint).ToList();
            if (pairs.Count == 0) return null;
            static double? Num(JsonElement e, string name) =>
                e.TryGetProperty(name, out var v) ? v.ValueKind switch
                {
                    JsonValueKind.Number => v.GetDouble(),
                    JsonValueKind.String when double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
                    _ => null,
                } : null;
            var best = pairs.OrderByDescending(pr => pr.TryGetProperty("liquidity", out var l) ? Num(l, "usd") ?? 0 : 0).First();
            var oldest = pairs.Select(pr => Num(pr, "pairCreatedAt")).Where(v => v is > 0).Min();
            var bt = best.GetProperty("baseToken");
            var image = best.TryGetProperty("info", out var info) && info.TryGetProperty("imageUrl", out var im) ? im.GetString() : null;
            return new Market(bt.GetProperty("symbol").GetString(), bt.TryGetProperty("name", out var n) ? n.GetString() : null,
                Num(best, "priceUsd"), Num(best, "marketCap") ?? Num(best, "fdv"),
                oldest is double ms ? DateTimeOffset.FromUnixTimeMilliseconds((long)ms).UtcDateTime : null, image);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogDebug("DexScreener lookup failed for {Mint}: {Error}", mint, ex.Message);
            return null;
        }
    }

    private async Task<JsonElement?> RpcAsync(string method, object[] args, CancellationToken ct)
    {
        try
        {
            var body = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method, @params = args });
            using var res = await _http.PostAsync(_rpcUrl, new StringContent(body, Encoding.UTF8, "application/json"), ct);
            if (!res.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            return doc.RootElement.TryGetProperty("result", out var r) && r.ValueKind != JsonValueKind.Null ? r.Clone() : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogDebug("RPC {Method} failed: {Error}", method, Redact(ex.Message));
            return null;
        }
    }
}
