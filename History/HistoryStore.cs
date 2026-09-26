using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using MediaBrowser.Common.Configuration;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.EpisodeHistory.History;

/// <summary>
/// The history database: one SQLite file of the plugin's own under the server's data dir
/// (<c>/var/lib/jellyfin/data/episode-history/history.db</c>), kept out of
/// <c>jellyfin.db</c> so Jellyfin's schema migrations can never touch it, and out of the
/// plugin's versioned install folder so it survives upgrades.
///
/// Like MythTV's <c>oldrecorded</c> table, rows outlive the files they describe: a
/// "recorded" row stays after the recording is deleted (it then means "recorded before"),
/// and a "watched" row stays after the item is gone.
/// </summary>
public sealed class HistoryStore
{
    public const string KindRecorded = "recorded";
    public const string KindWatched = "watched";

    private readonly object _lock = new();
    private readonly string _connectionString;

    public HistoryStore(IApplicationPaths applicationPaths)
    {
        var directory = Path.Combine(applicationPaths.DataPath, "episode-history");
        Directory.CreateDirectory(directory);
        DatabasePath = Path.Combine(directory, "history.db");
        _connectionString = new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString();
        Initialize();
    }

    public string DatabasePath { get; }

    public sealed record Row(
        long Id,
        string Kind,
        Guid? UserId,
        Guid ItemId,
        EpisodeIdentity Identity,
        string Source,
        DateTime EventUtc);

    public sealed record CancelledTimer(string TimerId, string? Title, string Reason, DateTime CancelledUtc);

    private void Initialize()
    {
        lock (_lock)
        {
            using var conn = Open();
            // Rollback journal, not WAL: in WAL mode anyone who merely opens the file (an admin
            // inspecting it with sqlite3, a backup script) creates -wal/-shm files owned by
            // them, and Jellyfin then gets "attempt to write a readonly database" forever.
            // Writes here are small and rare, so WAL buys nothing.
            Exec(conn, "PRAGMA journal_mode=DELETE;");
            Exec(conn, """
                CREATE TABLE IF NOT EXISTS history (
                    id            INTEGER PRIMARY KEY,
                    kind          TEXT    NOT NULL,        -- 'recorded' (household) | 'watched' (per user)
                    user_id       TEXT,                    -- watched rows only
                    item_id       TEXT    NOT NULL,        -- library item; may no longer exist
                    program_id    TEXT,                    -- TMS id (EP.../MV...) when known
                    series_name   TEXT,                    -- movie title for movies
                    series_alt    TEXT,                    -- folder / original-title alias
                    season        INTEGER,
                    episode       INTEGER,
                    episode_title TEXT,
                    is_movie      INTEGER NOT NULL DEFAULT 0,
                    year          INTEGER,
                    source        TEXT    NOT NULL,        -- 'recording' | 'import' | 'played'
                    event_utc     TEXT    NOT NULL
                );
                CREATE UNIQUE INDEX IF NOT EXISTS ux_history_item ON history(kind, IFNULL(user_id, ''), item_id);
                CREATE INDEX IF NOT EXISTS ix_history_program ON history(program_id);
                CREATE TABLE IF NOT EXISTS cancelled_timers (
                    timer_id      TEXT PRIMARY KEY,
                    title         TEXT,
                    reason        TEXT NOT NULL,
                    cancelled_utc TEXT NOT NULL
                );
                """);
        }
    }

    /// <summary>
    /// Inserts or updates the row for (kind, user, item). Returns true if anything changed,
    /// so callers only re-sweep the guide when the history really moved.
    /// </summary>
    public bool Upsert(string kind, Guid? userId, Guid itemId, EpisodeIdentity identity, string source, DateTime eventUtc)
    {
        return UpsertMany(new[] { (kind, userId, itemId, identity, source, eventUtc) }) > 0;
    }

    /// <param name="rows">Rows to write.</param>
    /// <param name="overwrite">False: only insert rows that don't exist yet, leaving existing
    /// ones alone (the library import must not replace what a finished recording captured
    /// from its timer).</param>
    public int UpsertMany(IEnumerable<(string Kind, Guid? UserId, Guid ItemId, EpisodeIdentity Identity, string Source, DateTime EventUtc)> rows, bool overwrite = true)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            int changed = 0;

