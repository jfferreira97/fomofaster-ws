using System.Data;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using TelegramBot.Data;

namespace TelegramBot.Services;

// A history of what each logged-in person does on the website, by Telegram chat id: opening and leaving the feed (with how long),
// opening the manage page, and every change made there (follow, unfollow, limits, categories, chains, settings, suggestions).
// Anonymous visitors of the public demo are not recorded here (they have no chat id); the cookie-less WebEvents table covers them.
// Writes go through one queue and one writer, in batches, so the website can never slow the alert fan-out down or lock the database.
// Rows older than 90 days are deleted. The table is created with raw SQL, like WebEvents (no EF migration).
public sealed record ActivityRow(string At, string Kind, string? Detail, int? DurationSec);
public sealed record ActivitySummary(long ChatId, string LastSeen, int FeedOpens, int FeedSeconds, int ManageOpens, int Actions, int Clicks);

public class UserActivityService
{
    private const int RetentionDays = 90;
    private sealed record Pending(DateTime At, long ChatId, string Kind, string? Detail, int? DurationSec);

    private readonly IServiceProvider _services;
    private readonly ILogger<UserActivityService> _logger;
    private readonly Channel<Pending> _queue = Channel.CreateBounded<Pending>(new BoundedChannelOptions(5000) { FullMode = BoundedChannelFullMode.DropOldest });
    private DateTime _purgedAt = DateTime.MinValue;
    private bool _started;

    public UserActivityService(IServiceProvider services, ILogger<UserActivityService> logger)
    {
        _services = services;
        _logger = logger;
    }

    // Everything that is not an "action" of the person (they only looked, or it is a heartbeat or a click on a link).
    private const string NotAnAction = "'feed_open','feed_close','feed_alive','manage_open','view_trader','login'";

