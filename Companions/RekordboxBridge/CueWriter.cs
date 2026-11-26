using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace RekordboxBridge;

/// <summary>
/// Phase 2: writes ORBIT's cues into Rekordbox's djmdCue table. Opt-in in ORBIT, which backs up
/// master.db before it calls this. Everything a write does mirrors what Rekordbox itself does when
/// a DJ edits cues (learnt from a real 6.7 / 7.2 library, see `schema` and `encoding-stats`):
///
///   • a cue row: random numeric ID, a fresh UUID, ContentUUID = the track's UUID, InFrame =
///     floor(InMsec × 0.15), OutMsec = -1 for a point cue, Color 255 + ColorTableIndex for a
///     coloured hot cue, Color -1 + index 0 otherwise; the change-counter columns stay NULL, as on
///     every cue Rekordbox writes;
///   • the track row: CueUpdated + 1, rb_local_usn = the next local update count, updated_at = now;
///   • agentRegistry 'localUpdateCount' moves up by one per changed track.
///
/// Safety: only a known DBVersion is written; BEGIN IMMEDIATE fails fast when Rekordbox holds the
/// database; one transaction for the whole plan (all or nothing); a track that already has cues in
/// Rekordbox is left alone unless the plan says "replace"; a dry run never opens the file writable.
/// </summary>
internal static class CueWriter
{
    /// <summary>djmdProperty.DBVersion values whose layout this writer was checked against.</summary>
    private static readonly HashSet<string> KnownDbVersions = new(StringComparer.Ordinal) { "6000" };

    internal sealed record PlanCue(int Kind, long InMsec, long OutMsec, int ColorTableIndex, string? Comment);
    internal sealed record PlanTrack(string Path, List<PlanCue> Cues);
    internal sealed record Plan(bool Replace, List<PlanTrack> Tracks);

    private sealed record ContentRow(string Id, string? Uuid, string? CueUpdated, int ExistingCues, bool Ambiguous = false);

    public static Plan ReadPlan(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        bool replace = root.TryGetProperty("mode", out var mode) && mode.GetString() == "replace";
        var tracks = new List<PlanTrack>();
        foreach (var t in root.GetProperty("tracks").EnumerateArray())
        {
            var cues = t.GetProperty("cues").EnumerateArray().Select(c => new PlanCue(
                c.GetProperty("kind").GetInt32(),
                c.GetProperty("inMsec").GetInt64(),
                c.GetProperty("outMsec").GetInt64(),
                c.GetProperty("colorTableIndex").GetInt32(),
                c.TryGetProperty("comment", out var cm) ? cm.GetString() : null)).ToList();
            tracks.Add(new PlanTrack(t.GetProperty("path").GetString() ?? "", cues));
        }
        return new Plan(replace, tracks);
    }

