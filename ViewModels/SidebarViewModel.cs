using System;
using System.Reactive;
using System.Reactive.Disposables;
using ReactiveUI;
using SLSKDONET.Services;

namespace SLSKDONET.ViewModels
{
    public enum SidebarTab { Player, Inspector, Similarity }

    public class SidebarViewModel : ReactiveObject, IDisposable
    {
        private readonly IRightPanelService _rightPanelService;
        private readonly CompositeDisposable _disposables = new();

        private SidebarTab _activeTab = SidebarTab.Inspector;
        public SidebarTab ActiveTab
        {
            get => _activeTab;
            set
            {
                this.RaiseAndSetIfChanged(ref _activeTab, value);
                this.RaisePropertyChanged(nameof(IsPlayerTab));
                this.RaisePropertyChanged(nameof(IsInspectorTab));
                this.RaisePropertyChanged(nameof(IsSimilarityTab));
            }
        }

        public bool IsPlayerTab     => ActiveTab == SidebarTab.Player;
        public bool IsInspectorTab  => ActiveTab == SidebarTab.Inspector;
        public bool IsSimilarityTab => ActiveTab == SidebarTab.Similarity;

        // Tab switch commands
        public ReactiveCommand<Unit, Unit> SwitchToPlayerCommand     { get; }
        public ReactiveCommand<Unit, Unit> SwitchToInspectorCommand  { get; }
        public ReactiveCommand<Unit, Unit> SwitchToSimilarityCommand { get; }

        // Close command (ICommand — bindable in AXAML)
        public ReactiveCommand<Unit, Unit> CloseCommand { get; }

        // Sub-panel view models for the Player and Similarity tabs
        public PlayerViewModel       PlayerVm       { get; }
        public SimilarTracksViewModel SimilarTracksVm { get; }

        public SidebarViewModel(
            IRightPanelService rightPanelService,
            PlayerViewModel playerVm,
            SimilarTracksViewModel similarTracksVm)
        {
            _rightPanelService = rightPanelService;
            PlayerVm           = playerVm;
            SimilarTracksVm    = similarTracksVm;

            // Mirror RightPanelService reactive properties
            this.WhenAnyValue(x => x._rightPanelService.CurrentPanelVm)
                .Subscribe(vm =>
                {
                    this.RaisePropertyChanged(nameof(CurrentContent));

                    if (vm is PlaylistTrackViewModel playlistTrack)
                        _ = playlistTrack.LoadAnalysisDataAsync();

                    SimilarTracksVm.PrimeFromInspectorContext(vm);

                    if (vm is PlayerViewModel)
                        ActiveTab = SidebarTab.Player;
                    else if (vm != null && ActiveTab != SidebarTab.Similarity)
                        ActiveTab = SidebarTab.Inspector;
                })
                .DisposeWith(_disposables);
            this.WhenAnyValue(x => x._rightPanelService.IsPanelOpen)
                .Subscribe(_ => this.RaisePropertyChanged(nameof(IsVisible)))
                .DisposeWith(_disposables);
            this.WhenAnyValue(x => x._rightPanelService.ModeLabel)
                .Subscribe(_ => this.RaisePropertyChanged(nameof(ModeLabel)))
                .DisposeWith(_disposables);
            this.WhenAnyValue(x => x._rightPanelService.ModeIcon)
                .Subscribe(_ => this.RaisePropertyChanged(nameof(ModeIcon)))
                .DisposeWith(_disposables);

            SwitchToPlayerCommand     = ReactiveCommand.Create(() => { ActiveTab = SidebarTab.Player; _rightPanelService.IsPanelOpen = true; });
            SwitchToInspectorCommand  = ReactiveCommand.Create(() => { ActiveTab = SidebarTab.Inspector; _rightPanelService.IsPanelOpen = true; });
            SwitchToSimilarityCommand = ReactiveCommand.Create(() =>
            {
                SimilarTracksVm.PrimeFromInspectorContext(CurrentContent);
                ActiveTab = SidebarTab.Similarity;
                _rightPanelService.IsPanelOpen = true;
            });
            CloseCommand = ReactiveCommand.Create(() => _rightPanelService.ClosePanel());
        }

        public object? CurrentContent => _rightPanelService.CurrentPanelVm;
        public bool IsVisible => _rightPanelService.IsPanelOpen;
        public string? ModeLabel => _rightPanelService.ModeLabel;
        public string? ModeIcon => _rightPanelService.ModeIcon;

        /// <summary>Kept for backwards-compat with any code-behind callers.</summary>
        public void Close() => _rightPanelService.ClosePanel();

        public void Dispose() => _disposables.Dispose();
    }
}
