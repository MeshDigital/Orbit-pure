using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace RekordboxBridge;

/// <summary>
/// RekordboxBridge — reads Rekordbox 6/7's encrypted master.db for ORBIT. Phase 1: read-only.
///
///   RekordboxBridge probe          --db &lt;master.db&gt;   can it be opened; row counts
///   RekordboxBridge cues           --db &lt;master.db&gt;   every track with cues, as JSON
///   RekordboxBridge encoding-stats --db &lt;master.db&gt;   how Kind / colour / positions are encoded
///
/// JSON goes to stdout, a one-line reason to stderr. Exit codes: 0 ok, 1 usage, 2 database not
/// found, 3 couldn't decrypt (wrong key / unsupported Rekordbox), 4 unexpected schema or error.
/// The connection is read-only: nothing in the Rekordbox library is changed.
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

        SQLitePCL.Batteries_V2.Init();
        SqliteConnection connection;
        try
        {
            connection = Open(db);
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
        Console.Error.WriteLine("usage: RekordboxBridge probe|cues|encoding-stats --db <path to master.db>");
        return 1;
    }

    private static string? Arg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    /// <summary>Read-only, SQLCipher passphrase via PRAGMA key (Microsoft.Data.Sqlite's Password).
    /// The first query proves the key: a wrong key fails with "file is not a database".</summary>
    private static SqliteConnection Open(string path)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Password = RekordboxKey.Get(),
            Pooling = false,
        };
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