            using var select = conn.CreateCommand();
            select.Transaction = tx;
            select.CommandText = """
                SELECT id, program_id, series_name, series_alt, season, episode, episode_title, is_movie, year
                FROM history WHERE kind = $kind AND IFNULL(user_id, '') = $user AND item_id = $item
                """;
            var sKind = select.Parameters.Add("$kind", SqliteType.Text);
            var sUser = select.Parameters.Add("$user", SqliteType.Text);
            var sItem = select.Parameters.Add("$item", SqliteType.Text);

            foreach (var r in rows)
            {
                sKind.Value = r.Kind;
                sUser.Value = UserKey(r.UserId);
                sItem.Value = ItemKey(r.ItemId);

                long? existingId = null;
                EpisodeIdentity? existing = null;
                using (var reader = select.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        existingId = reader.GetInt64(0);
                        existing = ReadIdentity(reader, 1);
                    }
                }

                // A later event for the same item may lack the programme id the recording
                // captured (e.g. a "played" event); keep the id rather than losing it.
                var identity = r.Identity.ProgramId is null && existing?.ProgramId is not null
                    ? r.Identity with { ProgramId = existing.ProgramId }
                    : r.Identity;

                if (existing == identity || (existing is not null && !overwrite))
                {
                    continue;
                }

