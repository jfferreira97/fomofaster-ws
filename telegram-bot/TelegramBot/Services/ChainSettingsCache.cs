using Microsoft.EntityFrameworkCore;
using TelegramBot.Data;
using TelegramBot.Models;

namespace TelegramBot.Services;

// In-memory mirror of UserChainSettings, kept for the notification hot path
// (TelegramService.SendNotificationToAllUsersAsync fires per trade — it must never wait on a
// DB round trip just to check chain preferences). Loaded once at startup; every write goes
// through ChainSettingsService, which updates this cache immediately after saving to the DB,
// so a /chains toggle takes effect on the very next notification.
public class ChainSettingsCache
{
    private static readonly Dictionary<int, UserChainSetting> Empty = new();

    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ChainSettingsCache> _logger;
    private readonly Dictionary<Chain, ConcurrentReadDictionary> _byChain = new();

    public ChainSettingsCache(IServiceProvider serviceProvider, ILogger<ChainSettingsCache> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    private volatile bool _loaded;

    public async Task LoadAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var all = await dbContext.UserChainSettings.AsNoTracking().ToListAsync();

        foreach (var group in all.GroupBy(s => s.Chain))
        {
            _byChain[group.Key] = new ConcurrentReadDictionary(group.ToDictionary(s => s.UserId));
        }

        _loaded = true;
        _logger.LogInformation("ChainSettingsCache loaded: {Count} setting row(s) across {ChainCount} chain(s)", all.Count, _byChain.Count);
    }

    // Zero-DB-query read for the notification hot path. Fails open (empty = "no overrides,
    // deliver to everyone") if called before LoadAsync completes, rather than blocking sends.
    public IReadOnlyDictionary<int, UserChainSetting> GetForChain(Chain chain)
    {
        if (!_loaded) return Empty;
        return _byChain.TryGetValue(chain, out var forChain) ? forChain.Snapshot : Empty;
    }

    public void Upsert(UserChainSetting setting)
    {
        if (!_byChain.TryGetValue(setting.Chain, out var forChain))
        {
            forChain = new ConcurrentReadDictionary(new Dictionary<int, UserChainSetting>());
            _byChain[setting.Chain] = forChain;
        }
        forChain.Set(setting.UserId, setting);
    }

    // Copy-on-write wrapper: readers hold a plain Dictionary snapshot (safe to enumerate/index
    // without locking) while writes replace the whole snapshot under a lock. Writes are rare
    // (a user tapping a /chains button); reads happen on every notification send, so they must
    // stay lock-free.
    private sealed class ConcurrentReadDictionary
    {
        private readonly object _writeLock = new();
        private volatile Dictionary<int, UserChainSetting> _snapshot;

        public ConcurrentReadDictionary(Dictionary<int, UserChainSetting> initial) => _snapshot = initial;

        public Dictionary<int, UserChainSetting> Snapshot => _snapshot;

        public void Set(int userId, UserChainSetting setting)
        {
            lock (_writeLock)
            {
                var next = new Dictionary<int, UserChainSetting>(_snapshot) { [userId] = setting };
                _snapshot = next;
            }
        }
    }
}
