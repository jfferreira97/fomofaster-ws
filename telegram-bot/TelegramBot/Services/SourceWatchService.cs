using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;
using TelegramBot.Data;
using TelegramBot.Models;

namespace TelegramBot.Services;

// A cookie a sidecar reported: name, domain and expiry only (the value never leaves the sidecar).
public record SidecarCookie(string Name, string Domain, DateTime ExpiresAt);

// What a sidecar said about itself with its last heartbeat. A sidecar that sends nothing only counts by its heartbeat.
public record SidecarReport(DateTime At, string State, DateTime? Since, string? Detail, IReadOnlyList<SidecarCookie> Cookies);

// One alert source the watchdog looks after. Silence = the longest gap between two events that is still normal: measured over
// 14 days in November 2026, pump.fun never went quiet for more than ~4 min and FOMO for ~20 min (early mornings) outside real outages.
public sealed record WatchedSource(string Name, string Label, string HeartbeatSource, string Site, TimeSpan Silence, string[] LoginCookies);

// The watchdog's verdict on one source. Kind names the problem in a stable way (the texts carry durations that change).
public record SourceVerdict(string Name, string Label, string State, string? Kind, string? Problem, string? Fix, DateTime? ProblemSince,
    DateTime? LastEvent, DateTime? LastBeat, DateTime? LoginExpiresAt, string? LoginCookie, bool LoginEstimated, IReadOnlyList<SidecarCookie> Cookies);

// Watches the FOMO and pump.fun alert sources and warns the owner on Telegram (admin bot) when one of them stops: a sidecar that
// says it is logged out or has no live connection, a sidecar that stopped, or a source silent for longer than it ever is in normal
// running. It says so again every 2 hours while it lasts, and once more when it is back. A few days before a login runs out it
// warns once a day, so the login can be made at a good moment instead of after a night without alerts.
// Why: on 2026-11-18 both logins ran out (30 days after they were made) and no alert went out for 8-10 hours while every heartbeat,
// and so the Monitoring tab, said all was well.
public class SourceWatchService : BackgroundService
{
    public static readonly WatchedSource[] Sources =
    {
        // Login cookies, as the sidecars reported them on 2026-11-18 after a login at 22:42: both sites log in through Privy, whose
        // session cookies run exactly 30 days (to 2026-12-18 22:42), the life the pump.fun login really had (2026-10-19 -> 11-18).
        // Short-lived tokens the page renews by itself (privy-token 1 h, pump.fun's auth_token 14 days) are left out on purpose.
        new("fomo", "👀 FOMO (account 2)", "fomo-ws-sidecar", "fomo.family", TimeSpan.FromMinutes(30), new[] { "privy-refresh-token", "privy-session" }),
        new("pump", "💊 pump.fun", "pump-ws-sidecar", "pump.fun", TimeSpan.FromMinutes(10), new[] { "privy-session", "privy-refresh-token" }),
    };

    private static readonly TimeSpan Every = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ConfirmFor = TimeSpan.FromSeconds(60);   // a problem must last this long before the owner hears of it
    private static readonly TimeSpan RemindEvery = TimeSpan.FromHours(2);
    private static readonly TimeSpan WarnLoginBefore = TimeSpan.FromDays(3);
    private static readonly TimeSpan AssumedLoginLife = TimeSpan.FromDays(30);  // what pump.fun's login lasted (2026-10-19 -> 2026-11-18)

    private const string Relogin = "log in again in the sidecar's own Chrome window on Desktop 2 (FOMOFASTER); the sidecar picks the login up by itself";
    private const string Restart = "start it again: C:\\apps\\tools\\start-project.ps1 -Only ff";

    private readonly IServiceScopeFactory _scopes;
    private readonly MonitorStore _monitor;
    private readonly AppConfigService _config;
    private readonly TelegramSettings _settings;
    private readonly ILogger<SourceWatchService> _logger;

