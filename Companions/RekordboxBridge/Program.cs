using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace RekordboxBridge;

/// <summary>
/// RekordboxBridge — reads Rekordbox 6/7's encrypted master.db for ORBIT, and (phase 2, opt-in in
/// ORBIT) writes cues into it.
///
///   RekordboxBridge probe          --db &lt;master.db&gt;   can it be opened; row counts
///   RekordboxBridge cues           --db &lt;master.db&gt;   every track with cues, as JSON
///   RekordboxBridge encoding-stats --db &lt;master.db&gt;   how Kind / colour / positions are encoded
///   RekordboxBridge history        --db &lt;master.db&gt;   play-history playlists with their tracks, as JSON
///   RekordboxBridge schema         --db &lt;master.db&gt;   table layout + samples the write path relies on
///   RekordboxBridge write-cues     --db &lt;master.db&gt; --plan &lt;plan.json&gt; [--dry-run]
///                                  write ORBIT's cues (see <see cref="CueWriter"/>); a dry run only reports
///
/// JSON goes to stdout, a one-line reason to stderr. Exit codes: 0 ok, 1 usage, 2 database not
/// found, 3 couldn't decrypt (wrong key / unsupported Rekordbox), 4 unexpected schema or error,
/// 5 unsupported database version (write-cues), 6 database in use (write-cues).
/// Every command except a real write-cues opens the database read-only. --plain opens an
/// unencrypted SQLite file (test fixtures only).
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length < 1) return Usage();
        string command = args[0];
        string? db = Arg(args, "--db");
        if (db == null) return Usage();
        if (!File.Exists(db))
        {
            Console.Error.WriteLine($"Rekordbox database not found: {db}");
            return 2;
        }

        bool write = command == "write-cues" && !args.Contains("--dry-run");
        CueWriter.Plan? plan = null;
        if (command == "write-cues")
        {
            string? planPath = Arg(args, "--plan");
            if (planPath == null || !File.Exists(planPath)) return Usage();
            try { plan = CueWriter.ReadPlan(planPath); }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                Console.Error.WriteLine($"Unreadable cue plan: {ex.Message}");
                return 1;
            }
        }

        SQLitePCL.Batteries_V2.Init();
        SqliteConnection connection;
        try
        {
            connection = Open(db, write, plain: args.Contains("--plain"));
        }
        catch (SqliteException ex)
        {
            Console.Error.WriteLine($"Couldn't decrypt the Rekordbox database ({ex.SqliteErrorCode}): {ex.Message}");
            return 3;
        }

        try
        {
            using (connection)
            using (var stdout = Console.OpenStandardOutput())
            using (var json = new Utf8JsonWriter(stdout, new JsonWriterOptions { Indented = command != "cues" }))
            {
                switch (command)
                {
                    case "probe": Probe(connection, json); break;
                    case "cues": Cues(connection, json); break;
                    case "encoding-stats": EncodingStats(connection, json); break;
                    case "history": History(connection, json); break;
                    case "schema": Schema(connection, json); break;
                    case "write-cues": return CueWriter.Run(connection, plan!, dryRun: !write, json, Console.Error);
                    default: return Usage();
                }
            }
            return 0;
        }
        catch (SqliteException ex)
        {
            Console.Error.WriteLine($"Unexpected Rekordbox database layout: {ex.Message}");
            return 4;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"RekordboxBridge failed: {ex.Message}");
            return 4;
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine("usage: RekordboxBridge probe|cues|encoding-stats|history|schema --db <path to master.db>");
        Console.Error.WriteLine("       RekordboxBridge write-cues --db <path to master.db> --plan <plan.json> [--dry-run]");
        return 1;
    }

    private static string? Arg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    /// <summary>Read-only unless <paramref name="write"/>; SQLCipher passphrase via PRAGMA key
    /// (Microsoft.Data.Sqlite's Password). The first query proves the key: a wrong key fails with
    /// "file is not a database". Writable never creates a file (ReadWrite, not ReadWriteCreate).</summary>
    private static SqliteConnection Open(string path, bool write, bool plain)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = write ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 5,
        };
        if (!plain) builder.Password = RekordboxKey.Get();
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using var check = connection.CreateCommand();
        check.CommandText = "SELECT count(*) FROM sqlite_master";
        check.ExecuteScalar();
        return connection;
    }

    private static long Scalar(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Rekordbox's play history (one playlist per session it recorded), oldest first, each with its
    /// tracks in played order: [{ id, name, created, tracks: [{ trackNo, path, title, artist }] }].
    /// Folders (Attribute 1) and deleted rows are skipped.
    /// </summary>
    private static void History(SqliteConnection c, Utf8JsonWriter json)
    {
        var histories = new List<(string Id, string? Name, string? Created)>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = @"SELECT ID, Name, DateCreated FROM djmdHistory
                                WHERE rb_local_deleted = 0 AND coalesce(Attribute, 0) = 0
                                ORDER BY DateCreated, Seq";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                histories.Add((Convert.ToString(r.GetValue(0), CultureInfo.InvariantCulture) ?? "",
                    r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2)));
        }

        json.WriteStartArray();
        foreach (var h in histories)
        {
            json.WriteStartObject();
            json.WriteString("id", h.Id);
            json.WriteString("name", h.Name);
            json.WriteString("created", h.Created);
            json.WriteStartArray("tracks");
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = @"SELECT s.TrackNo, t.FolderPath, t.Title, a.Name
                                    FROM djmdSongHistory s
                                    JOIN djmdContent t ON t.ID = s.ContentID
                                    LEFT JOIN djmdArtist a ON a.ID = t.ArtistID
                                    WHERE s.HistoryID = $id AND s.rb_local_deleted = 0
                                    ORDER BY s.TrackNo";
                cmd.Parameters.AddWithValue("$id", h.Id);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    json.WriteStartObject();
                    json.WriteNumber("trackNo", r.IsDBNull(0) ? 0 : Convert.ToInt32(r.GetValue(0), CultureInfo.InvariantCulture));
                    json.WriteString("path", r.IsDBNull(1) ? null : r.GetString(1));
                    json.WriteString("title", r.IsDBNull(2) ? null : r.GetString(2));
                    json.WriteString("artist", r.IsDBNull(3) ? null : r.GetString(3));
                    json.WriteEndObject();
                }
            }
            json.WriteEndArray();
            json.WriteEndObject();
        }
        json.WriteEndArray();
    }

    private static void Probe(SqliteConnection c, Utf8JsonWriter json)
    {
        json.WriteStartObject();
        json.WriteBoolean("ok", true);
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "PRAGMA cipher_version";
            json.WriteString("sqlcipher", cmd.ExecuteScalar()?.ToString());
        }
        json.WriteNumber("tracks", Scalar(c, "SELECT count(*) FROM djmdContent WHERE rb_local_deleted = 0"));
        json.WriteNumber("cues", Scalar(c, "SELECT count(*) FROM djmdCue WHERE rb_local_deleted = 0"));
        json.WriteNumber("tracksWithCues", Scalar(c, "SELECT count(DISTINCT ContentID) FROM djmdCue WHERE rb_local_deleted = 0"));
        json.WriteEndObject();
    }

    /// <summary>Every track that has cues: its file path (to match ORBIT tracks) and the raw cue rows.</summary>
    private static void Cues(SqliteConnection c, Utf8JsonWriter json)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT t.ID, t.FolderPath, t.Title, a.Name, t.BPM,
                   q.Kind, q.InMsec, q.OutMsec, q.InFrame, q.OutFrame, q.Color, q.ColorTableIndex,
                   q.Comment, q.ActiveLoop, q.BeatLoopSize, q.CueMicrosec
            FROM djmdCue q
            JOIN djmdContent t ON t.ID = q.ContentID
            LEFT JOIN djmdArtist a ON a.ID = t.ArtistID
            WHERE q.rb_local_deleted = 0 AND t.rb_local_deleted = 0
            ORDER BY t.ID, q.InMsec
            """;
        using var r = cmd.ExecuteReader();
        json.WriteStartArray();
        string? current = null;
        while (r.Read())
        {
            string id = r.GetString(0);
            if (id != current)
            {
                if (current != null) { json.WriteEndArray(); json.WriteEndObject(); }
                current = id;
                json.WriteStartObject();
                json.WriteString("id", id);
                json.WriteString("path", Str(r, 1));
                json.WriteString("title", Str(r, 2));
                json.WriteString("artist", Str(r, 3));
                json.WriteNumber("bpm", Int(r, 4) / 100.0); // stored ×100
                json.WriteStartArray("cues");
            }
            json.WriteStartObject();
            json.WriteNumber("kind", Int(r, 5));
            json.WriteNumber("inMsec", Int(r, 6));
            json.WriteNumber("outMsec", Int(r, 7));
            json.WriteNumber("inFrame", Int(r, 8));
            json.WriteNumber("outFrame", Int(r, 9));
            json.WriteNumber("color", Int(r, 10));
            json.WriteNumber("colorTableIndex", Int(r, 11));
            json.WriteString("comment", Str(r, 12));
            json.WriteNumber("activeLoop", Int(r, 13));
            json.WriteNumber("beatLoopSize", Int(r, 14));
            json.WriteNumber("cueMicrosec", Int(r, 15));
            json.WriteEndObject();
        }
        if (current != null) { json.WriteEndArray(); json.WriteEndObject(); }
        json.WriteEndArray();
    }

    /// <summary>
    /// What phase 2 (writing cues) needs to know and nobody has documented: which Kind values hot
    /// cues A–H use, how Color relates to ColorTableIndex, and the unit of the frame/microsecond
    /// columns relative to InMsec. Learnt from the library's own hand-placed cues.
    /// </summary>
    private static void EncodingStats(SqliteConnection c, Utf8JsonWriter json)
    {
        json.WriteStartObject();
        Histogram(c, json, "kind", "SELECT Kind, count(*) FROM djmdCue WHERE rb_local_deleted = 0 GROUP BY Kind ORDER BY Kind");
        Histogram(c, json, "kindLoops", "SELECT Kind, count(*) FROM djmdCue WHERE rb_local_deleted = 0 AND OutMsec > 0 GROUP BY Kind ORDER BY Kind");
        Histogram(c, json, "colorTableIndex", "SELECT ColorTableIndex, count(*) FROM djmdCue WHERE rb_local_deleted = 0 GROUP BY ColorTableIndex ORDER BY ColorTableIndex");
        Histogram(c, json, "color", "SELECT Color, count(*) FROM djmdCue WHERE rb_local_deleted = 0 GROUP BY Color ORDER BY count(*) DESC LIMIT 40");

        json.WriteStartArray("colorPairs");
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT Kind > 0, Color, ColorTableIndex, count(*) FROM djmdCue WHERE rb_local_deleted = 0 GROUP BY 1, 2, 3 ORDER BY 4 DESC LIMIT 40";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                json.WriteStartObject();
                json.WriteBoolean("hotCue", Int(r, 0) == 1);
                json.WriteNumber("color", Int(r, 1));
                json.WriteNumber("colorTableIndex", Int(r, 2));
                json.WriteNumber("count", Int(r, 3));
                json.WriteEndObject();
            }
        }
        json.WriteEndArray();

        // Position units: ratio of each column to InMsec over cues away from the start.
        foreach (var column in new[] { "InFrame", "InMpegFrame", "InMpegAbs", "CueMicrosec" })
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"SELECT min(1.0 * {column} / InMsec), avg(1.0 * {column} / InMsec), max(1.0 * {column} / InMsec), sum({column} = 0), count(*) FROM djmdCue WHERE rb_local_deleted = 0 AND InMsec > 1000";
            using var r = cmd.ExecuteReader();
            if (!r.Read()) continue;
            json.WriteStartObject("ratio_" + column + "_per_ms");
            json.WriteNumber("min", r.IsDBNull(0) ? 0 : r.GetDouble(0));
            json.WriteNumber("avg", r.IsDBNull(1) ? 0 : r.GetDouble(1));
            json.WriteNumber("max", r.IsDBNull(2) ? 0 : r.GetDouble(2));
            json.WriteNumber("zeroRows", Int(r, 3));
            json.WriteNumber("rows", Int(r, 4));
            json.WriteEndObject();
        }

        json.WriteStartArray("seekInfoSamples");
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT DISTINCT substr(coalesce(InPointSeekInfo, ''), 1, 40) FROM djmdCue WHERE rb_local_deleted = 0 LIMIT 5";
            using var r = cmd.ExecuteReader();
            while (r.Read()) json.WriteStringValue(Str(r, 0));
        }
        json.WriteEndArray();

        json.WriteNumber("maxHotCuesOnOneTrack", Scalar(c, "SELECT coalesce(max(n), 0) FROM (SELECT count(*) n FROM djmdCue WHERE rb_local_deleted = 0 AND Kind > 0 GROUP BY ContentID)"));
        json.WriteEndObject();
    }

    /// <summary>
    /// Diagnostics for the write path: the CREATE statements of the tables a cue write touches,
    /// a few sample cue rows, the change-counter registry and the database version property.
    /// </summary>
    private static void Schema(SqliteConnection c, Utf8JsonWriter json)
    {
        json.WriteStartObject();
        json.WriteStartObject("tables");
        foreach (var table in new[] { "djmdCue", "agentRegistry", "djmdProperty", "djmdContent" })
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = $n";
            cmd.Parameters.AddWithValue("$n", table);
            json.WriteString(table, cmd.ExecuteScalar()?.ToString());
        }
        json.WriteEndObject();
        json.WriteStartArray("triggersAndIndexes");
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT type, name, sql FROM sqlite_master WHERE tbl_name IN ('djmdCue', 'agentRegistry') AND type IN ('trigger', 'index')";
            using var r = cmd.ExecuteReader();
            while (r.Read()) json.WriteStringValue($"{Str(r, 0)} {Str(r, 1)}: {Str(r, 2)}");
        }
        json.WriteEndArray();
        Rows(c, json, "sampleCues", "SELECT * FROM djmdCue WHERE rb_local_deleted = 0 ORDER BY rb_local_usn DESC LIMIT 4");
        Rows(c, json, "agentRegistry", "SELECT * FROM agentRegistry");
        Rows(c, json, "djmdProperty", "SELECT * FROM djmdProperty");
        Rows(c, json, "contentWithCues", "SELECT ID, UUID, CueUpdated, AnalysisUpdated, TrackInfoUpdated, rb_local_usn, updated_at FROM djmdContent WHERE ID IN (SELECT ContentID FROM djmdCue) LIMIT 4");
        Rows(c, json, "contentWithoutCues", "SELECT ID, UUID, CueUpdated, AnalysisUpdated, TrackInfoUpdated, rb_local_usn, updated_at FROM djmdContent WHERE ID NOT IN (SELECT ContentID FROM djmdCue) AND rb_local_deleted = 0 LIMIT 4");
        Rows(c, json, "cueUpdatedValues", "SELECT CueUpdated, count(*) AS n FROM djmdContent GROUP BY CueUpdated ORDER BY n DESC LIMIT 8");
        Rows(c, json, "usnStats", "SELECT (SELECT max(rb_local_usn) FROM djmdCue) AS maxCueUsn, (SELECT max(rb_local_usn) FROM djmdContent) AS maxContentUsn, (SELECT count(*) FROM djmdCue WHERE rb_local_deleted = 1) AS deletedCues");
        json.WriteEndObject();
    }

    private static void Rows(SqliteConnection c, Utf8JsonWriter json, string name, string sql)
    {
        json.WriteStartArray(name);
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            json.WriteStartObject();
            for (int i = 0; i < r.FieldCount; i++) json.WriteString(r.GetName(i), Str(r, i));
            json.WriteEndObject();
        }
        json.WriteEndArray();
    }

    private static void Histogram(SqliteConnection c, Utf8JsonWriter json, string name, string sql)
    {
        json.WriteStartObject(name);
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        while (r.Read()) json.WriteNumber(r.IsDBNull(0) ? "null" : Convert.ToString(r.GetValue(0), CultureInfo.InvariantCulture)!, Int(r, 1));
        json.WriteEndObject();
    }

    private static string? Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture);
    private static long Int(SqliteDataReader r, int i) => r.IsDBNull(i) ? 0 : Convert.ToInt64(r.GetValue(i), CultureInfo.InvariantCulture);
}
