using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Configuration;
using SLSKDONET.Models;
using SLSKDONET.Services.Library.Rekordbox;
using SLSKDONET.Services.Rekordbox;
using Xunit;

namespace SLSKDONET.Tests.Services.Rekordbox;

/// <summary>
/// The opt-in Rekordbox library write, end to end through the real RekordboxBridge process against
/// a plain-SQLite fixture with Rekordbox's own table layout (the CREATE statements are copied from a
/// real Rekordbox 6.7 master.db). Never touches the user's Rekordbox library.
/// </summary>
public sealed class RekordboxDbWriteServiceTests : IDisposable
{
    private const string TrackA = @"D:\Music\A.flac";   // in Rekordbox, no cues
    private const string TrackB = @"D:\Music\B.flac";   // in Rekordbox, has one cue of its own
    private const string TrackC = @"D:\Music\C.flac";   // not in the Rekordbox collection

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"orbit-rbw-{Guid.NewGuid():N}");
    private readonly string _db;
    private readonly AppConfig _config = new() { EnableRekordboxDbWrite = true };
    private readonly RekordboxLibraryService _library;
    private readonly RekordboxDbWriteService _writer;
    private IReadOnlyList<string> _running = Array.Empty<string>();

    public RekordboxDbWriteServiceTests()
    {
        Directory.CreateDirectory(_dir);
        _db = Path.Combine(_dir, "master.db");
        CreateFixture(_db, dbVersion: "6000");

        _library = new RekordboxLibraryService(NullLogger<RekordboxLibraryService>.Instance)
        {
            BridgePath = FindBridge(),
            DatabasePathOverride = _db,
            PlainDatabaseForTests = true,
        };
        _writer = new RekordboxDbWriteService(NullLogger<RekordboxDbWriteService>.Instance, null!, _library, _config)
        {
            BackupRoot = Path.Combine(_dir, "backups"),
            RunningBlockers = () => _running,
        };
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static IReadOnlyList<RekordboxPlannedTrack> Plan(params string[] paths) => paths.Select(p => new RekordboxPlannedTrack(p, p, new[]
    {
        new RekordboxPlannedCue(1, 22221, -1, 22, "[IN -16]"),     // hot cue A, green
        new RekordboxPlannedCue(0, 22221, -1, 0, "[IN -16]"),      // its memory copy
        new RekordboxPlannedCue(9, 140842, -1, 42, "[DROP 2]"),    // hot cue H
        new RekordboxPlannedCue(0, 60000, 67500, 0, "Loop"),       // memory loop
    })).ToList();

    [Fact]
    public async Task Write_FillsTracksWithoutCues_SkipsOwnCuesAndUnknownFiles_AndVerifies()
    {
        var report = await _writer.WriteAsync(Plan(TrackA, TrackB, TrackC), RekordboxWriteMode.FillEmpty);

        Assert.True(report.Success, report.Message);
        Assert.Equal(new[] { TrackA }, report.WrittenPaths);
        Assert.Equal(new[] { TrackB }, report.SkippedHasCues);
        Assert.Equal(new[] { TrackC }, report.NotInRekordbox);
        Assert.Equal(4, report.CuesWritten);
        Assert.Equal(1, report.Verified);
        Assert.Empty(report.VerifyMismatches);
        Assert.True(File.Exists(Path.Combine(report.BackupFolder!, "master.db")));

        using var c = Open();
        Assert.Equal(4L, Scalar(c, "SELECT count(*) FROM djmdCue WHERE ContentID = '100'"));
        Assert.Equal(1L, Scalar(c, "SELECT count(*) FROM djmdCue WHERE ContentID = '200'")); // B untouched

        // Encoded the way Rekordbox encodes its own cues.
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT InMsec, InFrame, OutMsec, OutFrame, Color, ColorTableIndex, Comment, ContentUUID, length(UUID), rb_local_usn FROM djmdCue WHERE ContentID = '100' AND Kind = 1";
            using var r = cmd.ExecuteReader();
            Assert.True(r.Read());
            Assert.Equal(22221L, r.GetInt64(0));
            Assert.Equal(3333L, r.GetInt64(1));      // floor(22221 × 0.15)
            Assert.Equal(-1L, r.GetInt64(2));
            Assert.Equal(0L, r.GetInt64(3));
            Assert.Equal(255L, r.GetInt64(4));       // coloured hot cue
            Assert.Equal(22L, r.GetInt64(5));
            Assert.Equal("[IN -16]", r.GetString(6));
            Assert.Equal("uuid-a", r.GetString(7));
            Assert.Equal(36L, r.GetInt64(8));
            Assert.True(r.IsDBNull(9));              // Rekordbox leaves cue USNs empty
        }
        Assert.Equal(-1L, Scalar(c, "SELECT Color FROM djmdCue WHERE ContentID = '100' AND Kind = 0 AND OutMsec = -1"));
        Assert.Equal(10125L, Scalar(c, "SELECT OutFrame FROM djmdCue WHERE ContentID = '100' AND OutMsec = 67500"));

        // The track row and the change counter move exactly like a cue edit in Rekordbox.
        Assert.Equal("1", ScalarText(c, "SELECT CueUpdated FROM djmdContent WHERE ID = '100'"));
        Assert.Equal(501L, Scalar(c, "SELECT rb_local_usn FROM djmdContent WHERE ID = '100'"));
        Assert.Equal(501L, Scalar(c, "SELECT int_1 FROM agentRegistry WHERE registry_id = 'localUpdateCount'"));
        Assert.Equal(7L, Scalar(c, "SELECT rb_local_usn FROM djmdContent WHERE ID = '200'"));
    }

    [Fact]
    public async Task Write_SecondRunChangesNothing()
    {
        Assert.True((await _writer.WriteAsync(Plan(TrackA), RekordboxWriteMode.FillEmpty)).Success);

        var again = await _writer.WriteAsync(Plan(TrackA), RekordboxWriteMode.FillEmpty);

        Assert.Empty(again.WrittenPaths);
        Assert.Equal(new[] { TrackA }, again.SkippedHasCues);
        using var c = Open();
        Assert.Equal(4L, Scalar(c, "SELECT count(*) FROM djmdCue WHERE ContentID = '100'"));
    }

    [Fact]
    public async Task Replace_DeletesRekordboxCuesOnThatTrackOnly()
    {
        var report = await _writer.WriteAsync(Plan(TrackB), RekordboxWriteMode.Replace);

        Assert.True(report.Success, report.Message);
        Assert.Equal(1, report.CuesRemoved);
        using var c = Open();
        Assert.Equal(0L, Scalar(c, "SELECT count(*) FROM djmdCue WHERE ID = 'rb-own-cue'"));
        Assert.Equal(4L, Scalar(c, "SELECT count(*) FROM djmdCue WHERE ContentID = '200'"));
        Assert.Equal("4", ScalarText(c, "SELECT CueUpdated FROM djmdContent WHERE ID = '200'")); // was 3
    }

    [Fact]
    public async Task Preview_ChangesNothing()
    {
        string before = Hash(_db);

        var preview = await _writer.PreviewAsync(Plan(TrackA, TrackB, TrackC), RekordboxWriteMode.Replace);

        Assert.True(preview.Success, preview.Message);
        Assert.True(preview.DryRun);
        Assert.Equal(2, preview.TracksWritten);
        Assert.Equal(before, Hash(_db));
        Assert.False(Directory.Exists(_writer.BackupRoot));
    }

    [Fact]
    public async Task Write_IsRefused_WhenTurnedOff_OrRekordboxIsRunning_WithoutTouchingAnything()
    {
        string before = Hash(_db);

        _config.EnableRekordboxDbWrite = false;
        Assert.False((await _writer.WriteAsync(Plan(TrackA), RekordboxWriteMode.FillEmpty)).Success);

        _config.EnableRekordboxDbWrite = true;
        _running = new[] { "rekordbox" };
        var busy = await _writer.WriteAsync(Plan(TrackA), RekordboxWriteMode.FillEmpty);

        Assert.False(busy.Success);
        Assert.Contains("Quit Rekordbox", busy.Message);
        Assert.Equal(before, Hash(_db));
        Assert.False(Directory.Exists(_writer.BackupRoot));
    }

    [Fact]
    public async Task Write_IsRefused_ForAnUnknownDatabaseVersion()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_db);
        CreateFixture(_db, dbVersion: "7100");

        var report = await _writer.WriteAsync(Plan(TrackA), RekordboxWriteMode.FillEmpty);

        Assert.False(report.Success);
        Assert.Contains("Unsupported Rekordbox database version", report.Message);
        using var c = Open();
        Assert.Equal(1L, Scalar(c, "SELECT count(*) FROM djmdCue"));
    }

    [Fact]
    public async Task Write_SkipsCuesRekordboxCantTake()
    {
        var bad = new[] { new RekordboxPlannedTrack(TrackA, "A", new[] { new RekordboxPlannedCue(4, 1000, -1, 0, null) }) };

        var report = await _writer.WriteAsync(bad, RekordboxWriteMode.FillEmpty);

        Assert.Equal(new[] { TrackA }, report.Invalid);
        using var c = Open();
        Assert.Equal(0L, Scalar(c, "SELECT count(*) FROM djmdCue WHERE ContentID = '100'"));
    }

    [Fact]
    public async Task Restore_PutsTheLibraryBack_AndDropsAWalTheBackupDidNotHave()
    {
        string before = Hash(_db);
        var report = await _writer.WriteAsync(Plan(TrackA), RekordboxWriteMode.FillEmpty);
        Assert.True(report.Success, report.Message);
        Assert.NotEqual(before, Hash(_db));
        File.WriteAllText(_db + "-wal", "stale");

        var backup = _writer.ListBackups().First(b => b.Folder == report.BackupFolder);
        var (ok, message) = await _writer.RestoreAsync(backup);

        Assert.True(ok, message);
        Assert.Equal(before, Hash(_db));
        Assert.False(File.Exists(_db + "-wal"));
        Assert.Contains(_writer.ListBackups(), b => b.Folder.EndsWith("-before-restore", StringComparison.Ordinal));
    }

    [Fact]
    public void PlannedCues_UseRekordboxKinds_OnePerPad_AndTheHotCueColourTable()
    {
        var marks = new[]
        {
            new RekordboxMark("Drop", false, 64.5, 0, 3, "#28E214"),
            new RekordboxMark("Drop", false, 64.5, 0, -1, "#28E214"),
            new RekordboxMark("Also D", false, 70, 0, 3, "#E62828"),      // pad D already taken
            new RekordboxMark("Hot loop", true, 80, 84, 7, "#E62828"),
            new RekordboxMark("Empty loop", true, 90, 90, -1, "#FFFFFF"),
        };

        var cues = RekordboxDbWriteService.ToPlannedCues(marks);

        Assert.Equal(new RekordboxPlannedCue(5, 64500, -1, 22, "Drop"), cues[0]);
        Assert.Equal(new RekordboxPlannedCue(0, 64500, -1, 0, "Drop"), cues[1]);
        Assert.Equal(0, cues[2].Kind);
        Assert.Equal(new RekordboxPlannedCue(9, 80000, 84000, 42, "Hot loop"), cues[3]);
        Assert.Equal(4, cues.Count); // the empty loop is dropped
    }

    [Fact]
    public void Layout_IsTheXmlExportLayout_PadPlusMemoryCopy_SectionMarker_Countdown_Loop()
    {
        var cues = new List<OrbitCue>
        {
            new() { Name = "Intro end", Timestamp = 10, SlotIndex = -1, Role = CueRole.IntroEnd, Color = "#00FFFF" },
            new() { Name = "Drop 1", Timestamp = 60, SlotIndex = -1, Role = CueRole.Drop, Color = "#FF0000" },
            new() { Name = "Loop", Timestamp = 90, IsLoop = true, LoopEndSeconds = 94, SlotIndex = 5, Color = "#FFFF00" },
        };

        var marks = RekordboxCueLayout.Build(cues, bpm: 174);

        Assert.Equal(-1, marks[0].Num);                                // section marker → memory cue
        Assert.Equal(("Drop 1", 0), (marks[1].Name, marks[1].Num));     // first free pad (5 is the loop's)
        Assert.Equal(("Drop 1", -1), (marks[2].Name, marks[2].Num));    // its memory copy
        Assert.Equal("16 bars to Drop 1", marks[3].Name);
        Assert.Equal(60 - 60.0 / 174 * 4 * 16, marks[3].StartSeconds, 6);
        Assert.Equal((true, 5, 94.0), (marks[4].IsLoop, marks[4].Num, marks[4].EndSeconds));
    }

    // ─── fixture ────────────────────────────────────────────────────────────

    private SqliteConnection Open()
    {
        var c = new SqliteConnection($"Data Source={_db};Pooling=False");
        c.Open();
        return c;
    }

    private static long Scalar(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static string? ScalarText(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar()?.ToString();
    }

    private static string Hash(string path)
    {
        SqliteConnection.ClearAllPools();
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
    }

    private static void CreateFixture(string path, string dbVersion)
    {
        using var c = new SqliteConnection($"Data Source={path};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"""
            CREATE TABLE `djmdCue` (`ID` VARCHAR(255) PRIMARY KEY, `ContentID` VARCHAR(255) DEFAULT NULL, `InMsec` INTEGER DEFAULT NULL, `InFrame` INTEGER DEFAULT NULL, `InMpegFrame` INTEGER DEFAULT NULL, `InMpegAbs` INTEGER DEFAULT NULL, `OutMsec` INTEGER DEFAULT NULL, `OutFrame` INTEGER DEFAULT NULL, `OutMpegFrame` INTEGER DEFAULT NULL, `OutMpegAbs` INTEGER DEFAULT NULL, `Kind` INTEGER DEFAULT NULL, `Color` INTEGER DEFAULT NULL, `ColorTableIndex` INTEGER DEFAULT NULL, `ActiveLoop` INTEGER DEFAULT NULL, `Comment` VARCHAR(255) DEFAULT NULL, `BeatLoopSize` INTEGER DEFAULT NULL, `CueMicrosec` INTEGER DEFAULT NULL, `InPointSeekInfo` VARCHAR(255) DEFAULT NULL, `OutPointSeekInfo` VARCHAR(255) DEFAULT NULL, `ContentUUID` VARCHAR(255) DEFAULT NULL, `UUID` VARCHAR(255) DEFAULT NULL, `rb_data_status` INTEGER DEFAULT 0, `rb_local_data_status` INTEGER DEFAULT 0, `rb_local_deleted` TINYINT(1) DEFAULT 0, `rb_local_synced` TINYINT(1) DEFAULT 0, `usn` BIGINT DEFAULT NULL, `rb_local_usn` BIGINT DEFAULT NULL, `created_at` DATETIME NOT NULL, `updated_at` DATETIME NOT NULL);
            CREATE TABLE `agentRegistry` (`registry_id` VARCHAR(255) PRIMARY KEY, `id_1` VARCHAR(255) DEFAULT NULL, `id_2` VARCHAR(255) DEFAULT NULL, `int_1` BIGINT DEFAULT NULL, `int_2` BIGINT DEFAULT NULL, `str_1` VARCHAR(255) DEFAULT NULL, `str_2` VARCHAR(255) DEFAULT NULL, `date_1` DATETIME DEFAULT NULL, `date_2` DATETIME DEFAULT NULL, `text_1` TEXT DEFAULT NULL, `text_2` TEXT DEFAULT NULL, `created_at` DATETIME NOT NULL, `updated_at` DATETIME NOT NULL);
            CREATE TABLE `djmdProperty` (`DBID` VARCHAR(255) PRIMARY KEY, `DBVersion` VARCHAR(255) DEFAULT NULL, `BaseDBDrive` VARCHAR(255) DEFAULT NULL, `CurrentDBDrive` VARCHAR(255) DEFAULT NULL, `DeviceID` VARCHAR(255) DEFAULT NULL, `Reserved1` TEXT DEFAULT NULL, `Reserved2` TEXT DEFAULT NULL, `Reserved3` TEXT DEFAULT NULL, `Reserved4` TEXT DEFAULT NULL, `Reserved5` TEXT DEFAULT NULL, `created_at` DATETIME NOT NULL, `updated_at` DATETIME NOT NULL);
            CREATE TABLE `djmdContent` (`ID` VARCHAR(255) PRIMARY KEY, `FolderPath` VARCHAR(255) DEFAULT NULL, `Title` VARCHAR(255) DEFAULT NULL, `ArtistID` VARCHAR(255) DEFAULT NULL, `BPM` INTEGER DEFAULT NULL, `CueUpdated` VARCHAR(255) DEFAULT NULL, `UUID` VARCHAR(255) DEFAULT NULL, `rb_local_deleted` TINYINT(1) DEFAULT 0, `rb_local_usn` BIGINT DEFAULT NULL, `created_at` DATETIME NOT NULL, `updated_at` DATETIME NOT NULL);
            CREATE TABLE `djmdArtist` (`ID` VARCHAR(255) PRIMARY KEY, `Name` VARCHAR(255) DEFAULT NULL);
            CREATE INDEX `djmd_cue__content_i_d_rb_local_deleted` ON `djmdCue` (`ContentID`, `rb_local_deleted`);

            INSERT INTO djmdProperty VALUES ('1', '{dbVersion}', '', '', 'dev', NULL, NULL, NULL, NULL, NULL, '2025-01-01', '2025-01-01');
            INSERT INTO agentRegistry (registry_id, int_1, created_at, updated_at) VALUES ('localUpdateCount', 500, '2025-01-01', '2025-01-01');
            INSERT INTO djmdContent VALUES ('100', 'D:/Music/A.flac', 'A', NULL, 17400, NULL, 'uuid-a', 0, 6, '2025-01-01', '2025-01-01');
            INSERT INTO djmdContent VALUES ('200', 'D:/Music/B.flac', 'B', NULL, 17400, '3', 'uuid-b', 0, 7, '2025-01-01', '2025-01-01');
            INSERT INTO djmdCue (ID, ContentID, InMsec, InFrame, OutMsec, Kind, Color, ColorTableIndex, ContentUUID, UUID, created_at, updated_at)
                VALUES ('rb-own-cue', '200', 5000, 750, -1, 1, 255, 22, 'uuid-b', 'u', '2025-01-01', '2025-01-01');
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>The bridge ORBIT's build publishes next to the app (SLSKDONET.csproj's BuildRekordboxBridge target).</summary>
    private static string FindBridge()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SLSKDONET.csproj"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var exe = Directory.GetFiles(Path.Combine(dir!.FullName, "bin"), "RekordboxBridge.exe", SearchOption.AllDirectories)
            .Where(p => p.Contains(Path.Combine("Tools", "RekordboxBridge"), StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        Assert.True(exe != null, "RekordboxBridge.exe wasn't built — build SLSKDONET.csproj first.");
        return exe!;
    }
}
