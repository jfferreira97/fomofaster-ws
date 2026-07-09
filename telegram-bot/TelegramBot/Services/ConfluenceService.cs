using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using TelegramBot.Data;
using TelegramBot.Models;

namespace TelegramBot.Services;

public record ConfluenceTradeEvent(
    string Type,
    string Trader,
    string? Ticker,
    string TokenAddress,
    int? NetworkId,
    decimal UsdAmount,
    decimal? MarketCap,
    DateTime OccurredAt);

/// <summary>
/// Detects confluence: N distinct followed traders buying the same token within a
/// (rolling) time window, and broadcasts a TRENDING alert to ALL users.
/// Consumes events from an in-memory channel so the notification hot path only pays
/// for a non-blocking enqueue. All window state lives in memory; rebuilt from
/// WsEvents on startup.
/// </summary>
public class ConfluenceService : BackgroundService
{
    private class TraderPosition
    {
        public string Handle = string.Empty;
        public decimal TotalUsd;
        public decimal WeightedMcSum;   // Σ usd × entry MC, for USD-weighted average entry
        public decimal McWeight;        // Σ usd of buys that carried an MC
        public int BuyCount;
        public bool Sold;
    }

    private class TokenWindow
    {
        public string TokenAddress = string.Empty;
        public string? Ticker;
        public int? NetworkId;
        public DateTime StartedAt;
        public DateTime ExpiresAt;
        public int ExtensionStage;
        public decimal? LastMarketCap;
        public Dictionary<string, TraderPosition> Traders = new(StringComparer.OrdinalIgnoreCase);
        public int AlertedTraderCount;  // 0 = never alerted; re-alert when distinct count exceeds this
        public DateTime LastAlertAt;
        public bool PendingRealert;     // re-alert due but debounced; housekeeping fires it
    }

    private record ConfluenceConfig(
        bool Enabled,
        int MinTraders,
        int WindowMinutes,
        bool RolloverEnabled,
        int[] RolloverSteps,
        int RealertDebounceSeconds);

    private static readonly TimeSpan HousekeepingInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ConfigCacheTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RebuildLookback = TimeSpan.FromHours(6);

    private readonly Channel<ConfluenceTradeEvent> _channel =
        Channel.CreateUnbounded<ConfluenceTradeEvent>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Dictionary<string, TokenWindow> _windows = new();

    private readonly ITelegramService _telegramService;
    private readonly AppConfigService _appConfig;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ConfluenceService> _logger;

    private ConfluenceConfig? _cachedConfig;
    private DateTime _configFetchedAt;

