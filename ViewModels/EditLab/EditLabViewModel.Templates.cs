using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using ReactiveUI;
using SLSKDONET.Services.EditLab;

namespace SLSKDONET.ViewModels.EditLab;

/// <summary>Templates: starting points that lay out ordinary, fully editable clips.</summary>
public sealed partial class EditLabViewModel
{
    public IReadOnlyList<EditTemplateInfo> Templates => EditTemplates.Catalog;

    private EditTemplateInfo _selectedTemplate = EditTemplates.Catalog[0];
    public EditTemplateInfo SelectedTemplate
    {
        get => _selectedTemplate;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedTemplate, value ?? EditTemplates.Catalog[0]);
            RaiseTemplateProperties();
        }
    }

    private EditSourceItemViewModel? _templateTrackA;
    public EditSourceItemViewModel? TemplateTrackA { get => _templateTrackA; set { this.RaiseAndSetIfChanged(ref _templateTrackA, value); RaiseTemplateProperties(); } }

    private EditSourceItemViewModel? _templateTrackB;
    public EditSourceItemViewModel? TemplateTrackB { get => _templateTrackB; set { this.RaiseAndSetIfChanged(ref _templateTrackB, value); RaiseTemplateProperties(); } }

    private decimal? _templateBars = 16;
    public decimal? TemplateBars { get => _templateBars; set => this.RaiseAndSetIfChanged(ref _templateBars, value); }

    /// <summary>1-based start bar for range templates.</summary>
    private decimal? _templateStartBar = 33;
    public decimal? TemplateStartBar { get => _templateStartBar; set => this.RaiseAndSetIfChanged(ref _templateStartBar, value); }

    private decimal? _templateRepeats = 2;
    public decimal? TemplateRepeats { get => _templateRepeats; set => this.RaiseAndSetIfChanged(ref _templateRepeats, value); }

    public bool TemplateNeedsTrackB => SelectedTemplate.NeedsTrackB;
    public bool TemplateUsesBars => SelectedTemplate.UsesBars;
    public bool TemplateUsesStartBar => SelectedTemplate.UsesStartBar;
    public bool TemplateUsesRepeats => SelectedTemplate.UsesRepeats;
    public string TemplateDescription => SelectedTemplate.Description;

    /// <summary>Why "Apply" can't run, or empty when it can.</summary>
    public string TemplateBlocker =>
        !HasProject ? "Open an edit first."
        : TemplateTrackA == null ? "Add a track to the bin, then pick it as track A."
        : SelectedTemplate.NeedsTrackB && (TemplateTrackB == null || TemplateTrackB.Hash == TemplateTrackA.Hash) ? "Add a second track and pick it as track B."
        : "";
    public bool HasTemplateBlocker => TemplateBlocker.Length > 0;
    public bool CanApplyTemplate => TemplateBlocker.Length == 0;

    public ReactiveCommand<Unit, Unit> ApplyTemplateCommand { get; private set; } = null!;

    private void InitializeTemplates()
    {
        ApplyTemplateCommand = ReactiveCommand.Create(ApplyTemplate, this.WhenAnyValue(x => x.CanApplyTemplate));
    }

    private void RaiseTemplateProperties()
    {
        // Default the track pickers to the base and the next track in the bin.
        if (_templateTrackA == null || !Sources.Contains(_templateTrackA))
            _templateTrackA = Sources.FirstOrDefault(s => s.IsBase) ?? Sources.FirstOrDefault();
        if (_templateTrackB == null || !Sources.Contains(_templateTrackB) || _templateTrackB == _templateTrackA)
            _templateTrackB = Sources.FirstOrDefault(s => s != _templateTrackA);
        foreach (var name in new[]
                 {
                     nameof(TemplateTrackA), nameof(TemplateTrackB), nameof(TemplateNeedsTrackB), nameof(TemplateUsesBars),
                     nameof(TemplateUsesStartBar), nameof(TemplateUsesRepeats), nameof(TemplateDescription),
                     nameof(TemplateBlocker), nameof(HasTemplateBlocker), nameof(CanApplyTemplate),
                 })
            this.RaisePropertyChanged(name);
    }

    /// <summary>Lays the selected template out (replacing the arrangement) as one undo step.</summary>
    public void ApplyTemplate()
    {
        if (!CanApplyTemplate || TemplateTrackA == null) return;
        var options = new EditTemplateOptions(
            SelectedTemplate.Kind,
            TemplateTrackA.Hash,
            TemplateTrackB?.Hash,
            (int)Math.Round(TemplateBars ?? 16),
            Math.Max(0, (double)(TemplateStartBar ?? 1) - 1),
            (int)Math.Round(TemplateRepeats ?? 2));

        string summary;
        try
        {
            // Validate on a copy first so a bad choice never leaves a half-built arrangement.
            var probe = Services.EditLab.EditProjectStore.Deserialize(Services.EditLab.EditProjectStore.Serialize(Editor.Project));
            summary = EditTemplates.Apply(probe, options);
        }
        catch (InvalidOperationException ex)
        {
            Status = ex.Message;
            return;
        }

        Editor.Mutate(p => EditTemplates.Apply(p, options));
        SelectedClip = null;
        PlayheadBar = 0;
        Status = summary + (HasProblems ? " Some clips need stems: separate them in the bin." : "") + " Everything stays editable; Ctrl+Z undoes it.";
    }
}
