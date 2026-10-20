using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TelegramBot.Data;
using TelegramBot.Models;
using TelegramBot.Services;

namespace TelegramBot.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DashboardController : ControllerBase
{
    private readonly AppDbContext _dbContext;
    private readonly ILogger<DashboardController> _logger;
    private readonly ITelegramService _telegramService;
    private readonly AppConfigService _appConfig;
    private readonly MonitorStore _monitor;

    public DashboardController(
        AppDbContext dbContext,
        ILogger<DashboardController> logger,
        ITelegramService telegramService,
        AppConfigService appConfig,
        MonitorStore monitor)
    {
        _dbContext = dbContext;
        _logger = logger;
        _telegramService = telegramService;
        _appConfig = appConfig;
        _monitor = monitor;
    }

    // ---- Monitoring tab: is the app healthy? One call, read-only. ----
    private static string Ago(TimeSpan t) =>
        t.TotalSeconds < 90 ? $"{t.TotalSeconds:0}s ago" : t.TotalMinutes < 90 ? $"{t.TotalMinutes:0} min ago" : t.TotalHours < 48 ? $"{t.TotalHours:0.#} h ago" : $"{t.TotalDays:0} d ago";

    private static double? Pctl(List<double> v, double p)
    {
        if (v.Count == 0) return null;
        v.Sort();
        return v[(int)Math.Min(v.Count - 1, Math.Floor(p * (v.Count - 1)))];
    }

    private static object Tile(string label, string value) => new { l = label, v = value };

    // ---- Queue tab: alerts being sent right now, and the ones that just went out. In memory, cheap, polled every second. ----
    [HttpGet("queue")]
    public IActionResult GetQueue()
    {
        var live = _monitor.Live();
        var now = DateTime.UtcNow;
        double Sec(DateTime a, DateTime b) => Math.Round((b - a).TotalSeconds, 2);
        return Ok(new
        {
            pendingPaid = live.Sum(a => a.PaidTotal - a.PaidSent - a.PaidFailed),
            pendingFree = live.Sum(a => a.FreeTotal - a.FreeSent - a.FreeFailed),
            sending = live.Count(a => a.DoneAt == null && a.PaidTotal + a.FreeTotal > 0),
            alerts = live.Select(a => new
            {
                seq = a.Seq,
                at = a.StartedAt,
                ageSec = Sec(a.StartedAt, now),
                platform = a.Platform,
                type = a.Type,
                trader = a.Trader,
                ticker = a.Ticker,
                text = a.Text is { Length: > 160 } t ? t[..160] + "…" : a.Text,
                usd = a.UsdAmount,
                paid = new { total = a.PaidTotal, sent = a.PaidSent, failed = a.PaidFailed, doneSec = a.PaidDoneAt is { } d ? (double?)Sec(a.StartedAt, d) : null },
                free = new { total = a.FreeTotal, sent = a.FreeSent, failed = a.FreeFailed },
                doneSec = a.DoneAt is { } e ? (double?)Sec(a.StartedAt, e) : null,
            }),
        });
    }