    // One outage of one source. Its kind can change while it lasts (logged out, then silent until the first trade after the new login):
    // that is still the same outage, told once, with one "back" at the end.
    private sealed class Alarm
    {
        public string Kind = "";
        public string Text = "";          // the problem as the owner was told it
        public DateTime FirstSeen;        // when the outage began
        public DateTime? NotifiedAt;      // when the owner was last told (null = not yet)
        public bool WasLogin;             // a login problem was part of it: when it clears, a new login was made
        public bool BackPending;          // over, but the "back" message has not gone out yet
        public int BackTries;
        public DateTime NextTryAt;        // after a failed send, wait before trying again (no 429 every 30 s)
    }
    private readonly Dictionary<string, Alarm> _alarms = new();
    private readonly Dictionary<string, DateTime> _warnNextTry = new();
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(5);
    private DateTime _lastTick = DateTime.MinValue;
    private DateTime _resumedAt = DateTime.MinValue;   // after the machine slept (a long gap between checks), silence counts from here

    public SourceWatchService(IServiceScopeFactory scopes, MonitorStore monitor, AppConfigService config, IOptions<TelegramSettings> settings,
        ILogger<SourceWatchService> logger)
    {
        _scopes = scopes;
        _monitor = monitor;
        _config = config;
        _settings = settings.Value;
        _logger = logger;
    }

    // ---- the verdict (pure, so it can be tested without a database or a clock) ----