                using var write = conn.CreateCommand();
                write.Transaction = tx;
                write.CommandText = existingId is null
                    ? """
                      INSERT INTO history (kind, user_id, item_id, program_id, series_name, series_alt, season, episode, episode_title, is_movie, year, source, event_utc)
                      VALUES ($kind, $user, $item, $pid, $series, $alt, $season, $episode, $title, $movie, $year, $source, $utc)
                      """
                    : """
                      UPDATE history SET program_id = $pid, series_name = $series, series_alt = $alt, season = $season, episode = $episode,
                          episode_title = $title, is_movie = $movie, year = $year, source = $source, event_utc = $utc
                      WHERE id = $id
                      """;
                write.Parameters.AddWithValue("$id", existingId ?? 0);
                write.Parameters.AddWithValue("$kind", r.Kind);
                write.Parameters.AddWithValue("$user", (object?)r.UserId?.ToString("N") ?? DBNull.Value);
                write.Parameters.AddWithValue("$item", ItemKey(r.ItemId));
                AddIdentity(write, identity);
                write.Parameters.AddWithValue("$source", r.Source);
                write.Parameters.AddWithValue("$utc", r.EventUtc.ToString("O", CultureInfo.InvariantCulture));
                write.ExecuteNonQuery();
                changed++;
            }

            tx.Commit();
            return changed;
        }
    }

    public bool DeleteWatched(Guid userId, Guid itemId)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM history WHERE kind = $kind AND user_id = $user AND item_id = $item";
            cmd.Parameters.AddWithValue("$kind", KindWatched);
            cmd.Parameters.AddWithValue("$user", UserKey(userId));
            cmd.Parameters.AddWithValue("$item", ItemKey(itemId));
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    public List<Row> LoadAll() => Query("SELECT * FROM history ORDER BY id", null);

    /// <summary>A user's watched rows from one source (e.g. "manual").</summary>
    public List<Row> LoadWatched(Guid userId, string source) =>
        Query(
            "SELECT * FROM history WHERE kind = $kind AND user_id = $user AND source = $source ORDER BY id",
            cmd =>
            {
                cmd.Parameters.AddWithValue("$kind", KindWatched);
                cmd.Parameters.AddWithValue("$user", UserKey(userId));
                cmd.Parameters.AddWithValue("$source", source);
            });

    public int DeleteRows(IEnumerable<long> ids)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM history WHERE id = $id";
            var p = cmd.Parameters.Add("$id", SqliteType.Integer);
            int deleted = 0;
            foreach (var id in ids)
            {
                p.Value = id;
                deleted += cmd.ExecuteNonQuery();
            }

            tx.Commit();
            return deleted;
        }
    }

    public List<Row> Recent(int limit) =>
        Query("SELECT * FROM history ORDER BY event_utc DESC, id DESC LIMIT $limit", cmd => cmd.Parameters.AddWithValue("$limit", limit));

    /// <summary>The TMS id a finished recording captured for this item, if any.</summary>
    public string? GetProgramIdForItem(Guid itemId)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT program_id FROM history WHERE item_id = $item AND program_id IS NOT NULL LIMIT 1";
            cmd.Parameters.AddWithValue("$item", ItemKey(itemId));
            return cmd.ExecuteScalar() as string;
        }
    }

    public bool HasRecorded(Guid itemId)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM history WHERE kind = $kind AND item_id = $item";
            cmd.Parameters.AddWithValue("$kind", KindRecorded);
            cmd.Parameters.AddWithValue("$item", ItemKey(itemId));
            return cmd.ExecuteScalar() is not null;
        }
    }

    public bool HasCancelled(string timerId)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM cancelled_timers WHERE timer_id = $id";
            cmd.Parameters.AddWithValue("$id", timerId);
            return cmd.ExecuteScalar() is not null;
        }
    }

    public void AddCancelled(string timerId, string? title, string reason)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT OR REPLACE INTO cancelled_timers (timer_id, title, reason, cancelled_utc) VALUES ($id, $title, $reason, $utc)";
            cmd.Parameters.AddWithValue("$id", timerId);
            cmd.Parameters.AddWithValue("$title", (object?)title ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$reason", reason);
            cmd.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            cmd.ExecuteNonQuery();
        }
    }

    public List<CancelledTimer> RecentCancelled(int limit)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT timer_id, title, reason, cancelled_utc FROM cancelled_timers ORDER BY cancelled_utc DESC LIMIT $limit";
            cmd.Parameters.AddWithValue("$limit", limit);
            var list = new List<CancelledTimer>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new CancelledTimer(
                    reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.GetString(2),
                    ParseUtc(reader.GetString(3))));
            }

            return list;
        }
    }

    public Dictionary<string, long> Counts()
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT 'recorded', COUNT(*) FROM history WHERE kind = 'recorded'
                UNION ALL SELECT 'recordedWithProgramId', COUNT(*) FROM history WHERE kind = 'recorded' AND program_id IS NOT NULL
                UNION ALL SELECT 'watched', COUNT(*) FROM history WHERE kind = 'watched'
                UNION ALL SELECT 'cancelledTimers', COUNT(*) FROM cancelled_timers
                """;
            var result = new Dictionary<string, long>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result[reader.GetString(0)] = reader.GetInt64(1);
            }

            return result;
        }
    }

    public bool IsEmpty()
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM history)";
            return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) == 0;
        }
    }

    private List<Row> Query(string sql, Action<SqliteCommand>? bind)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            bind?.Invoke(cmd);
            var list = new List<Row>();
            using var reader = cmd.ExecuteReader();
            int oKind = reader.GetOrdinal("kind"), oUser = reader.GetOrdinal("user_id"), oItem = reader.GetOrdinal("item_id"),
                oPid = reader.GetOrdinal("program_id"), oSource = reader.GetOrdinal("source"), oUtc = reader.GetOrdinal("event_utc");
            while (reader.Read())
            {
                list.Add(new Row(
                    reader.GetInt64(reader.GetOrdinal("id")),
                    reader.GetString(oKind),
                    reader.IsDBNull(oUser) ? null : Guid.Parse(reader.GetString(oUser)),
                    Guid.Parse(reader.GetString(oItem)),
                    ReadIdentity(reader, oPid),
                    reader.GetString(oSource),
                    ParseUtc(reader.GetString(oUtc))));
            }

            return list;
        }
    }

    // Reads program_id .. year, which are consecutive columns starting at `first`.
    private static EpisodeIdentity ReadIdentity(SqliteDataReader r, int first)
    {
        string? S(int i) => r.IsDBNull(first + i) ? null : r.GetString(first + i);
        int? I(int i) => r.IsDBNull(first + i) ? null : r.GetInt32(first + i);
        return new EpisodeIdentity(S(0), S(1), S(2), I(3), I(4), S(5), r.GetInt64(first + 6) != 0, I(7));
    }

    private static void AddIdentity(SqliteCommand cmd, EpisodeIdentity id)
    {
        cmd.Parameters.AddWithValue("$pid", (object?)id.ProgramId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$series", (object?)id.SeriesName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$alt", (object?)id.SeriesAlt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$season", (object?)id.Season ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$episode", (object?)id.Episode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$title", (object?)id.EpisodeTitle ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$movie", id.IsMovie ? 1 : 0);
        cmd.Parameters.AddWithValue("$year", (object?)id.Year ?? DBNull.Value);
    }

    private static string UserKey(Guid? userId) => userId?.ToString("N") ?? string.Empty;

    private static string ItemKey(Guid itemId) => itemId.ToString("N");

    // Stored as round-trip ("O") strings; a value written from an Unspecified-kind DateTime
    // (library DateCreated) has no "Z" but is UTC too, hence AssumeUniversal.
    private static DateTime ParseUtc(string s) =>
        DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
