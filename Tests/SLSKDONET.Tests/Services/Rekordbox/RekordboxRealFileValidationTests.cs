using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Services.Rekordbox;
using Xunit;
using Xunit.Abstractions;

namespace SLSKDONET.Tests.Services.Rekordbox;

/// <summary>
/// Manual diagnostic, not a correctness gate: runs <see cref="RekordboxAnlzParser"/> against
/// whatever real Pioneer analysis files exist on THIS machine's local Rekordbox install. Skips
/// (does not fail) when Rekordbox isn't installed or has no analysed tracks, so it's safe to leave
/// in the suite and won't turn CI or other dev machines red.
///
/// Why this exists: <see cref="RekordboxAnlzParserTests"/> and <see cref="RekordboxPssiServiceTests"/>
/// only exercise synthetic PMAI/PSSI bytes that this same test project built with the exact same
/// offset arithmetic and XOR formula the parser uses to read them back — a closed loop that proves
/// internal consistency but can never catch a real mismatch against Pioneer's actual encoder:
/// unexpected tag interleaving between PPTH and PSSI, an XOR mask phase that starts somewhere other
/// than byte 0 of the encrypted slice, or phrase "kind" values outside the vocabulary
/// <see cref="RekordboxPssiService"/> maps. Until this runs against bytes Pioneer's own engine
/// wrote, the parser is an unverified hypothesis, not a working feature.
/// </summary>
public class RekordboxRealFileValidationTests
{
    private readonly ITestOutputHelper _output;

    public RekordboxRealFileValidationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void InspectLocalRekordboxAnlzFiles()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var candidateRoots = new[]
        {
            Path.Combine(appData, "Pioneer", "rekordbox", "share", "PIONEER", "USBANLZ"), // Rekordbox 6+
            Path.Combine(appData, "Pioneer", "rekordbox", "PIONEER", "USBANLZ"),          // Rekordbox 5
        };

        var anlzDir = candidateRoots.FirstOrDefault(Directory.Exists);
        if (anlzDir == null)
        {
            _output.WriteLine("No local Rekordbox analysis folder found (checked: " +
                string.Join(", ", candidateRoots) + "). Skipping — meaningful only on a machine " +
                "with Rekordbox installed and at least one analysed track.");
            return;
        }

        // PSSI's documented home is .EXT only (.2EX carries 3-band preview waveforms, .DAT carries
        // beatgrid/basic cues) — real-file validation is the only way to actually confirm that, so
        // if a real .2EX in this scan turns out to carry PSSI too, that's worth knowing.
        var extFiles = Directory.EnumerateFiles(anlzDir, "*.EXT", SearchOption.AllDirectories).Take(40).ToList();
        if (extFiles.Count == 0)
        {
            _output.WriteLine($"Found {anlzDir} but no .EXT files under it. Skipping.");
            return;
        }

        int parsedOk = 0, withPath = 0, withPhrases = 0;
        var failures = new List<string>();

        foreach (var file in extFiles)
        {
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(file);
            }
            catch (Exception ex)
            {
                failures.Add($"{Path.GetFileName(file)}: could not read file — {ex.Message}");
                continue;
            }

            var parsed = RekordboxAnlzParser.TryParse(bytes);
            if (parsed == null)
            {
                failures.Add($"{Path.GetFileName(file)}: TryParse returned null (magic mismatch, or " +
                    $"neither PPTH nor PSSI present) — file size {bytes.Length}");
                continue;
            }
            parsedOk++;

            _output.WriteLine($"File: {Path.GetFileName(file)} ({bytes.Length} bytes)");
            _output.WriteLine($"  SourcePath: {parsed.SourcePath ?? "(none)"}");
            _output.WriteLine($"  Mood: {parsed.Mood}, Phrases: {parsed.Phrases.Count}");
            foreach (var phrase in parsed.Phrases.Take(6))
            {
                _output.WriteLine($"    Index={phrase.Index} Beat={phrase.Beat} Kind={phrase.Kind}");
            }

            if (!string.IsNullOrWhiteSpace(parsed.SourcePath))
            {
                withPath++;
            }
            else
            {
                failures.Add($"{Path.GetFileName(file)}: PPTH missing/empty — every real .EXT should carry a source path");
            }

            if (parsed.Phrases.Count == 0)
            {
                continue; // Valid outcome: this track just hasn't had phrase analysis run in Rekordbox.
            }
            withPhrases++;

