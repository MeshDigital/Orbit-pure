using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Threading;
using System.Threading.Tasks;
using ReactiveUI;
using Microsoft.EntityFrameworkCore;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Engine.Snapping;
using SLSKDONET.Engine.Analysis;
using SLSKDONET.Engine.Cueing;
using SLSKDONET.Exporters.Rekordbox;

namespace SLSKDONET.ViewModels;

public sealed class CurationWorkstationViewModel : ReactiveObject
{
    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly AnalysisPipeline _analysisPipeline;
    private readonly CueGenerationService _cueGenerationService;
    private readonly RekordboxXmlExporter _rekordboxExporter;

    private string _trackHash = string.Empty;
    private double _trackTotalDurationSeconds = 240.0;
    private double _trackBpm = 120.0;
    private double _trackDownbeatAnchorSeconds = 0.0;

    private ObservableCollection<CueMarkerViewModel> _calculatedCues = new();
    private CueMarkerViewModel? _selectedCue;
    private ObservableCollection<LoopViewModel> _loopRegistry = new();
    private LoopViewModel? _selectedLoop;

    private double _transientEngineWeight = 50.0;
    private double _energyCurveWeight = 80.0;

    public CurationWorkstationViewModel(
        IDbContextFactory<AppDbContext> contextFactory,
        AnalysisPipeline analysisPipeline,
        CueGenerationService cueGenerationService)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _analysisPipeline = analysisPipeline ?? throw new ArgumentNullException(nameof(analysisPipeline));
        _cueGenerationService = cueGenerationService ?? throw new ArgumentNullException(nameof(cueGenerationService));
        _rekordboxExporter = new RekordboxXmlExporter();

        SnappingEngine = new TransientAwareSnappingEngine();