    [HttpGet("monitor")]
    public async Task<IActionResult> GetMonitor()
    {
        var now = DateTime.UtcNow;
        var hourAgo = now.AddHours(-1);
        var upSec = (now - _monitor.StartedAt).TotalSeconds;

        var ws = await _dbContext.WsEvents.AsNoTracking().Where(e => e.ReceivedAt > hourAgo)
            .Select(e => new { e.ReceivedAt, e.CreatedAt }).ToListAsync();
        var pump = await _dbContext.PumpEvents.AsNoTracking().Where(e => e.ReceivedAt > hourAgo)
            .Select(e => new { e.ReceivedAt, e.CreatedAt, e.Kind }).ToListAsync();
        var lastWs = await _dbContext.WsEvents.AsNoTracking().OrderByDescending(e => e.Id).Select(e => (DateTime?)e.ReceivedAt).FirstOrDefaultAsync();
        var lastPump = await _dbContext.PumpEvents.AsNoTracking().OrderByDescending(e => e.Id).Select(e => (DateTime?)e.ReceivedAt).FirstOrDefaultAsync();

        var components = new List<object>();

        object Sidecar(string name, string label, string source, int events, List<double> lags, DateTime? lastEvent, params object[] extraTiles)
        {
            var beat = _monitor.GetBeat(source);
            double? age = beat.HasValue ? (now - beat.Value.Last).TotalSeconds : null;
            var state = age == null ? (upSec < 120 ? "idle" : "down") : age <= 90 ? "ok" : age <= 180 ? "degraded" : "down";
            var summary = age == null ? "no heartbeat since the backend started" : state == "ok" ? $"alive · last heartbeat {age:0}s ago" : $"heartbeat is stale: {age:0}s ago";
            var med = Pctl(lags, 0.5);
            var p95 = Pctl(lags, 0.95);
            var tiles = new List<object>
            {
                Tile("Last heartbeat", age == null ? "—" : $"{age:0}s ago"),
                Tile("Events (1 h)", events.ToString()),
                Tile("Median delay", med == null ? "—" : $"{med / 1000.0:0.0} s"),
                Tile("Slowest 5%", p95 == null ? "—" : $"{p95 / 1000.0:0.0} s"),
                Tile("Last event", lastEvent == null ? "—" : Ago(now - lastEvent.Value)),
            };
            tiles.AddRange(extraTiles);
            return new { name, label, state, summary, tiles, recent = Array.Empty<object>(), info = new Dictionary<string, string>() };
        }

        static List<double> Lags(IEnumerable<(DateTime rec, DateTime? cr)> xs) =>
            xs.Where(x => x.cr.HasValue).Select(x => Math.Max(0, (x.rec - x.cr!.Value).TotalMilliseconds)).ToList();

        components.Add(Sidecar("fomo", "👀 FOMO sidecar (account 2)", "fomo-ws-sidecar", ws.Count,
            Lags(ws.Select(e => (e.ReceivedAt, e.CreatedAt))), lastWs));
        var pumpTrades = pump.Count(e => e.Kind == "trade");
        components.Add(Sidecar("pump", "💊 Pump sidecar (live stream + safety poll)", "pump-ws-sidecar", pump.Count,
            Lags(pump.Select(e => (e.ReceivedAt, e.CreatedAt))), lastPump,
            Tile("Pump trades (1 h)", pumpTrades.ToString())));

        // Telegram delivery
        var alerts = _monitor.Alerts(TimeSpan.FromHours(1));
        var sent = alerts.Sum(a => a.Recipients);
        var failed = alerts.Sum(a => a.Failed);
        var failRate = sent + failed == 0 ? 0.0 : failed / (double)(sent + failed);
        var fan = alerts.Select(a => (double)a.FanOutMs).ToList();
        var lastAlert = alerts.Count == 0 ? (DateTime?)null : alerts.Max(a => a.At);
        var deliveryState = !_telegramService.IsConfigured() ? "down" : alerts.Count == 0 ? "idle" : failRate > 0.30 ? "down" : failRate > 0.05 ? "degraded" : "ok";
        var deliverySummary = !_telegramService.IsConfigured() ? "Telegram bot is not configured"
            : alerts.Count == 0 ? "no alert has gone out since the backend started (or in the last hour)"
            : $"{alerts.Count} alerts reached {sent} chats in the last hour" + (failed > 0 ? $", {failed} failed" : ", none failed");
        components.Add(new
        {
            name = "telegram", label = "✈️ Telegram delivery", state = deliveryState, summary = deliverySummary,
            tiles = new List<object>
            {
                Tile("Alerts (1 h)", alerts.Count.ToString()),
                Tile("Messages sent", sent.ToString()),
                Tile("Failed", failed.ToString()),
                Tile("Fan-out median", Pctl(fan.ToList(), 0.5) is double m ? $"{m / 1000.0:0.0} s" : "—"),
                Tile("Fan-out slowest", fan.Count == 0 ? "—" : $"{fan.Max() / 1000.0:0.0} s"),
                Tile("Last alert", lastAlert == null ? "—" : Ago(now - lastAlert.Value)),
            },
            recent = Array.Empty<object>(),
            info = new Dictionary<string, string> { ["Alerts since start"] = _monitor.Alerts(TimeSpan.FromHours(24)).Count.ToString() },
        });

        // ---- Alert speed, stage by stage. Every gap between two backend/sidecar stamps is exact (same PC clock);
        // only "from the source" uses the source's own clock, so it is shifted by the NTP offset and is good to ±0.5 s
        // (pump.fun and FOMO give whole seconds).
        var off = _monitor.ClockOffsetMs;   // positive = this PC is behind real time
        var a30 = _monitor.Alerts(TimeSpan.FromMinutes(30));
        object Stage(string label, string note, IEnumerable<double> values)
        {
            var l = values.Where(x => x >= -1000 && x < 900_000).Select(x => Math.Max(0, x)).ToList();
            return new { label, note, n = l.Count, p50 = Pctl(l.ToList(), 0.5), p90 = Pctl(l.ToList(), 0.9), max = l.Count == 0 ? (double?)null : l.Max() };
        }
        var trades = a30.Where(a => a.Platform == "Pump" && (a.Via == "stream" || a.Via == "rest") && a.SidecarRecvMs.HasValue && a.SourceCreatedMs.HasValue).ToList();
        var stages = new List<object>
        {
            Stage("1a · pump.fun → our sidecar, live stream", "pump.fun's own publish delay is in here; same data the website gets", trades.Where(a => a.Via == "stream").Select(a => (double)(a.SidecarRecvMs!.Value + off - a.SourceCreatedMs!.Value))),
            Stage("1b · pump.fun → our sidecar, slower poll", "trades the live stream did not deliver", trades.Where(a => a.Via == "rest").Select(a => (double)(a.SidecarRecvMs!.Value + off - a.SourceCreatedMs!.Value))),
            Stage("2 · sidecar → backend", "one local HTTP call", trades.Where(a => a.ArrivedMs > 0).Select(a => (double)(a.ArrivedMs - a.SidecarRecvMs!.Value))),
            Stage("3 · backend → web feed", "who gets it + database lookups, then the website /feed is fed", a30.Where(a => a.ArrivedMs > 0 && a.PublishedMs > 0).Select(a => (double)(a.PublishedMs - a.ArrivedMs))),
            Stage("4 · web feed → FIRST Telegram message", "Telegram accepted the first alert", a30.Where(a => a.PublishedMs > 0 && a.FirstAckMs.HasValue).Select(a => (double)(a.FirstAckMs!.Value - a.PublishedMs))),
            Stage("5 · web feed → last PAYING user", "paying and trial users go first", a30.Where(a => a.PublishedMs > 0 && a.PaidLastAckMs.HasValue).Select(a => (double)(a.PaidLastAckMs!.Value - a.PublishedMs))),
            Stage("6 · web feed → everybody", "the free tail, behind the 25 messages/s Telegram limit", a30.Where(a => a.PublishedMs > 0 && a.LastAckMs.HasValue).Select(a => (double)(a.LastAckMs!.Value - a.PublishedMs))),
            Stage("TOTAL · pump.fun → web feed", "clock-corrected, ±0.5 s", trades.Where(a => a.PublishedMs > 0).Select(a => (double)(a.PublishedMs + off - a.SourceCreatedMs!.Value))),
            Stage("TOTAL · pump.fun → first Telegram message", "clock-corrected, ±0.5 s: this is how fast an alert reaches a user", trades.Where(a => a.FirstAckMs.HasValue).Select(a => (double)(a.FirstAckMs!.Value + off - a.SourceCreatedMs!.Value))),
            Stage("TOTAL · pump.fun → last paying user", "clock-corrected, ±0.5 s", trades.Where(a => a.PaidLastAckMs.HasValue).Select(a => (double)(a.PaidLastAckMs!.Value + off - a.SourceCreatedMs!.Value))),
        };
        var viaStream = a30.Count(a => a.Platform == "Pump" && a.Via == "stream");
        var viaRest = a30.Count(a => a.Platform == "Pump" && a.Via == "rest");

        // Is pump.fun's live stream actually delivering? (It sat silent for hours once, and degraded again later.)
        var a10 = _monitor.Alerts(TimeSpan.FromMinutes(10)).Where(a => a.Platform == "Pump" && (a.Via == "stream" || a.Via == "rest")).ToList();
        var s10 = a10.Count(a => a.Via == "stream"); var r10 = a10.Count - s10;
        var lastStream = a10.Where(a => a.Via == "stream").Select(a => (DateTime?)a.At).DefaultIfEmpty(null).Max();
        var streamState = a10.Count < 5 ? "idle" : s10 == 0 ? "down" : s10 * 100 / a10.Count < 60 ? "degraded" : "ok";
        var streamSummary = a10.Count < 5 ? "too few trades in the last 10 minutes to judge"
            : s10 == 0 ? $"the live stream delivered nothing in 10 min; {r10} trades only came through the slower poll"
            : $"live stream delivered {s10 * 100 / a10.Count}% of the last {a10.Count} trades; {r10} came through the slower poll";
        components.Add(new
        {
            name = "stream", label = "⚡ Pump live stream (what makes alerts fast)", state = streamState, summary = streamSummary,
            tiles = new List<object> { Tile("Via stream (10 min)", s10.ToString()), Tile("Via slow poll (10 min)", r10.ToString()), Tile("Last stream trade", lastStream == null ? "—" : Ago(now - lastStream.Value)), Tile("Share in 30 min", viaStream + viaRest == 0 ? "—" : $"{100 * viaStream / (viaStream + viaRest)}%") },
            recent = Array.Empty<object>(), info = new Dictionary<string, string>(),
        });

        // Website: the real public path, and the local Caddy
        object Site(string name, string label, string probe, string what)
        {
            var ps = _monitor.Probes(probe);
            if (ps.Count == 0)
                return new { name, label, state = "idle", summary = "waiting for the first check", tiles = new List<object>(), recent = Array.Empty<object>(), info = new Dictionary<string, string>() };
            var last = ps[^1];
            bool Ok(ProbeRecord p) => p.Status is >= 200 and < 400;
            var state = Ok(last) ? "ok" : ps.TakeLast(3).All(p => !Ok(p)) && ps.Count >= 3 ? "down" : "degraded";
            var okMs = ps.Where(Ok).TakeLast(15).Select(p => (double)p.Ms).ToList();
            var summary = Ok(last) ? $"{what}: HTTP {last.Status} in {last.Ms} ms" : $"{what}: failing ({(last.Status == 0 ? last.Error : "HTTP " + last.Status)})";
            return new
            {
                name, label, state, summary,
                tiles = new List<object>
                {
                    Tile("Last check", Ago(now - last.At)),
                    Tile("Latency (median)", Pctl(okMs, 0.5) is double md ? $"{md:0} ms" : "—"),
                    Tile("Success (last " + ps.Count + ")", $"{100.0 * ps.Count(Ok) / ps.Count:0}%"),
                },
                recent = ps.Select(p => new { at = p.At, status = p.Status, ms = p.Ms }).ToArray(),
                info = new Dictionary<string, string>(),
            };
        }
        components.Add(Site("site-public", "🌐 Website, public path", "public", "internet → Cloudflare → tunnel → this PC"));
        components.Add(Site("site-local", "🏠 Website, local Caddy", "local", "127.0.0.1:8080"));

        // Users + database + machine
        var users = await _dbContext.Users.AsNoTracking().Where(u => u.IsActive)
            .Select(u => new { u.IsRN4L, u.IsRegisteredNurse, u.RNExpiresAt, u.TrialExpiresAt, u.NotifyPumpBuySell, u.NotifyPumpCallouts, u.NotifyFomoBuySell }).ToListAsync();
        var rn4l = users.Count(u => u.IsRN4L);
        var paid = users.Count(u => !u.IsRN4L && u.IsRegisteredNurse && u.RNExpiresAt > now);
        var trial = users.Count(u => !u.IsRN4L && !(u.IsRegisteredNurse && u.RNExpiresAt > now) && u.TrialExpiresAt > now);
        var free = users.Count - rn4l - paid - trial;

        double dbMb = 0; try { dbMb = new FileInfo("fomofaster_ws.db").Length / 1048576.0; } catch { }
        double freeGb = 0; try { freeGb = new DriveInfo(Path.GetPathRoot(Environment.CurrentDirectory)!).AvailableFreeSpace / 1073741824.0; } catch { }
        var proc = System.Diagnostics.Process.GetCurrentProcess();
        var memMb = proc.WorkingSet64 / 1048576.0;
        var clockKnown = _monitor.ClockCheckedAt.HasValue;
        var clockText = !clockKnown ? "not measured yet" : $"{Math.Abs(off) / 1000.0:0.0} s {(off >= 0 ? "behind" : "ahead")}";
        var sysState = freeGb < 3 ? "down" : freeGb < 10 || memMb > 2500 || (clockKnown && Math.Abs(off) > 1000) ? "degraded" : "ok";
        components.Add(new
        {
            name = "system", label = "🖥️ This machine and the database", state = sysState,
            summary = $"backend up {(upSec < 3600 ? (upSec / 60).ToString("0") + " min" : (upSec / 3600).ToString("0.#") + " h")} · {freeGb:0} GB free on disk · clock {clockText}" + (clockKnown && Math.Abs(off) > 1000 ? " (Windows time service is stopped: turn on Set time automatically and press Sync now)" : ""),
            tiles = new List<object>
            {
                Tile("PC clock vs real time", clockText),
                Tile("Backend uptime", upSec < 3600 ? $"{upSec / 60:0} min" : $"{upSec / 3600:0.#} h"),
                Tile("Backend memory", $"{memMb:0} MB"),
                Tile("Database", $"{dbMb:0} MB"),
                Tile("Free disk", $"{freeGb:0} GB"),
            },
            recent = Array.Empty<object>(),
            info = new Dictionary<string, string>(),
        });

        var live = components.Select(c => (dynamic)c).Where(c => (string)c.state != "idle").ToList();
        var bad = live.Where(c => (string)c.state != "ok").ToList();
        var overall = bad.Any(c => (string)c.state == "down") ? "down" : bad.Count > 0 ? "degraded" : "ok";
        var text = bad.Count == 0 ? "All systems healthy" : bad.Count == 1 ? $"1 thing needs attention: {(string)bad[0].label}" : $"{bad.Count} things need attention";

        var byType = await _dbContext.Notifications.AsNoTracking().Where(n => n.SentAt > hourAgo)
            .GroupBy(n => new { n.Platform, n.Type }).Select(g => new { platform = g.Key.Platform.ToString(), type = g.Key.Type.ToString(), n = g.Count() }).ToListAsync();

        return Ok(new
        {
            monitor = new { overall = new { state = overall, text }, uptimeSec = (int)upSec, components },
            alertsLastHour = byType.OrderByDescending(x => x.n),
            users = new { active = users.Count, rn4l, paid, trial, free, pumpTxOn = users.Count(u => u.NotifyPumpBuySell), pumpCalloutsOn = users.Count(u => u.NotifyPumpCallouts), fomoTxOn = users.Count(u => u.NotifyFomoBuySell) },
            rollout = new { pumpTradeAlerts = (await _appConfig.GetAsync("PumpTradeAlerts")) ?? "owner" },
            pipeline = new { windowMin = 30, alerts = a30.Count, pumpTrades = trades.Count, viaStream, viaRest, clockOffsetMs = off, clockCheckedAt = _monitor.ClockCheckedAt, stages },
            generatedAt = now,
        });
    }