    /// <summary>
    /// Runs the plan (or, with <paramref name="dryRun"/>, only works out what it would do) and writes
    /// the report: { dryRun, dbVersion, replace, written[], skippedHasCues[], notInRekordbox[],
    /// invalid[], localUpdateCountBefore, localUpdateCountAfter, cuesWritten, cuesRemoved, quickCheck }.
    /// Returns the process exit code: 0 ok, 5 unsupported database version, 6 database busy.
    /// </summary>
    public static int Run(SqliteConnection c, Plan plan, bool dryRun, Utf8JsonWriter json, TextWriter errors)
    {
        string? dbVersion = ScalarString(c, null, "SELECT DBVersion FROM djmdProperty LIMIT 1");
        if (dbVersion == null || !KnownDbVersions.Contains(dbVersion))
        {
            errors.WriteLine($"Unsupported Rekordbox database version '{dbVersion ?? "none"}'. ORBIT only writes to layouts it has been checked against ({string.Join(", ", KnownDbVersions)}).");
            return 5;
        }

        SqliteTransaction? tx = null;
        if (!dryRun)
        {
            try
            {
                // IMMEDIATE takes the write lock now, so a running Rekordbox makes this fail before
                // anything is touched instead of halfway through.
                tx = c.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6) // SQLITE_BUSY / SQLITE_LOCKED
            {
                errors.WriteLine("Rekordbox's library is in use. Quit Rekordbox (and its agent in the system tray) and try again.");
                return 6;
            }
        }

        try
        {
            var content = LoadContent(c, tx);
            long usnBefore = ScalarLong(c, tx, "SELECT int_1 FROM agentRegistry WHERE registry_id = 'localUpdateCount'") ?? 0;
            long usn = usnBefore;
            var existingIds = dryRun ? new HashSet<string>() : LoadCueIds(c, tx);
            string now = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + " +00:00";

            var written = new List<(string Path, string Id, int Cues, int Removed)>();
            var skipped = new List<string>();
            var missing = new List<string>();
            var invalid = new List<string>();
            int cuesWritten = 0, cuesRemoved = 0;

            foreach (var track in plan.Tracks)
            {
                if (!content.TryGetValue(Normalize(track.Path), out var row)) { missing.Add(track.Path); continue; }
                if (row.Ambiguous || (row.ExistingCues > 0 && !plan.Replace)) { skipped.Add(track.Path); continue; }
                if (track.Cues.Count == 0 || track.Cues.Any(q => !IsValid(q))) { invalid.Add(track.Path); continue; }

                int removed = row.ExistingCues;
                if (!dryRun)
                {
                    if (removed > 0)
                        Exec(c, tx, "DELETE FROM djmdCue WHERE ContentID = $id", ("$id", row.Id));
                    foreach (var cue in track.Cues)
                        InsertCue(c, tx!, cue, row, NewCueId(existingIds), now);

                    usn++;
                    int cueUpdated = int.TryParse(row.CueUpdated, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cu) ? cu : 0;
                    Exec(c, tx, "UPDATE djmdContent SET CueUpdated = $cu, rb_local_usn = $usn, updated_at = $now WHERE ID = $id",
                        ("$cu", (cueUpdated + 1).ToString(CultureInfo.InvariantCulture)), ("$usn", usn), ("$now", now), ("$id", row.Id));
                }
                written.Add((track.Path, row.Id, track.Cues.Count, removed));
                cuesWritten += track.Cues.Count;
                cuesRemoved += removed;
            }

            string? quickCheck = null;
            if (!dryRun)
            {
                if (usn != usnBefore)
                    Exec(c, tx, "UPDATE agentRegistry SET int_1 = $usn, updated_at = $now WHERE registry_id = 'localUpdateCount'",
                        ("$usn", usn), ("$now", now));

                // Read the result back inside the transaction: every written track must hold exactly
                // the planned cues before anything is committed.
                foreach (var w in written)
                {
                    long count = ScalarLong(c, tx, "SELECT count(*) FROM djmdCue WHERE ContentID = $id AND rb_local_deleted = 0", ("$id", w.Id)) ?? -1;
                    if (count != w.Cues)
                        throw new InvalidOperationException($"Read-back mismatch for {w.Path}: expected {w.Cues} cues, found {count}. Nothing was written.");
                }
                tx!.Commit();
                quickCheck = ScalarString(c, null, "PRAGMA quick_check");
            }

            json.WriteStartObject();
            json.WriteBoolean("dryRun", dryRun);
            json.WriteString("dbVersion", dbVersion);
            json.WriteBoolean("replace", plan.Replace);
            json.WriteStartArray("written");
            foreach (var w in written)
            {
                json.WriteStartObject();
                json.WriteString("path", w.Path);
                json.WriteString("contentId", w.Id);
                json.WriteNumber("cues", w.Cues);
                json.WriteNumber("removed", w.Removed);
                json.WriteEndObject();
            }
            json.WriteEndArray();
            WriteStrings(json, "skippedHasCues", skipped);
            WriteStrings(json, "notInRekordbox", missing);
            WriteStrings(json, "invalid", invalid);
            json.WriteNumber("cuesWritten", cuesWritten);
            json.WriteNumber("cuesRemoved", cuesRemoved);
            json.WriteNumber("localUpdateCountBefore", usnBefore);
            json.WriteNumber("localUpdateCountAfter", usn);
            json.WriteString("quickCheck", quickCheck);
            json.WriteEndObject();
            return 0;
        }
        catch
        {
            tx?.Rollback();
            throw;
        }
        finally
        {
            tx?.Dispose();
        }
    }

    private static bool IsValid(PlanCue q) =>
        q.Kind is 0 or 1 or 2 or 3 or 5 or 6 or 7 or 8 or 9
        && q.InMsec >= 0
        && (q.OutMsec == -1 || q.OutMsec > q.InMsec)
        && q.ColorTableIndex is >= 0 and <= 62;

