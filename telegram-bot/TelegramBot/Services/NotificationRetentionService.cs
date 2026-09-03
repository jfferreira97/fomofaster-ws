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
}
