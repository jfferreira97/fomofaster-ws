using Microsoft.EntityFrameworkCore;
using TelegramBot.Data;
using TelegramBot.Models;

namespace TelegramBot.Services;

// In-memory mirror of active Users for the notification hot path — avoids a DB round trip on
// every single notification send, same reasoning as ChainSettingsCache. Refreshed on a timer
// rather than kept live-synced: a couple minutes of staleness on "who's active"/"when did they
// last interact" doesn't matter here the way it would for a hard on/off setting, so this stays
// a plain periodic refresh instead of ChainSettingsCache's write-through pattern.
public class ActiveUserCache : BackgroundService
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(2);

    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ActiveUserCache> _logger;
    private volatile List<User> _snapshot = new();

    public ActiveUserCache(IServiceProvider serviceProvider, ILogger<ActiveUserCache> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    // Zero-DB-query read for the notification hot path.
    public List<User> GetActiveUsers() => _snapshot;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RefreshAsync(stoppingToken); // populate before the first notification can arrive
        _logger.LogInformation("ActiveUserCache loaded: {Count} active user(s)", _snapshot.Count);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(RefreshInterval, stoppingToken); } catch (OperationCanceledException) { break; }
            try { await RefreshAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogError(ex, "Error refreshing active user cache"); }
        }
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        _snapshot = await db.Users.AsNoTracking().Where(u => u.IsActive).ToListAsync(ct);
    }
}