    public static (DateTime? At, string? Cookie, bool Estimated) LoginExpiry(WatchedSource s, SidecarReport? report, DateTime? loginAt)
    {
        var cookie = report?.Cookies
            .Where(c => s.LoginCookies.Contains(c.Name, StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(c => c.ExpiresAt)
            .FirstOrDefault();
        if (cookie != null) return (cookie.ExpiresAt, cookie.Name, false);
        if (loginAt != null) return (loginAt.Value + AssumedLoginLife, null, true);
        return (null, null, false);
    }

    public static SourceVerdict Judge(WatchedSource s, DateTime now, DateTime backendStartedAt, DateTime? lastBeat, SidecarReport? report,
        DateTime? lastEvent, DateTime? loginAt)
    {
        var (loginExpires, loginCookie, estimated) = LoginExpiry(s, report, loginAt);
        var cookies = report?.Cookies ?? Array.Empty<SidecarCookie>();
        SourceVerdict V(string state, string? kind = null, string? problem = null, string? fix = null, DateTime? since = null) =>
            new(s.Name, s.Label, state, kind, problem, fix, since, lastEvent, lastBeat, loginExpires, loginCookie, estimated, cookies);

        if (lastBeat == null)
            return now - backendStartedAt < TimeSpan.FromMinutes(3)
                ? V("idle")
                : V("down", "no_heartbeat", "the sidecar is not running: no heartbeat since the backend started", Restart, backendStartedAt);
        if (now - lastBeat.Value > TimeSpan.FromMinutes(3))
            return V("down", "stopped", $"the sidecar stopped: its last heartbeat was {Ago(now - lastBeat.Value)}", Restart, lastBeat);

        // What the sidecar says about itself counts only while it is fresh (an old report from before a restart says nothing).
        if (report != null && now - report.At < TimeSpan.FromMinutes(3))
        {
            if (report.State == "logged_out")
                return V("down", "logged_out", $"{s.Site} says we are logged out" + (report.Detail != null ? $" ({report.Detail})" : ""), Relogin, report.Since ?? report.At);
            if (report.State == "no_connection")
                return V("down", "no_connection", $"the {s.Site} page has no live connection, usually because the login ran out", Relogin, report.Since ?? report.At);
        }

        // Silence: counted from the last event, or from the backend's start if that is later (a restart is not the source's fault).
        var quietFrom = lastEvent is DateTime le && le > backendStartedAt ? le : backendStartedAt;
        if (now - quietFrom > s.Silence)
            return V("down", "silent", $"no {s.Site} trades for {Left(now - quietFrom)} (normally never more than {s.Silence.TotalMinutes:0} min)",
                "look at the sidecar's window on Desktop 2 and its log in C:\\apps\\logs; logging in again fixes it when the login ran out", quietFrom);

        if (loginExpires is DateTime exp && exp - now < WarnLoginBefore)
            return V("degraded", "login_expiring",
                exp <= now ? $"the {s.Site} login should have run out already" : $"the {s.Site} login runs out in {Left(exp - now)}" + (estimated ? " (estimated: 30 days after the last login we saw)" : ""),
                Relogin, null);

        return V("ok");
    }

    // ---- live evaluation ----

    public async Task<List<SourceVerdict>> EvaluateAsync(CancellationToken ct = default)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var lastWs = await db.WsEvents.AsNoTracking().OrderByDescending(e => e.Id).Select(e => (DateTime?)e.ReceivedAt).FirstOrDefaultAsync(ct);
        var lastPump = await db.PumpEvents.AsNoTracking().OrderByDescending(e => e.Id).Select(e => (DateTime?)e.ReceivedAt).FirstOrDefaultAsync(ct);
        var now = DateTime.UtcNow;
        var watchingSince = _resumedAt > _monitor.StartedAt ? _resumedAt : _monitor.StartedAt;
        var list = new List<SourceVerdict>();
        foreach (var s in Sources)
        {
            var beat = _monitor.GetBeat(s.HeartbeatSource);
            var loginAt = ParseUtc(await _config.GetAsync("LoginSeenAt:" + s.Name));
            list.Add(Judge(s, now, watchingSince, beat?.Last, _monitor.GetReport(s.HeartbeatSource), s.Name == "fomo" ? lastWs : lastPump, loginAt));
        }
        return list;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Give the sidecars time to send their first heartbeats after a backend start.
        try { await Task.Delay(TimeSpan.FromSeconds(90), stoppingToken); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            // Only a real shutdown ends the loop: anything else (an HTTP timeout is a TaskCanceledException too) must not stop the host.
            try { await TickAsync(stoppingToken); }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested) { _logger.LogWarning(ex, "Source watch check failed"); }
            try { await Task.Delay(Every, stoppingToken); } catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        if (_lastTick != DateTime.MinValue && now - _lastTick > TimeSpan.FromMinutes(2))
        {
            // The machine slept or the clock jumped: nothing was watched in between, so the counts start again from now.
            _resumedAt = now;
            foreach (var a in _alarms.Values) if (a.NotifiedAt == null) a.FirstSeen = now;
        }
        _lastTick = now;

        foreach (var v in await EvaluateAsync(ct))
        {
            _alarms.TryGetValue(v.Name, out var alarm);
            if (v.State == "down")
            {
                if (alarm == null)
                {
                    alarm = new Alarm { FirstSeen = v.ProblemSince is DateTime ps && ps < now ? ps : now };
                    _alarms[v.Name] = alarm;
                }
                alarm.BackPending = false;   // down again before the "back" went out: the same outage goes on, nothing new to say
                alarm.Kind = v.Kind!;
                alarm.WasLogin |= v.Kind is "logged_out" or "no_connection";
                if (now < alarm.NextTryAt) continue;

                string? message = null;
                if (alarm.NotifiedAt == null && now - alarm.FirstSeen >= ConfirmFor)
                    message = $"🚨 <b>{Esc(v.Label)}: no alerts are coming in</b>\n\n{Esc(v.Problem!)}, since {Local(alarm.FirstSeen)}.\n\n<b>Fix:</b> {Esc(v.Fix ?? "")}";
                else if (alarm.NotifiedAt is DateTime told && now - told >= RemindEvery)
                    message = $"🚨 <b>{Esc(v.Label)}: still no alerts</b>, for {Left(now - alarm.FirstSeen)} now\n\n{Esc(v.Problem!)}.\n\n<b>Fix:</b> {Esc(v.Fix ?? "")}";
                if (message == null) continue;
                if (await TellOwnerAsync(message, ct)) { alarm.NotifiedAt = now; alarm.Text = v.Problem!; }
                else alarm.NextTryAt = now + RetryAfter;
                continue;
            }

            if (alarm != null && !alarm.BackPending)
            {
                if (alarm.NotifiedAt == null) { _alarms.Remove(v.Name); alarm = null; }   // never told: nothing to take back
                else
                {
                    alarm.BackPending = true;
                    alarm.NextTryAt = DateTime.MinValue;
                    // A told login problem that cleared means someone logged in just now: the start of the next login's life.
                    if (alarm.WasLogin)
                        await SaveAsync("LoginSeenAt:" + v.Name, now.ToString("o"), "When the watchdog last saw this source come back after a login problem (UTC). It assumes the login lasts 30 days when the sidecar reports no login cookie.");
                }
            }
            if (alarm is { BackPending: true } && now >= alarm.NextTryAt)
            {
                var sent = await TellOwnerAsync($"✅ <b>{Esc(v.Label)} is back</b>: alerts are coming in again after {Left(now - alarm.FirstSeen)} (it was: {Esc(alarm.Text)}).", ct);
                if (sent || ++alarm.BackTries >= 5) _alarms.Remove(v.Name);
                else alarm.NextTryAt = now + RetryAfter;
            }

            // A login about to run out: one warning a day, in waking hours, so it can be renewed at a good moment.
            var hour = DateTime.Now.Hour;
            if (v.Kind == "login_expiring" && hour >= 9 && hour < 22 && now >= _warnNextTry.GetValueOrDefault(v.Name))
            {
                var key = "LoginWarnedOn:" + v.Name;
                var today = DateTime.Now.ToString("yyyy-MM-dd");
                if (await _config.GetAsync(key) != today)
                {
                    if (await TellOwnerAsync($"⏳ <b>{Esc(v.Label)}</b>: {Esc(v.Problem!)}" + (v.LoginExpiresAt is DateTime exp ? $" (around {Local(exp)})" : "") +
                                             $".\n\nWhen it suits you, {Esc(Relogin)}. Until then nothing changes.", ct))
                        await SaveAsync(key, today, "The day the watchdog last warned the owner that this source's login is about to run out (one warning a day).");
                    else _warnNextTry[v.Name] = now + RetryAfter;
                }
            }
        }
    }

    private async Task SaveAsync(string key, string value, string description)
    {
        try { await _config.SetAsync(key, value, description); }
        catch (Exception ex) { _logger.LogWarning(ex, "Source watch: could not save {Key}", key); }
    }

    // ---- telling the owner ----

    private async Task<bool> TellOwnerAsync(string html, CancellationToken ct)
    {
        long chatId;
        using (var scope = _scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var owner = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == _settings.OwnerUserId, ct);
            if (owner == null) { _logger.LogWarning("Source watch: no owner (user {Id}) to warn", _settings.OwnerUserId); return false; }
            chatId = owner.ChatId;
        }
        // The admin bot first (the owner's own channel); the main bot if that fails (it may be under flood control for the owner).
        foreach (var token in new[] { _settings.AdminBotToken, _settings.BotToken }.Where(t => !string.IsNullOrEmpty(t)))
        {
            try
            {
                await new TelegramBotClient(token).SendTextMessageAsync(chatId: chatId, text: html, parseMode: ParseMode.Html,
                    disableWebPagePreview: true, cancellationToken: ct);
                _logger.LogInformation("Source watch: told the owner: {Text}", html.Replace('\n', ' '));
                return true;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Source watch: could not tell the owner through one bot");
            }
        }
        return false;
    }

    // ---- small helpers ----

    private static string Esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    private static string Local(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString("d MMM HH:mm");
    public static string Ago(TimeSpan t) => Left(t) + " ago";
    public static string Left(TimeSpan t) =>
        t.TotalSeconds < 90 ? $"{t.TotalSeconds:0} s" : t.TotalMinutes < 90 ? $"{t.TotalMinutes:0} min" : t.TotalHours < 48 ? $"{t.TotalHours:0.#} h" : $"{t.TotalDays:0.#} days";
    private static DateTime? ParseUtc(string? s) =>
        DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d : null;
}
