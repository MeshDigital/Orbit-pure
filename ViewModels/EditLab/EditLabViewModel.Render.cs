using System;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using SLSKDONET.Models.EditLab;
using SLSKDONET.Services.EditLab;
using SLSKDONET.Views;

namespace SLSKDONET.ViewModels.EditLab;

/// <summary>Render: offline render of the project (or its loop region) to a new library track.</summary>
public sealed partial class EditLabViewModel
{
    private readonly EditLabRenderService? _renderService;
    private CancellationTokenSource? _renderCts;

    public bool CanRenderAtAll => _renderService != null;

    private bool _renderAsFlac;
    public bool RenderAsFlac { get => _renderAsFlac; set { this.RaiseAndSetIfChanged(ref _renderAsFlac, value); this.RaisePropertyChanged(nameof(RenderAsWav)); } }
    public bool RenderAsWav { get => !_renderAsFlac; set => RenderAsFlac = !value; }

    private bool _renderLoopOnly;
    public bool RenderLoopOnly { get => _renderLoopOnly; set => this.RaiseAndSetIfChanged(ref _renderLoopOnly, value); }

    private bool _renderWriteCues = true;
    public bool RenderWriteCues { get => _renderWriteCues; set => this.RaiseAndSetIfChanged(ref _renderWriteCues, value); }

    private bool _isRendering;
    public bool IsRendering
    {
        get => _isRendering;
        private set { this.RaiseAndSetIfChanged(ref _isRendering, value); this.RaisePropertyChanged(nameof(IsNotRendering)); }
    }
    public bool IsNotRendering => !IsRendering;

    private double _renderProgress;
    public double RenderProgress { get => _renderProgress; private set => this.RaiseAndSetIfChanged(ref _renderProgress, value); }

    public string RenderOutputFolder => _renderService?.OutputDirectory ?? "";

    public string RenderSummary =>
        Editor.Project.BaseSource is { } b
            ? $"\"{b.Title} (ORBIT Edit)\" by {(string.IsNullOrWhiteSpace(b.Artist) ? "ORBIT Edit Lab" : b.Artist)}, {Editor.Project.Bpm:0.##} BPM{(Editor.Project.Key is { } k ? ", " + k : "")}"
            : "Add a base track first.";

    public ReactiveCommand<Unit, Unit> RenderCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> CancelRenderCommand { get; private set; } = null!;

    private void InitializeRender()
    {
        var canRender = this.WhenAnyValue(x => x.HasProject, x => x.IsRendering, (p, r) => p && !r && _renderService != null);
        RenderCommand = ReactiveCommand.CreateFromTask(RenderAsync, canRender);
        CancelRenderCommand = ReactiveCommand.Create(() => _renderCts?.Cancel(), this.WhenAnyValue(x => x.IsRendering));
    }

    private void RaiseRenderProperties()
    {
        this.RaisePropertyChanged(nameof(RenderSummary));
        if (RenderLoopOnly && !HasLoop) RenderLoopOnly = false;
    }

    public async Task RenderAsync()
    {
        if (_renderService == null || !HasProject || IsRendering) return;
        if (RenderLoopOnly && !HasLoop)
        {
            Status = "Set a loop region first (drag across the bar ruler), or turn off \"Loop region only\".";
            return;
        }

        StopPreview();
        var cts = _renderCts = new CancellationTokenSource();
        IsRendering = true;
        RenderProgress = 0;
        Status = "Rendering…";
        var project = EditProjectStore.Deserialize(EditProjectStore.Serialize(Editor.Project));
        try
        {
            var progress = new Progress<double>(p => RenderProgress = p);
            var request = new EditRenderRequest(project, RenderAsFlac ? EditRenderFormat.Flac : EditRenderFormat.Wav, RenderLoopOnly, RenderWriteCues);
            var outcome = await Task.Run(() => _renderService.RenderToLibraryAsync(request, progress, cts.Token), cts.Token);

            await _ui(() =>
            {
                // Provenance on the project side: which tracks this edit produced.
                Editor.Project.Renders.Add(new EditRenderRecord { TrackHash = outcome.TrackHash, FilePath = outcome.FilePath, LoopRegionOnly = RenderLoopOnly });
                var extra = outcome.Warnings.Count > 0 ? " " + string.Join(" ", outcome.Warnings) : "";
                var cues = outcome.CuesWritten > 0 ? $", {outcome.CuesWritten} cues" : "";
                Status = $"Rendered \"{outcome.Title}\" ({CueForgeGrid.FormatTime(outcome.Result.DurationSeconds)}{cues}). It's in the library and queued for analysis.{extra}";
                _notifications?.Show("Edit rendered",
                    $"\"{outcome.Title}\" was added to the library and queued for analysis.{extra}",
                    outcome.Warnings.Count > 0 ? NotificationType.Warning : NotificationType.Success);
            });
            await SaveNowAsync();
        }
        catch (OperationCanceledException)
        {
            Status = "Render cancelled. No file was added.";
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Edit Lab: render failed");
            Status = $"Render failed: {ex.Message}";
            _notifications?.Show("Render failed", ex.Message, NotificationType.Error);
        }
        finally
        {
            IsRendering = false;
            cts.Dispose();
            if (ReferenceEquals(_renderCts, cts)) _renderCts = null;
        }
    }
}