    public ConfluenceService(
        ITelegramService telegramService,
        AppConfigService appConfig,
        IServiceProvider serviceProvider,
        ILogger<ConfluenceService> logger)
    {
        _telegramService = telegramService;
        _appConfig = appConfig;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    /// <summary>Non-blocking; safe to call from the notification hot path.</summary>
    public void Enqueue(ConfluenceTradeEvent evt)
    {
        if (string.IsNullOrEmpty(evt.TokenAddress) || string.IsNullOrEmpty(evt.Trader))
            return;
        _channel.Writer.TryWrite(evt);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            await RebuildStateAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Confluence state rebuild failed, starting with empty state");
        }

        Task<ConfluenceTradeEvent>? pendingRead = null;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                pendingRead ??= _channel.Reader.ReadAsync(ct).AsTask();
                var completed = await Task.WhenAny(pendingRead, Task.Delay(HousekeepingInterval, ct));
                if (completed == pendingRead)
                {
                    var evt = await pendingRead;
                    pendingRead = null;
                    await ProcessEventAsync(evt, DateTime.UtcNow, suppressAlerts: false, ct);
                }
                await HousekeepingAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if (pendingRead is { IsCompleted: true }) pendingRead = null;
                _logger.LogError(ex, "Confluence processing error");
            }
        }
    }

    private async Task ProcessEventAsync(ConfluenceTradeEvent evt, DateTime now, bool suppressAlerts, CancellationToken ct)
    {
        var config = await GetConfigAsync();
        if (!config.Enabled) return;

        var isBuy = evt.Type == "swap_buy";
        var isSell = evt.Type is "swap_sell" or "swap_withdraw" or "transfer_out";
        if (!isBuy && !isSell) return;

        _windows.TryGetValue(evt.TokenAddress, out var window);
        if (window != null && window.ExpiresAt <= now)
        {
            _windows.Remove(evt.TokenAddress);
            window = null;
        }

        if (isSell)
        {
            // Sells never extend the window; they only flip the trader's bubble to red.
            if (window != null && window.Traders.TryGetValue(evt.Trader, out var seller))
                seller.Sold = true;
            return;
        }

        if (window == null)
        {
            window = new TokenWindow
            {
                TokenAddress = evt.TokenAddress,
                StartedAt = now,
                ExpiresAt = now.AddMinutes(config.WindowMinutes)
            };
            _windows[evt.TokenAddress] = window;
        }

        if (!string.IsNullOrEmpty(evt.Ticker)) window.Ticker = evt.Ticker;
        window.NetworkId ??= evt.NetworkId;
        if (evt.MarketCap.HasValue) window.LastMarketCap = evt.MarketCap;

        if (!window.Traders.TryGetValue(evt.Trader, out var position))
        {
            position = new TraderPosition { Handle = evt.Trader };
            window.Traders[evt.Trader] = position;

            // Only a NEW distinct trader (after the window opener) extends the window.
            if (window.Traders.Count > 1 && config.RolloverEnabled)
            {
                var steps = config.RolloverSteps;
                var stepMinutes = steps.Length == 0
                    ? config.WindowMinutes
                    : steps[Math.Min(window.ExtensionStage, steps.Length - 1)];
                window.ExtensionStage++;
                var candidate = now.AddMinutes(stepMinutes);
                if (candidate > window.ExpiresAt) window.ExpiresAt = candidate;
            }
        }

        position.TotalUsd += evt.UsdAmount;
        position.BuyCount++;
        position.Sold = false; // re-buy after a sell counts as holding again
        if (evt.MarketCap is { } mc && evt.UsdAmount > 0)
        {
            position.WeightedMcSum += evt.UsdAmount * mc;
            position.McWeight += evt.UsdAmount;
        }

        if (!suppressAlerts)
            await MaybeAlertAsync(window, config, now, ct);
    }

    private async Task MaybeAlertAsync(TokenWindow window, ConfluenceConfig config, DateTime now, CancellationToken ct)
    {
        var count = window.Traders.Count;
        if (count < config.MinTraders || count <= window.AlertedTraderCount)
            return;

        var isRealert = window.AlertedTraderCount > 0;
        if (isRealert && now - window.LastAlertAt < TimeSpan.FromSeconds(config.RealertDebounceSeconds))
        {
            window.PendingRealert = true;
            return;
        }

        await FireAlertAsync(window, count, ct);
        window.AlertedTraderCount = count;
        window.LastAlertAt = DateTime.UtcNow;
        window.PendingRealert = false;
    }

    private async Task FireAlertAsync(TokenWindow window, int count, CancellationToken ct)
    {
        var ticker = string.IsNullOrEmpty(window.Ticker) ? "Token" : window.Ticker;
        var mcPart = window.LastMarketCap.HasValue
            ? $" - ${FormatMarketCap(window.LastMarketCap.Value)} MC"
            : "";
        var header = $"🔥 {ticker} is TRENDING ({count} traders){mcPart}";

        var entries = window.Traders.Values
            .OrderByDescending(p => p.TotalUsd)
            .Select(p =>
            {
                var bubble = p.Sold ? "🔴" : "🟢";
                var entryPart = "";
                if (p.McWeight > 0)
                {
                    var avgMc = p.WeightedMcSum / p.McWeight;
                    var avgLabel = p.BuyCount > 1 ? "avg " : "";
                    entryPart = $" @ {avgLabel}${FormatMarketCap(avgMc)} MC";
                }
                return $"{p.Handle} {bubble} ${p.TotalUsd:N0}{entryPart}";
            });

        var message = header + "\n" + string.Join(" | ", entries);

        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.ConfluenceAlerts.Add(new ConfluenceAlert
            {
                TokenAddress = window.TokenAddress,
                Ticker = window.Ticker,
                NetworkId = window.NetworkId,
                WindowStartedAt = window.StartedAt,
                TraderCount = count,
                TotalUsd = window.Traders.Values.Sum(p => p.TotalUsd),
                FiredAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync(ct);
        }

        var chain = window.NetworkId.HasValue ? ChainInfo.FromNetworkId(window.NetworkId.Value) : null;

        _logger.LogInformation("🔥 TRENDING alert: {Ticker} ({Token}) with {Count} traders", ticker, window.TokenAddress, count);

        // traderHandle deliberately null: trending alerts broadcast to ALL active users,
        // not just followers of the buying traders.
        await _telegramService.SendNotificationToAllUsersAsync(
            new NotificationRequest { Message = message },
            contractAddress: window.TokenAddress,
            chain: chain,
            traderHandle: null,
            ticker: window.Ticker,
            marketCap: window.LastMarketCap.HasValue ? (double)window.LastMarketCap.Value : null,
            notificationType: NotificationType.CUSTOM_Trending);
    }

    private async Task HousekeepingAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        List<string>? expired = null;
        foreach (var (key, window) in _windows)
            if (window.ExpiresAt <= now)
                (expired ??= new List<string>()).Add(key);
        if (expired != null)
        {
            foreach (var key in expired)
            {
                _logger.LogDebug("Confluence window expired for {Token} ({Count} traders, never realerted past {Alerted})",
                    key, _windows[key].Traders.Count, _windows[key].AlertedTraderCount);
                _windows.Remove(key);
            }
        }

        var pending = _windows.Values.Where(w => w.PendingRealert).ToList();
        if (pending.Count == 0) return;

        var config = await GetConfigAsync();
        foreach (var window in pending)
        {
            if (!config.Enabled)
            {
                window.PendingRealert = false;
                continue;
            }
            // Re-checks debounce internally; leaves PendingRealert set if still too soon.
            await MaybeAlertAsync(window, config, now, ct);
        }
    }

    private async Task RebuildStateAsync(CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow - RebuildLookback;

        List<WsEvent> events;
        List<ConfluenceAlert> alerts;
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var swapTypes = new[] { "swap_buy", "swap_sell", "swap_withdraw", "transfer_out" };
            events = await db.WsEvents
                .Where(e => swapTypes.Contains(e.Type)
                    && e.ReceivedAt >= cutoff
                    && e.TokenAddress != null
                    && e.UserHandle != null)
                .OrderBy(e => e.ReceivedAt)
                .ToListAsync(ct);
            alerts = await db.ConfluenceAlerts
                .Where(a => a.FiredAt >= cutoff)
                .ToListAsync(ct);
        }

        foreach (var e in events)
        {
            var evt = new ConfluenceTradeEvent(
                Type: e.Type,
                Trader: e.UserHandle!,
                Ticker: e.Ticker,
                TokenAddress: e.TokenAddress!,
                NetworkId: e.NetworkId,
                UsdAmount: e.UsdAmount ?? 0,
                MarketCap: e.MarketCap,
                OccurredAt: e.ReceivedAt);
            await ProcessEventAsync(evt, e.ReceivedAt, suppressAlerts: true, ct);
        }

        var now = DateTime.UtcNow;
        foreach (var key in _windows.Where(kv => kv.Value.ExpiresAt <= now).Select(kv => kv.Key).ToList())
            _windows.Remove(key);

        // Restore alert state so live windows don't re-send alerts already fired before restart.
        foreach (var alert in alerts)
        {
            if (_windows.TryGetValue(alert.TokenAddress, out var w) && alert.FiredAt >= w.StartedAt)
            {
                w.AlertedTraderCount = Math.Max(w.AlertedTraderCount, alert.TraderCount);
                if (alert.FiredAt > w.LastAlertAt) w.LastAlertAt = alert.FiredAt;
            }
        }

        _logger.LogInformation("Confluence state rebuilt: {Windows} live window(s) from {Events} event(s)",
            _windows.Count, events.Count);
    }

    private async Task<ConfluenceConfig> GetConfigAsync()
    {
        if (_cachedConfig != null && DateTime.UtcNow - _configFetchedAt < ConfigCacheTtl)
            return _cachedConfig;

        var enabled = await _appConfig.GetAsync("ConfluenceEnabled");
        var minTraders = await _appConfig.GetAsync("ConfluenceMinTraders");
        var windowMinutes = await _appConfig.GetAsync("ConfluenceWindowMinutes");
        var rolloverEnabled = await _appConfig.GetAsync("ConfluenceRolloverEnabled");
        var rolloverSteps = await _appConfig.GetAsync("ConfluenceRolloverSteps");
        var debounce = await _appConfig.GetAsync("ConfluenceRealertDebounceSeconds");

        _cachedConfig = new ConfluenceConfig(
            Enabled: !string.Equals(enabled, "false", StringComparison.OrdinalIgnoreCase),
            MinTraders: int.TryParse(minTraders, out var mt) && mt > 1 ? mt : 3,
            WindowMinutes: int.TryParse(windowMinutes, out var wm) && wm > 0 ? wm : 30,
            RolloverEnabled: !string.Equals(rolloverEnabled, "false", StringComparison.OrdinalIgnoreCase),
            RolloverSteps: (rolloverSteps ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => int.TryParse(s, out var v) ? v : 0)
                .Where(v => v > 0)
                .ToArray(),
            RealertDebounceSeconds: int.TryParse(debounce, out var db) && db >= 0 ? db : 60);
        _configFetchedAt = DateTime.UtcNow;
        return _cachedConfig;
    }

    private static string FormatMarketCap(decimal mc) => mc switch
    {
        >= 1_000_000_000 => $"{mc / 1_000_000_000:0.##}b",
        >= 1_000_000     => $"{mc / 1_000_000:0.##}m",
        _                => $"{mc / 1_000:0.##}k"
    };
}