            // Mood is documented as 1 (high/EDM), 2 (mid), 3 (low). A value outside that is the
            // clearest possible symptom of an XOR phase/offset bug — it's the very first field read
            // from the unmasked buffer, so if the mask alignment is wrong this is the first place it
            // would show up as garbage rather than a valid small integer.
            if (parsed.Mood is < 1 or > 3)
            {
                failures.Add($"{Path.GetFileName(file)}: mood={parsed.Mood} outside documented 1-3 range — " +
                    "likely XOR mask phase/offset bug");
            }

            for (int i = 0; i < parsed.Phrases.Count; i++)
            {
                int kind = parsed.Phrases[i].Kind;
                // Generous sanity bound, not the mapped vocabulary: HighMoodLabels/MidLowMoodLabels
                // in RekordboxPssiService only map a handful of kinds (up to 10) and intentionally
                // leave others (e.g. numbered Verses) unmapped rather than guessed at, so an
                // unmapped-but-small kind is expected, not a bug. Anything wildly out of range is
                // still a strong signal of mask corruption.
                if (kind is < 1 or > 30)
                {
                    failures.Add($"{Path.GetFileName(file)}: phrase[{i}].Kind={kind} looks like garbage, " +
                        "not a plausible Pioneer phrase category");
                }
                if (i > 0 && parsed.Phrases[i].Beat <= parsed.Phrases[i - 1].Beat)
                {
                    failures.Add($"{Path.GetFileName(file)}: phrase beats not strictly increasing at index {i} " +
                        $"({parsed.Phrases[i - 1].Beat} -> {parsed.Phrases[i].Beat})");
                }
            }
        }

        _output.WriteLine("");
        _output.WriteLine($"Summary: {parsedOk}/{extFiles.Count} parsed, {withPath} with a source path, " +
            $"{withPhrases} with phrase (PSSI) data.");

        Assert.True(failures.Count == 0, "Real-file validation found issues:\n" + string.Join("\n", failures));
    }

    [Fact]
    public async Task AnalyzeAsync_FindsRealPhraseData_ViaFilenameFallback()
    {
        // End-to-end proof, not just the raw parser: RekordboxPssiService is what
        // AnalyzeTrackStructureJob actually calls, and real PPTH paths in this install are
        // Rekordbox's "?/<filename>" unresolved-drive placeholder (see the service's own doc
        // comment on _filenameFallbackIndex) — never equal to a real absolute path ORBIT would
        // have for the same file. Exact-path matching alone would silently find nothing for any
        // such track. This confirms the filename fallback actually bridges that gap.
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var anlzDir = new[]
        {
            Path.Combine(appData, "Pioneer", "rekordbox", "share", "PIONEER", "USBANLZ"),
            Path.Combine(appData, "Pioneer", "rekordbox", "PIONEER", "USBANLZ"),
        }.FirstOrDefault(Directory.Exists);
        if (anlzDir == null)
        {
            _output.WriteLine("No local Rekordbox analysis folder found. Skipping.");
            return;
        }

        // Find one real .EXT with a degraded (non-rooted) PPTH path and actual phrase data, so the
        // fallback path is guaranteed to be exercised rather than the direct match.
        string? realFileName = null;
        foreach (var file in Directory.EnumerateFiles(anlzDir, "*.EXT", SearchOption.AllDirectories).Take(100))
        {
            var parsed = RekordboxAnlzParser.TryParse(File.ReadAllBytes(file));
            if (parsed?.SourcePath is { Length: > 0 } sp && !Path.IsPathRooted(sp) && parsed.Phrases.Count > 0)
            {
                realFileName = Path.GetFileName(sp);
                break;
            }
        }
        if (realFileName == null)
        {
            _output.WriteLine("No .EXT with a degraded PPTH path and phrase data found in the first 100 scanned. Skipping.");
            return;
        }

        var service = new RekordboxPssiService(NullLogger<RekordboxPssiService>.Instance);
        // A path that could never appear in Rekordbox's own analysis cache — proves the match came
        // from the filename fallback, not a coincidental exact-path hit.
        var fakeOrbitPath = Path.Combine(@"D:\ORBIT-Test-Library\Does\Not\Exist", realFileName);

        var segments = await service.AnalyzeAsync(fakeOrbitPath, bpm: 128, downbeatAnchor: 0.0);

        _output.WriteLine($"Matched filename: {realFileName}");
        _output.WriteLine($"Segments returned: {segments?.Count ?? 0}");
        if (segments != null)
        {
            foreach (var s in segments) _output.WriteLine($"  {s.Label} @ {s.Start:F1}s (+{s.Duration:F1}s)");
        }

        Assert.NotNull(segments);
        Assert.NotEmpty(segments!);
    }
}