        TriggerAnalysisCommand = ReactiveCommand.CreateFromTask(ExecuteRecalculationAsync);
        NavTrackInfoCommand = ReactiveCommand.Create(() => Console.WriteLine("Navigating to Track Info"));
        NavForensicsCommand = ReactiveCommand.Create(() => Console.WriteLine("Navigating to Forensic Signals"));
        ExportXmlCommand = ReactiveCommand.CreateFromTask(ExecuteExportXmlAsync);
    }

    public TransientAwareSnappingEngine SnappingEngine { get; }

    public double TrackTotalDurationSeconds
    {
        get => _trackTotalDurationSeconds;
        set => this.RaiseAndSetIfChanged(ref _trackTotalDurationSeconds, value);
    }

    public double TrackBpm
    {
        get => _trackBpm;
        set => this.RaiseAndSetIfChanged(ref _trackBpm, value);
    }

    public double TrackDownbeatAnchorSeconds
    {
        get => _trackDownbeatAnchorSeconds;
        set => this.RaiseAndSetIfChanged(ref _trackDownbeatAnchorSeconds, value);
    }

    public ObservableCollection<CueMarkerViewModel> CalculatedCues
    {
        get => _calculatedCues;
        set => this.RaiseAndSetIfChanged(ref _calculatedCues, value);
    }

    public CueMarkerViewModel? SelectedCue
    {
        get => _selectedCue;
        set => this.RaiseAndSetIfChanged(ref _selectedCue, value);
    }

    public ObservableCollection<LoopViewModel> LoopRegistry
    {
        get => _loopRegistry;
        set => this.RaiseAndSetIfChanged(ref _loopRegistry, value);
    }

    public LoopViewModel? SelectedLoop
    {
        get => _selectedLoop;
        set => this.RaiseAndSetIfChanged(ref _selectedLoop, value);
    }

    public double TransientEngineWeight
    {
        get => _transientEngineWeight;
        set => this.RaiseAndSetIfChanged(ref _transientEngineWeight, value);
    }

    public double EnergyCurveWeight
    {
        get => _energyCurveWeight;
        set => this.RaiseAndSetIfChanged(ref _energyCurveWeight, value);
    }

    public object MinimalOverviewDeck => new object(); // Placeholder binding target for UI completeness

    public ReactiveCommand<Unit, Unit> TriggerAnalysisCommand { get; }
    public ReactiveCommand<Unit, Unit> NavTrackInfoCommand { get; }
    public ReactiveCommand<Unit, Unit> NavForensicsCommand { get; }
    public ReactiveCommand<Unit, Unit> ExportXmlCommand { get; }

    /// <summary>
    /// Loads a track's existing cues and properties from the database.
    /// </summary>
    public async Task LoadTrackDataAsync(string trackHash)
    {
        _trackHash = trackHash;
        using var db = await _contextFactory.CreateDbContextAsync();
        
        var track = await db.Tracks.FirstOrDefaultAsync(t => t.GlobalId == trackHash);
        if (track == null) return;

        TrackBpm = track.BPM ?? 120.0;
        TrackTotalDurationSeconds = track.CanonicalDuration ?? 240.0;
        TrackDownbeatAnchorSeconds = track.AnalysisOffset ?? 0.0;

        await ReloadCuesAsync(db);
    }

    private async Task ReloadCuesAsync(AppDbContext db)
    {
        var dbCues = await db.CuePoints
            .Where(c => c.TrackUniqueHash == _trackHash)
            .OrderBy(c => c.TimestampInSeconds)
            .ToListAsync();

        CalculatedCues.Clear();
        LoopRegistry.Clear();

        foreach (var c in dbCues)
        {
            var marker = new CueMarkerViewModel
            {
                Id = c.Id,
                Label = c.Label,
                TimestampInSeconds = c.TimestampInSeconds,
                Color = c.Color
            };
            CalculatedCues.Add(marker);

            // Populate loop registry with any loops
            if (c.Label.Contains("Loop"))
            {
                double len = 15.0;
                if (TrackBpm > 0)
                {
                    len = (60.0 / TrackBpm) * 16; // 4 bars
                }
                LoopRegistry.Add(new LoopViewModel
                {
                    Id = c.Id,
                    StartSeconds = c.TimestampInSeconds,
                    EndSeconds = c.TimestampInSeconds + len,
                    LoopLengthString = "4 Bars",
                    IsActiveLoop = true
                });
            }
        }
    }

    private async Task ExecuteRecalculationAsync()
    {
        if (string.IsNullOrEmpty(_trackHash)) return;

        using var db = await _contextFactory.CreateDbContextAsync();
        var track = await db.Tracks.FirstOrDefaultAsync(t => t.GlobalId == _trackHash);
        if (track == null || string.IsNullOrEmpty(track.LocalFilePath)) return;

        // Run full multi-tier DSP analysis pipeline
        var analysisResult = await _analysisPipeline.AnalyzeAsync(
            track.LocalFilePath, 
            (float)TrackBpm, 
            track.PrimaryGenre ?? "General");

        // Set weights inside generator (scaling parameters accordingly)
        // In a real application, weights adjust the threshold of K-Means and Energy boundaries
        
        // Generate Cues
        await _cueGenerationService.GenerateAndPersistCuesAsync(
            _trackHash, 
            analysisResult, 
            TrackDownbeatAnchorSeconds,
            track.VocalStartSeconds,
            track.VocalEndSeconds,
            track.VocalIntensity);

        await ReloadCuesAsync(db);
    }

    public async void CommitCueTimeUpdate(Guid cueId, double snappedTime)
    {
        using var db = await _contextFactory.CreateDbContextAsync();
        var cue = await db.CuePoints.FirstOrDefaultAsync(c => c.Id == cueId);
        if (cue != null)
        {
            cue.TimestampInSeconds = snappedTime;
            await db.SaveChangesAsync();
        }

        await ReloadCuesAsync(db);
    }

    private async Task ExecuteExportXmlAsync()
    {
        if (string.IsNullOrEmpty(_trackHash)) return;

        using var db = await _contextFactory.CreateDbContextAsync();
        var track = await db.Tracks.FirstOrDefaultAsync(t => t.GlobalId == _trackHash);
        if (track == null) return;

        var dbCues = await db.CuePoints
            .Where(c => c.TrackUniqueHash == _trackHash)
            .ToListAsync();

        var trackCuesMap = new Dictionary<string, List<CuePointEntity>>
        {
            [_trackHash] = dbCues
        };

        // Write directly to user's documents/export folder
        string exportPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), 
            $"ORBIT_Export_{_trackHash[..8]}.xml");

        _rekordboxExporter.ExportToXml(exportPath, new[] { track }, trackCuesMap);
        Console.WriteLine($"Exported Rekordbox XML successfully to: {exportPath}");
    }
}