    [HttpGet("notifications")]
    public async Task<IActionResult> GetRecentNotifications([FromQuery] int limit = 50, [FromQuery] int? id = null)
    {
        try
        {
            // Get total active users count (single query)
            var totalUsers = await _dbContext.Users.CountAsync(u => u.IsActive);

            // Get recent notifications (single query)
            var recentNotifications = id.HasValue
                ? await _dbContext.Notifications.Where(n => n.Id == id.Value).ToListAsync()
                : await _dbContext.Notifications
                    .OrderByDescending(n => n.SentAt)
                    .Take(limit)
                    .ToListAsync();

            var notificationIds = recentNotifications.Select(n => n.Id).ToList();

            // Get all sent messages for these notifications (single query)
            var sentMessagesData = await _dbContext.SentMessages
                .Where(sm => notificationIds.Contains(sm.NotificationId))
                .GroupBy(sm => sm.NotificationId)
                .Select(g => new
                {
                    NotificationId = g.Key,
                    RecipientCount = g.Count(),
                    IsManuallyEdited = g.Any(sm => sm.IsManuallyEdited),
                    IsSystemEdited = g.Any(sm => sm.IsSystemEdited),
                    EditedAt = g.Where(sm => (sm.IsManuallyEdited || sm.IsSystemEdited) && sm.EditedAt != null)
                                .Select(sm => sm.EditedAt)
                                .Max()
                })
                .ToListAsync();

            // Create lookup dictionary for O(1) access
            var sentMessagesLookup = sentMessagesData.ToDictionary(x => x.NotificationId);

            // Combine data in memory
            var notifications = recentNotifications
                .Select(n =>
                {
                    var sentData = sentMessagesLookup.GetValueOrDefault(n.Id);
                    return new
                    {
                        id = n.Id,
                        message = n.Message,
                        ticker = n.Ticker,
                        trader = n.Trader,
                        contractAddress = n.ContractAddress,
                        chain = n.Chain?.ToString(),
                        sentAt = n.SentAt,
                        recipientCount = sentData?.RecipientCount ?? 0,
                        totalUsers = totalUsers,
                        marketCapAtNotification = n.MarketCapAtNotification
                    };
                })
                .OrderBy(n => n.sentAt) // Chronological order
                .ToList();

            return Ok(new
            {
                status = "success",
                notifications
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching dashboard notifications");
            return StatusCode(500, new
            {
                status = "error",
                message = "Failed to fetch notifications"
            });
        }
    }

    [HttpGet("notifications/{id}/recipients")]
    public async Task<IActionResult> GetNotificationRecipients(int id)
    {
        try
        {
            // Get all users who received this notification
            var recipients = await _dbContext.SentMessages
                .Where(sm => sm.NotificationId == id)
                .Join(_dbContext.Users,
                    sm => sm.ChatId,
                    u => u.ChatId,
                    (sm, u) => new
                    {
                        chatId = u.ChatId,
                        username = u.Username,
                        firstName = u.FirstName
                    })
                .ToListAsync();

            return Ok(new
            {
                status = "success",
                recipients
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching notification recipients");
            return StatusCode(500, new
            {
                status = "error",
                message = "Failed to fetch recipients"
            });
        }
    }

    [HttpGet("payments")]
    public async Task<IActionResult> GetPayments()
    {
        try
        {
            var users = await _dbContext.Users.ToListAsync();
            var payments = await _dbContext.PendingPayments.ToListAsync();

            var now = DateTime.UtcNow;

            var rows = users
                .Where(u => payments.Any(p => p.ChatId == u.ChatId) || u.IsRegisteredNurse || u.IsRN4L)
                .Select(u =>
                {
                    var userPayments = payments.Where(p => p.ChatId == u.ChatId).ToList();
                    var latestPending = userPayments
                        .Where(p => !p.IsConfirmed && p.ExpiresAt > now)
                        .OrderByDescending(p => p.CreatedAt)
                        .FirstOrDefault();
                    var latestExpiredRequest = userPayments
                        .Where(p => !p.IsConfirmed && p.ExpiresAt <= now)
                        .OrderByDescending(p => p.ExpiresAt)
                        .FirstOrDefault();
                    var confirmed = userPayments.FirstOrDefault(p => p.IsConfirmed);

                    string status;
                    if (u.IsRN4L || u.IsRegisteredNurse)
                        status = "active";
                    else if (latestPending != null)
                        status = "pending";
                    else if (confirmed != null)
                        status = "expired_sub";
                    else if (latestExpiredRequest != null)
                        status = "expired_request";
                    else if (!u.IsActive && userPayments.Any())
                        status = "blocked";
                    else
                        status = "expired_request";

                    var relevantPayment = latestPending ?? confirmed ?? latestExpiredRequest;

                    return new
                    {
                        chatId = u.ChatId,
                        username = u.Username,
                        firstName = u.FirstName,
                        isRN4L = u.IsRN4L,
                        isRegisteredNurse = u.IsRegisteredNurse,
                        rnExpiresAt = u.RNExpiresAt,
                        isActive = u.IsActive,
                        isOnNewBot = u.IsOnNewBot,
                        status,
                        wallet = relevantPayment?.WalletPublicKey,
                        requestedAt = relevantPayment?.CreatedAt,
                        paymentDate = confirmed?.ConfirmedAt,
                        expiresAt = latestPending?.ExpiresAt
                    };
                })
                .ToList();

            return Ok(new { status = "success", payments = rows });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching payments");
            return StatusCode(500, new { status = "error", message = "Failed to fetch payments" });
        }
    }

    [HttpPost("payments/grant")]
    public async Task<IActionResult> GrantAccess([FromBody] GrantAccessRequest request)
    {
        try
        {
            var user = await _dbContext.Users.FirstOrDefaultAsync(u => u.ChatId == request.ChatId);
            if (user == null)
                return NotFound(new { status = "error", message = "User not found" });

            if (request.IsRN4L)
            {
                user.IsRN4L = true;
                user.IsRegisteredNurse = true;
                user.RNExpiresAt = null;
            }
            else
            {
                user.IsRegisteredNurse = true;
                user.RNExpiresAt = DateTime.UtcNow.AddDays(30);
            }

            await _dbContext.SaveChangesAsync();

            var msg = request.IsRN4L
                ? "✅ You've been granted lifetime access to GROUPCHAT. Welcome to the club."
                : $"✅ You've been granted 30 days of full access (until {user.RNExpiresAt:yyyy-MM-dd}). Enjoy.";

            await _telegramService.SendPlainMessageAsync(request.ChatId, msg);

            _logger.LogInformation("Manually granted {Type} to ChatId={ChatId}", request.IsRN4L ? "RN4L" : "RN30d", request.ChatId);
            return Ok(new { status = "success" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error granting access");
            return StatusCode(500, new { status = "error", message = "Failed to grant access" });
        }
    }

    [HttpPost("payments/revoke")]
    public async Task<IActionResult> RevokeAccess([FromBody] RevokeAccessRequest request)
    {
        try
        {
            var user = await _dbContext.Users.FirstOrDefaultAsync(u => u.ChatId == request.ChatId);
            if (user == null)
                return NotFound(new { status = "error", message = "User not found" });

            user.IsRegisteredNurse = false;
            user.IsRN4L = false;
            user.RNExpiresAt = null;

            await _dbContext.SaveChangesAsync();
            await _telegramService.SendPlainMessageAsync(request.ChatId, "Your GROUPCHAT access has been revoked. Use /subscribe to resubscribe.");

            _logger.LogInformation("Manually revoked access for ChatId={ChatId}", request.ChatId);
            return Ok(new { status = "success" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error revoking access");
            return StatusCode(500, new { status = "error", message = "Failed to revoke access" });
        }
    }

    [HttpGet("config")]
    public async Task<IActionResult> GetConfig()
    {
        var configs = await _appConfig.GetAllAsync();
        return Ok(new { status = "success", configs });
    }

    // Ranks Fomo handles seen trading in our own WsEvent history that we've never
    // added as tracked Traders. EV per handle = mean forward return of their buys,
    // where "forward return" is the token's peak MarketCap in the following window
    // (any trader's events on that token) vs. MarketCap at their buy — i.e. "what if
    // you bought the instant they did and sold at the local top". Optimistic upper
    // bound (no slippage/exact-timing), but a clean relative ranking signal since
    // MarketCap-at-trade is the platform's own number, not something we estimate.
    [HttpGet("trader-ev-suggestions")]
    public async Task<IActionResult> TraderEvSuggestions([FromQuery] int minBuys = 3, [FromQuery] int windowHours = 72, [FromQuery] int topN = 50)
    {
        var swapTypes = new[] { "swap_buy", "swap_sell" };
        var events = await _dbContext.WsEvents
            .Where(e => swapTypes.Contains(e.Type) && e.UserHandle != null && e.TokenAddress != null && e.MarketCap != null && e.CreatedAt != null)
            .Select(e => new { e.Type, Handle = e.UserHandle!, e.TokenAddress, MarketCap = e.MarketCap!.Value, CreatedAt = e.CreatedAt!.Value, e.Ticker })
            .ToListAsync();

        var trackedHandles = (await _dbContext.Traders.Select(t => t.Handle).ToListAsync())
            .Select(h => h.ToLowerInvariant()).ToHashSet();

        var byToken = events.GroupBy(e => e.TokenAddress)
            .ToDictionary(g => g.Key!, g => g.OrderBy(e => e.CreatedAt).ToList());

        var window = TimeSpan.FromHours(windowHours);
        var buyMultiples = new Dictionary<string, List<(double Multiple, string Ticker)>>(StringComparer.OrdinalIgnoreCase);

        foreach (var buy in events.Where(e => e.Type == "swap_buy"))
        {
            if (buy.MarketCap <= 0) continue;
            var timeline = byToken[buy.TokenAddress!];
            var peak = timeline
                .Where(e => e.CreatedAt > buy.CreatedAt && e.CreatedAt <= buy.CreatedAt + window)
                .Select(e => (decimal?)e.MarketCap)
                .DefaultIfEmpty(null)
                .Max();

            if (peak == null) continue;
            var multiple = (double)(peak.Value / buy.MarketCap);

            if (!buyMultiples.TryGetValue(buy.Handle, out var list))
                buyMultiples[buy.Handle] = list = new List<(double, string)>();
            list.Add((multiple, buy.Ticker ?? "?"));
        }

        var results = buyMultiples
            .Where(kv => !trackedHandles.Contains(kv.Key.ToLowerInvariant()) && kv.Value.Count >= minBuys)
            .Select(kv =>
            {
                var multiples = kv.Value.Select(x => x.Multiple).OrderBy(x => x).ToList();
                var winRate = multiples.Count(m => m >= 2.0) / (double)multiples.Count;
                var avgReturn = multiples.Average(m => m - 1.0);
                var median = multiples[multiples.Count / 2];
                var topTickers = kv.Value.OrderByDescending(x => x.Multiple).Take(5).Select(x => $"{x.Ticker} ({x.Multiple:0.0}x)");
                return new
                {
                    Handle = kv.Key,
                    Buys = multiples.Count,
                    WinRate = Math.Round(winRate * 100, 1),
                    AvgReturnPct = Math.Round(avgReturn * 100, 1),
                    MedianMultiple = Math.Round(median, 2),
                    TopCalls = topTickers,
                    ProfileUrl = $"https://fomo.family/profile/{kv.Key}"
                };
            })
            .OrderByDescending(r => r.AvgReturnPct)
            .Take(topN)
            .ToList();

        return Ok(new { status = "success", windowHours, minBuys, candidatesEvaluated = buyMultiples.Count, results });
    }

    [HttpPost("config")]
    public async Task<IActionResult> SetConfig([FromBody] SetConfigRequest request)
    {
        try
        {
            await _appConfig.SetAsync(request.Key, request.Value, request.Description);
            return Ok(new { status = "success" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting config {Key}", request.Key);
            return StatusCode(500, new { status = "error", message = "Failed to save config" });
        }
    }

    [HttpPost("payments/sweep")]
    public async Task<IActionResult> SweepPayments()
    {
        try
        {
            var treasury = await _appConfig.GetTreasuryWalletAsync();
            if (string.IsNullOrWhiteSpace(treasury))
                return BadRequest(new { status = "error", message = "Treasury wallet not configured. Set TreasuryWalletAddress in Config tab." });

            var confirmed = await _dbContext.PendingPayments
                .Where(p => p.IsConfirmed)
                .ToListAsync();

            if (confirmed.Count == 0)
                return Ok(new { status = "success", swept = 0, message = "No confirmed payments to sweep." });

            var rpcClient = Solnet.Rpc.ClientFactory.GetClient("https://api.mainnet-beta.solana.com");
            var treasuryPubkey = new Solnet.Wallet.PublicKey(treasury);

            int swept = 0;
            var errors = new List<string>();

            foreach (var payment in confirmed)
            {
                try
                {
                    var privateKeyBytes = Convert.FromBase64String(payment.WalletPrivateKey);
                    var pubkey = new Solnet.Wallet.PublicKey(payment.WalletPublicKey);
                    var account = new Solnet.Wallet.Account(privateKeyBytes, pubkey.KeyBytes);

                    var balanceRes = await rpcClient.GetBalanceAsync(account.PublicKey);
                    if (!balanceRes.WasSuccessful || balanceRes.Result.Value == 0)
                        continue;

                    var lamports = balanceRes.Result.Value;
                    // Reserve ~5000 lamports for tx fee
                    var sendLamports = lamports > 5000 ? lamports - 5000 : 0;
                    if (sendLamports == 0) continue;

                    var blockHashRes = await rpcClient.GetLatestBlockHashAsync();
                    if (!blockHashRes.WasSuccessful) continue;

                    var tx = new Solnet.Rpc.Builders.TransactionBuilder()
                        .SetRecentBlockHash(blockHashRes.Result.Value.Blockhash)
                        .SetFeePayer(account.PublicKey)
                        .AddInstruction(Solnet.Programs.SystemProgram.Transfer(
                            account.PublicKey, treasuryPubkey, sendLamports))
                        .Build(account);

                    var sendRes = await rpcClient.SendTransactionAsync(tx, commitment: Solnet.Rpc.Types.Commitment.Confirmed);
                    if (sendRes.WasSuccessful)
                    {
                        swept++;
                        _logger.LogInformation("Swept {Lamports} lamports from {Wallet} → {Treasury}, sig={Sig}",
                            sendLamports, payment.WalletPublicKey, treasury, sendRes.Result);
                    }
                    else
                    {
                        errors.Add($"{payment.WalletPublicKey[..8]}: {sendRes.Reason}");
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"{payment.WalletPublicKey[..8]}: {ex.Message}");
                }
            }

            return Ok(new { status = "success", swept, errors });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sweeping payments");
            return StatusCode(500, new { status = "error", message = ex.Message });
        }
    }
}

public record GrantAccessRequest(long ChatId, bool IsRN4L);
public record RevokeAccessRequest(long ChatId);
public record SetConfigRequest(string Key, string Value, string? Description);