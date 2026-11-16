using Microsoft.EntityFrameworkCore;
using TelegramBot.Data;

namespace TelegramBot.Services;

// Notifications/SentMessages exist only to serve the repeat-window throttle (looks back at
// most 7 days — RepeatWindowMinutes hard-caps at 10080 in /repeatwindow) and the dashboard's
// "recent notifications" view (only ever shows the last ~100). Nothing ever needs history
// older than that, so nothing ever deleted it either — this grew to 247k Notifications /
// 17.5M SentMessages before anyone noticed. Runs periodically forever so it can't happen again.
public class NotificationRetentionService : BackgroundService
{
    private static readonly TimeSpan RunInterval = TimeSpan.FromHours(6);
    // Strictly beyond the 7-day (10080min) max a user can set for RepeatWindowMinutes, with
    // a day of margin — never touches a row the throttle check could still need.
    private static readonly TimeSpan RetentionWindow = TimeSpan.FromDays(8);
    private const int BatchSize = 500;

    // Followed traders' pump.fun trades (PumpEvents Kind = 'trade') are ~90% of that table, about 20 MB a day. The longest
    // look back at them is 60 days (the accumulation trades chart; the accumulation tab itself goes 30), so 90 days leaves
    // a month of margin. The "still holding" figure on a sell alert reads all of a trader's trades on the coin: a bag
    // bought more than 90 days ago is understated, as one bought before we followed them already is. Callouts, replies
    // and reposts (the trader ratings' input, ~1.5 MB a day) are kept.
    private static readonly TimeSpan PumpTradeRetention = TimeSpan.FromDays(90);

    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<NotificationRetentionService> _logger;

    public NotificationRetentionService(IServiceProvider serviceProvider, ILogger<NotificationRetentionService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("NotificationRetentionService started (window={Days}d, interval={Hours}h)", RetentionWindow.TotalDays, RunInterval.TotalHours);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PurgeOldNotificationsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during notification retention purge");
            }

            try
            {
                await PurgeOldPumpTradesAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during pump trade retention purge");
            }

            try { await Task.Delay(RunInterval, stoppingToken); } catch (OperationCanceledException) { break; }
        }
    }

    // Deletes in small batches with a short pause between them — the initial backlog is large
    // enough that one giant DELETE would hold a write lock for a long stretch on a live DB.
    // SentMessages cascades away automatically via its FK to Notifications; no separate delete.
    private async Task PurgeOldNotificationsAsync(CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow - RetentionWindow;
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var totalDeleted = 0;
        while (!ct.IsCancellationRequested)
        {
            var batchIds = await db.Notifications
                .Where(n => n.SentAt < cutoff)
                .OrderBy(n => n.SentAt)
                .Take(BatchSize)
                .Select(n => n.Id)
                .ToListAsync(ct);

            if (batchIds.Count == 0) break;

            totalDeleted += await db.Notifications
                .Where(n => batchIds.Contains(n.Id))
                .ExecuteDeleteAsync(ct);

            await Task.Delay(200, ct);
        }

        if (totalDeleted > 0)
            _logger.LogInformation("Notification retention: purged {Count} notification(s) older than {Cutoff:u}", totalDeleted, cutoff);
    }

    // Oldest first through the Kind index in Id order (Ids follow ReceivedAt, checked on the live table), so it reads only
    // the rows it deletes plus one batch, never the whole table: ReceivedAt has no index. Stops at the first batch that
    // holds a row inside the window.
    private async Task PurgeOldPumpTradesAsync(CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow - PumpTradeRetention;
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var totalDeleted = 0;
        while (!ct.IsCancellationRequested)
        {
            var batch = await db.PumpEvents.AsNoTracking()
                .Where(e => e.Kind == "trade")
                .OrderBy(e => e.Id)
                .Take(BatchSize)
                .Select(e => new { e.Id, e.ReceivedAt })
                .ToListAsync(ct);
            var oldIds = batch.Where(e => e.ReceivedAt < cutoff).Select(e => e.Id).ToList();
            if (oldIds.Count == 0) break;

            totalDeleted += await db.PumpEvents
                .Where(e => oldIds.Contains(e.Id))
                .ExecuteDeleteAsync(ct);

            if (oldIds.Count < batch.Count) break;
            await Task.Delay(200, ct);
        }

        if (totalDeleted > 0)
            _logger.LogInformation("Pump trade retention: purged {Count} trade(s) older than {Cutoff:u}", totalDeleted, cutoff);
    }
}
