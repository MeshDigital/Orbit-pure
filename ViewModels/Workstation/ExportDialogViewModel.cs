using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using ReactiveUI;
using SLSKDONET.Data.Entities;
using SLSKDONET.Services;
using SLSKDONET.Services.Audio;
using SLSKDONET.ViewModels.Workstation;

namespace SLSKDONET.ViewModels.Workstation;

/// <summary>
/// Drives the Export dialog for the Workstation page.
/// Collects output path + deck options, then delegates to <see cref="MixdownService"/>.
/// </summary>
public sealed class ExportDialogViewModel : ReactiveObject, IDisposable
{
    private readonly MixdownService _mixdown;
    private readonly DatabaseService? _db;

    // ── Input sources (populated from active decks) ───────────────────────────

    public ObservableCollection<ExportDeckEntry> Decks { get; } = new();

    // ── Output path ───────────────────────────────────────────────────────────

    private string _outputPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
        "ORBIT Exports",
        $"Mixdown_{DateTime.Now:yyyyMMdd_HHmmss}.wav");

    public string OutputPath
    {
        get => _outputPath;
        set => this.RaiseAndSetIfChanged(ref _outputPath, value);
    }

    // ── Format / quality ──────────────────────────────────────────────────────

    private string _selectedFormat = "WAV";
    public string SelectedFormat
    {
        get => _selectedFormat;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedFormat, value);
            // Update output extension to match
            var ext = value.ToLowerInvariant();
            var dir  = Path.GetDirectoryName(OutputPath) ?? string.Empty;
            var stem = Path.GetFileNameWithoutExtension(OutputPath);
            OutputPath = Path.Combine(dir, $"{stem}.{ext}");
        }
    }

    public System.Collections.Generic.IReadOnlyList<string> AvailableFormats { get; } =
        new[] { "WAV", "MP3", "FLAC" };

    private bool _normalize;
    public bool Normalize
    {
        get => _normalize;
        set => this.RaiseAndSetIfChanged(ref _normalize, value);
    }

    private bool _dither = true;
    public bool Dither
    {
        get => _dither;
        set => this.RaiseAndSetIfChanged(ref _dither, value);
    }

    // ── Progress / state ──────────────────────────────────────────────────────

    private double _exportProgress;
    public double ExportProgress
    {
        get => _exportProgress;
        private set => this.RaiseAndSetIfChanged(ref _exportProgress, value);
    }

    private bool _isExporting;
    public bool IsExporting
    {
        get => _isExporting;
        private set => this.RaiseAndSetIfChanged(ref _isExporting, value);
    }

    private string _statusMessage = string.Empty;
    public string StatusMessage
    {
        get => _statusMessage;
        private set => this.RaiseAndSetIfChanged(ref _statusMessage, value);
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit> ExportCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand  { get; }

    private CancellationTokenSource? _cts;

    // ── Constructor ───────────────────────────────────────────────────────────

    public ExportDialogViewModel(MixdownService mixdown, DatabaseService? db = null)
    {
        _mixdown = mixdown;
        _db      = db;

        var canExport = this.WhenAnyValue(
            x => x.IsExporting, x => x.OutputPath,
            (busy, path) => !busy && !string.IsNullOrWhiteSpace(path));

        ExportCommand = ReactiveCommand.CreateFromTask(RunExportAsync, canExport);
        CancelCommand = ReactiveCommand.Create(Cancel);
    }

    /// <summary>Populates the deck list from active workstation decks.</summary>
    public void SetDecks(System.Collections.Generic.IEnumerable<WorkstationDeckViewModel> decks)
    {
        Decks.Clear();
        foreach (var d in decks)
        {
            if (d.IsLoaded && !string.IsNullOrEmpty(d.Deck.LoadedFilePath))
                Decks.Add(new ExportDeckEntry(d));
        }
    }

    private async Task RunExportAsync()
    {
        _cts = new CancellationTokenSource();
        IsExporting   = true;
        ExportProgress = 0;
        StatusMessage  = "Exporting…";

        try
        {
            var sources = new System.Collections.Generic.List<MixdownService.DeckSource>();
            foreach (var e in Decks)
            {
                if (e.Include && e.Deck.Deck.LoadedFilePath is { } path)
                    sources.Add(new MixdownService.DeckSource(path, e.Volume, e.IsMuted));
            }

            var prog = new Progress<double>(v => ExportProgress = v);
            var fmt  = SelectedFormat switch
            {
                "MP3"  => ExportFormat.Mp3,
                "FLAC" => ExportFormat.Flac,
                _      => ExportFormat.Wav
            };
            var settings = new ExportSettings(fmt, Normalize, Dither);
            await _mixdown.ExportAsync(sources, OutputPath, settings, prog, _cts.Token);
            StatusMessage = $"Done — saved to {Path.GetFileName(OutputPath)}";
            // Record a synthetic export entry in Download Center history
            if (_db != null)
            {
                _ = _db.RecordDownloadHistoryAsync(new DownloadHistoryEntity
                {
                    TrackHash          = $"export-{Guid.NewGuid():N}",
                    Artist             = "ORBIT Studio",
                    Title              = Path.GetFileName(OutputPath),
                    SearchOutcome      = "MixExport",
                    FinalState         = "Completed",
                    DownloadedFilename = OutputPath,
                    DownloadedFormat   = settings.Format.ToString().ToLowerInvariant(),
                    SearchStartedAt    = DateTime.UtcNow,
                    SearchEndedAt      = DateTime.UtcNow,
                    RecordedAt         = DateTime.UtcNow
                });
            }        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Export cancelled.";
            ExportProgress = 0;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Export failed: {ex.Message}";
        }
        finally
        {
            IsExporting = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void Cancel()
    {
        _cts?.Cancel();
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}

/// <summary>Per-deck entry in the export dialog.</summary>
public sealed class ExportDeckEntry : ReactiveObject
{
    public WorkstationDeckViewModel Deck { get; }

    private bool _include = true;
    public bool Include
    {
        get => _include;
        set => this.RaiseAndSetIfChanged(ref _include, value);
    }

    private float _volume = 1f;
    public float Volume
    {
        get => _volume;
        set => this.RaiseAndSetIfChanged(ref _volume, value);
    }

    private bool _isMuted;
    public bool IsMuted
    {
        get => _isMuted;
        set => this.RaiseAndSetIfChanged(ref _isMuted, value);
    }

    public string Label =>
        string.IsNullOrEmpty(Deck.TrackTitle)
            ? $"Deck {Deck.DeckLabel}"
            : $"Deck {Deck.DeckLabel} — {Deck.TrackArtist} – {Deck.TrackTitle}";

    public ExportDeckEntry(WorkstationDeckViewModel deck)
    {
        Deck = deck;
    }
}