    private static void InsertCue(SqliteConnection c, SqliteTransaction tx, PlanCue q, ContentRow track, string id, string now)
    {
        bool loop = q.OutMsec > q.InMsec;
        bool coloured = q.Kind > 0 && q.ColorTableIndex > 0;
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO djmdCue (ID, ContentID, InMsec, InFrame, InMpegFrame, InMpegAbs,
                                 OutMsec, OutFrame, OutMpegFrame, OutMpegAbs, Kind, Color, ColorTableIndex,
                                 ActiveLoop, Comment, BeatLoopSize, CueMicrosec, InPointSeekInfo, OutPointSeekInfo,
                                 ContentUUID, UUID, rb_data_status, rb_local_data_status, rb_local_deleted,
                                 rb_local_synced, usn, rb_local_usn, created_at, updated_at)
            VALUES ($id, $content, $in, $inFrame, 0, 0,
                    $out, $outFrame, 0, 0, $kind, $color, $cti,
                    $active, $comment, NULL, NULL, NULL, NULL,
                    $contentUuid, $uuid, 0, 0, 0,
                    0, NULL, NULL, $now, $now)
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$content", track.Id);
        cmd.Parameters.AddWithValue("$in", q.InMsec);
        cmd.Parameters.AddWithValue("$inFrame", Frames(q.InMsec));
        cmd.Parameters.AddWithValue("$out", loop ? q.OutMsec : -1);
        cmd.Parameters.AddWithValue("$outFrame", loop ? Frames(q.OutMsec) : 0);
        cmd.Parameters.AddWithValue("$kind", q.Kind);
        cmd.Parameters.AddWithValue("$color", coloured ? 255 : -1);
        cmd.Parameters.AddWithValue("$cti", coloured ? q.ColorTableIndex : 0);
        cmd.Parameters.AddWithValue("$active", loop ? 0 : DBNull.Value);
        cmd.Parameters.AddWithValue("$comment", string.IsNullOrWhiteSpace(q.Comment) ? DBNull.Value : q.Comment);
        cmd.Parameters.AddWithValue("$contentUuid", (object?)track.Uuid ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$uuid", Guid.NewGuid().ToString("D"));
        cmd.Parameters.AddWithValue("$now", now);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Rekordbox counts cue positions in 1/150 s frames, rounded down (checked on 419 real cues).</summary>
    internal static long Frames(long msec) => msec * 15 / 100;

    /// <summary>Rekordbox's cue IDs are random 32-bit numbers stored as text.</summary>
    private static string NewCueId(HashSet<string> taken)
    {
        while (true)
        {
            string id = ((uint)Random.Shared.NextInt64(1, uint.MaxValue)).ToString(CultureInfo.InvariantCulture);
            if (taken.Add(id)) return id;
        }
    }

    private static Dictionary<string, ContentRow> LoadContent(SqliteConnection c, SqliteTransaction? tx)
    {
        var map = new Dictionary<string, ContentRow>(StringComparer.Ordinal);
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT t.ID, t.FolderPath, t.UUID, t.CueUpdated,
                   (SELECT count(*) FROM djmdCue q WHERE q.ContentID = t.ID AND q.rb_local_deleted = 0)
            FROM djmdContent t
            WHERE t.rb_local_deleted = 0 AND t.FolderPath IS NOT NULL
            """;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            string key = Normalize(r.GetString(1));
            // Two collection entries for one file: leave the file alone rather than guess which.
            if (map.TryGetValue(key, out var first)) { map[key] = first with { Ambiguous = true }; continue; }
            map[key] = new ContentRow(Convert.ToString(r.GetValue(0), CultureInfo.InvariantCulture)!,
                r.IsDBNull(2) ? null : r.GetString(2),
                r.IsDBNull(3) ? null : Convert.ToString(r.GetValue(3), CultureInfo.InvariantCulture),
                Convert.ToInt32(r.GetValue(4), CultureInfo.InvariantCulture));
        }
        return map;
    }

    private static HashSet<string> LoadCueIds(SqliteConnection c, SqliteTransaction? tx)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT ID FROM djmdCue";
        using var r = cmd.ExecuteReader();
        while (r.Read()) ids.Add(Convert.ToString(r.GetValue(0), CultureInfo.InvariantCulture)!);
        return ids;
    }

    internal static string Normalize(string path) => path.Replace('\\', '/').TrimEnd('/').ToLowerInvariant();

    private static void WriteStrings(Utf8JsonWriter json, string name, List<string> values)
    {
        json.WriteStartArray(name);
        foreach (var v in values) json.WriteStringValue(v);
        json.WriteEndArray();
    }

    private static void Exec(SqliteConnection c, SqliteTransaction? tx, string sql, params (string Name, object Value)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }

    private static long? ScalarLong(SqliteConnection c, SqliteTransaction? tx, string sql, params (string Name, object Value)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        var v = cmd.ExecuteScalar();
        return v is null or DBNull ? null : Convert.ToInt64(v, CultureInfo.InvariantCulture);
    }

    private static string? ScalarString(SqliteConnection c, SqliteTransaction? tx, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        var v = cmd.ExecuteScalar();
        return v is null or DBNull ? null : Convert.ToString(v, CultureInfo.InvariantCulture);
    }
}