    public async Task EnsureSchemaAsync()
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.ExecuteSqlRawAsync(@"CREATE TABLE IF NOT EXISTS ""UserActivity"" (
            ""Id"" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            ""At"" TEXT NOT NULL, ""ChatId"" INTEGER NOT NULL, ""Kind"" TEXT NOT NULL,
            ""Detail"" TEXT NULL, ""DurationSec"" INTEGER NULL)");
        await db.Database.ExecuteSqlRawAsync(@"CREATE INDEX IF NOT EXISTS ""IX_UserActivity_ChatId_At"" ON ""UserActivity"" (""ChatId"", ""At"")");
        await db.Database.ExecuteSqlRawAsync(@"CREATE INDEX IF NOT EXISTS ""IX_UserActivity_At"" ON ""UserActivity"" (""At"")");
        if (!_started) { _started = true; _ = Task.Run(WriterLoopAsync); }
    }

    // Never throws and never waits: the website calls this and moves on.
    public void Log(long chatId, string kind, string? detail = null, int? durationSec = null)
    {
        if (detail is { Length: > 600 }) detail = detail[..600];
        _queue.Writer.TryWrite(new Pending(DateTime.UtcNow, chatId, kind, detail, durationSec));
    }

    private async Task WriterLoopAsync()
    {
        var batch = new List<Pending>();
        while (await _queue.Reader.WaitToReadAsync())
        {
            await Task.Delay(2000);   // let a few more arrive, then write them in one go
            batch.Clear();
            while (batch.Count < 500 && _queue.Reader.TryRead(out var p)) batch.Add(p);
            if (batch.Count == 0) continue;
            try
            {
                using var scope = _services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var conn = db.Database.GetDbConnection();
                if (conn.State != ConnectionState.Open) await conn.OpenAsync();
                using var tx = await conn.BeginTransactionAsync();
                foreach (var p in batch)
                {
                    using var cmd = conn.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = "INSERT INTO \"UserActivity\" (\"At\",\"ChatId\",\"Kind\",\"Detail\",\"DurationSec\") VALUES ($a,$c,$k,$d,$s)";
                    Add(cmd, "$a", p.At.ToString("yyyy-MM-dd HH:mm:ss"));
                    Add(cmd, "$c", p.ChatId); Add(cmd, "$k", p.Kind); Add(cmd, "$d", p.Detail); Add(cmd, "$s", p.DurationSec);
                    await cmd.ExecuteNonQueryAsync();
                }
                await tx.CommitAsync();
                if (DateTime.UtcNow - _purgedAt > TimeSpan.FromHours(6))
                {
                    _purgedAt = DateTime.UtcNow;
                    var cutoff = DateTime.UtcNow.AddDays(-RetentionDays).ToString("yyyy-MM-dd HH:mm:ss");
                    await db.Database.ExecuteSqlRawAsync("DELETE FROM \"UserActivity\" WHERE \"At\" < {0}", cutoff);
                }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Could not write {Count} user activity row(s)", batch.Count); }
        }
    }

    private static void Add(System.Data.Common.DbCommand cmd, string name, object? value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
    }

    private static async Task<List<object?[]>> QueryAsync(AppDbContext db, string sql, params (string Name, object? Value)[] ps)
    {
        var conn = db.Database.GetDbConnection();
        var wasClosed = conn.State != ConnectionState.Open;
        if (wasClosed) await conn.OpenAsync();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (name, value) in ps) Add(cmd, name, value);
            var rows = new List<object?[]>();
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                var row = new object?[r.FieldCount];
                for (var i = 0; i < r.FieldCount; i++) row[i] = r.IsDBNull(i) ? null : r.GetValue(i);
                rows.Add(row);
            }
            return rows;
        }
        finally { if (wasClosed) await conn.CloseAsync(); }
    }

    private static int I(object? v) => v is null ? 0 : Convert.ToInt32(v);

    // One line per person active in the last N days.
    public async Task<List<ActivitySummary>> SummaryAsync(int days)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var since = DateTime.UtcNow.AddDays(-days).ToString("yyyy-MM-dd HH:mm:ss");
        var rows = await QueryAsync(db, $@"SELECT ""ChatId"", MAX(""At""),
                SUM(""Kind"" = 'feed_open'),
                COALESCE(SUM(CASE WHEN ""Kind"" = 'feed_close' AND ""DurationSec"" >= 5 THEN ""DurationSec"" END), 0),
                SUM(""Kind"" = 'manage_open'),
                SUM(""Kind"" NOT IN ({NotAnAction}) AND ""Kind"" NOT LIKE 'click:%' AND ""Kind"" NOT LIKE 'nav:%'),
                SUM(""Kind"" LIKE 'click:%' OR ""Kind"" LIKE 'nav:%')
            FROM ""UserActivity"" WHERE ""At"" >= $s GROUP BY ""ChatId"" ORDER BY MAX(""At"") DESC", ("$s", since));
        return rows.Select(r => new ActivitySummary(Convert.ToInt64(r[0]), (string)r[1]!, I(r[2]), I(r[3]), I(r[4]), I(r[5]), I(r[6]))).ToList();
    }

    // The last real action of each person (not a view or a heartbeat), so the table can say what they did last.
    public async Task<Dictionary<long, ActivityRow>> LastActionsAsync(int days)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var since = DateTime.UtcNow.AddDays(-days).ToString("yyyy-MM-dd HH:mm:ss");
        var rows = await QueryAsync(db, $@"SELECT ""ChatId"", ""At"", ""Kind"", ""Detail"", ""DurationSec"" FROM ""UserActivity""
            WHERE ""At"" >= $s AND ""Kind"" NOT IN ({NotAnAction}) AND ""Kind"" NOT LIKE 'click:%' AND ""Kind"" NOT LIKE 'nav:%'
            ORDER BY ""At"" DESC LIMIT 3000", ("$s", since));
        var last = new Dictionary<long, ActivityRow>();
        foreach (var r in rows)
        {
            var id = Convert.ToInt64(r[0]);
            if (!last.ContainsKey(id)) last[id] = new ActivityRow((string)r[1]!, (string)r[2]!, r[3] as string, r[4] is null ? null : Convert.ToInt32(r[4]));
        }
        return last;
    }

    // Everything one person did, newest first.
    public async Task<List<ActivityRow>> RecentAsync(long chatId, int limit)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rows = await QueryAsync(db, @"SELECT ""At"", ""Kind"", ""Detail"", ""DurationSec"" FROM ""UserActivity""
            WHERE ""ChatId"" = $c ORDER BY ""At"" DESC, ""Id"" DESC LIMIT $l", ("$c", chatId), ("$l", Math.Clamp(limit, 1, 500)));
        return rows.Select(r => new ActivityRow((string)r[0]!, (string)r[1]!, r[2] as string, r[3] is null ? null : Convert.ToInt32(r[3]))).ToList();
    }
}
