using System;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using ReactiveUI;
using SLSKDONET.Services;

namespace SLSKDONET.ViewModels
{
    // Order matches the visual TabItem order in MainWindow.axaml's right-panel TabControl —
    // ActiveTabIndex below maps directly to this ordinal, driving TabControl.SelectedIndex.
    public enum SidebarTab { Inspector, Similarity, Player, Notifications, Mix }

    public class SidebarViewModel : ReactiveObject, IDisposable
    {
        private readonly IRightPanelService _rightPanelService;
        private readonly CompositeDisposable _disposables = new();
        private object? _lastInspectorContent;

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
                this.RaisePropertyChanged(nameof(IsNotificationsTab));
                this.RaisePropertyChanged(nameof(IsMixTab));
                this.RaisePropertyChanged(nameof(ActiveTabIndex));
            }
        }

        public bool IsPlayerTab        => ActiveTab == SidebarTab.Player;
        public bool IsInspectorTab     => ActiveTab == SidebarTab.Inspector;
        public bool IsSimilarityTab    => ActiveTab == SidebarTab.Similarity;
        public bool IsNotificationsTab => ActiveTab == SidebarTab.Notifications;
        public bool IsMixTab           => ActiveTab == SidebarTab.Mix;

        // TabControl.SelectedIndex has no enum overload — this is the int-typed mirror of
        // ActiveTab that the XAML actually binds to (two-way, so a manual tab click updates
        // ActiveTab too). ActiveTab itself was previously computed but never actually wired to
        // the TabControl at all — every tab-routing decision below had no visible effect, which
        // is the root cause behind notifications (and anything else not explicitly special-cased)
        // silently rendering wherever the Inspector tab's ContentControl happened to be showing.
        public int ActiveTabIndex
        {
            get => ActiveTab switch
            {
                SidebarTab.Inspector => 0,
                SidebarTab.Similarity => 1,
                SidebarTab.Player => 2,
                SidebarTab.Notifications => 3,
                SidebarTab.Mix => 4,
                _ => 0,
            };
            set
            {
                var newTab = value switch
                {
                    0 => SidebarTab.Inspector,
                    1 => SidebarTab.Similarity,
                    2 => SidebarTab.Player,
                    3 => SidebarTab.Notifications,
                    4 => SidebarTab.Mix,
                    _ => SidebarTab.Inspector,
                };
                if (newTab != ActiveTab) ActiveTab = newTab;
            }
        }

        // Tab switch commands
        public ReactiveCommand<Unit, Unit> SwitchToPlayerCommand     { get; }
        public ReactiveCommand<Unit, Unit> SwitchToInspectorCommand  { get; }
        public ReactiveCommand<Unit, Unit> SwitchToSimilarityCommand { get; }

        // Close command (ICommand — bindable in AXAML)
        public ReactiveCommand<Unit, Unit> CloseCommand { get; }

        // Notifications (bell/side panel)
        public ReactiveCommand<Unit, Unit> OpenNotificationsCommand { get; }

        // Sub-panel view models for the Player and Similarity tabs
        public PlayerViewModel       PlayerVm       { get; }
        public SimilarTracksViewModel SimilarTracksVm { get; }
        public NotificationCenterService NotificationCenter { get; }
        public MixTransitionViewModel MixTransitionVm { get; }

        public SidebarViewModel(
            IRightPanelService rightPanelService,
            PlayerViewModel playerVm,
            SimilarTracksViewModel similarTracksVm,
            NotificationCenterService notificationCenter,
            MixTransitionViewModel mixTransitionVm)
        {
            _rightPanelService = rightPanelService;
            PlayerVm           = playerVm;
            SimilarTracksVm    = similarTracksVm;
            NotificationCenter = notificationCenter;
            MixTransitionVm    = mixTransitionVm;
            MixTransitionVm.Closed += (_, _) => { if (ActiveTab == SidebarTab.Mix) ActiveTab = SidebarTab.Inspector; };

            // Mirror RightPanelService reactive properties
            this.WhenAnyValue(x => x._rightPanelService.CurrentPanelVm)
                .Subscribe(vm =>
                {
                    this.RaisePropertyChanged(nameof(CurrentContent));

                    if (vm is PlaylistTrackViewModel playlistTrack)
                        _ = playlistTrack.LoadAnalysisDataAsync();

                    if (vm is not null && vm is not PlayerViewModel && vm is not SimilarTracksViewModel && vm is not NotificationCenterService && vm is not MixTransitionViewModel)
                    {
                        _lastInspectorContent = vm;
                        SimilarTracksVm.PrimeFromInspectorContext(vm);
                    }

                    if (vm is PlayerViewModel)
                    {
                        ActiveTab = SidebarTab.Player;
                    }
                    else if (vm is SimilarTracksViewModel)
                    {
                        ActiveTab = SidebarTab.Similarity;
                    }
                    else if (vm is NotificationCenterService)
                    {
                        // Previously fell through to the generic Inspector branch below, since
                        // NotificationCenterService matched none of the explicit checks — that's
                        // why notifications rendered inside the "Inspector" tab instead of their
                        // own.
                        ActiveTab = SidebarTab.Notifications;
                    }
                    else if (vm is MixTransitionViewModel)
                    {
                        ActiveTab = SidebarTab.Mix;
                    }
                    else if (vm != null && ActiveTab != SidebarTab.Similarity && ActiveTab != SidebarTab.Mix)
                    {
                        // Mix is sticky like Similarity: while it's the active tab, selecting a
                        // track in the library (which still fires the normal single-track
                        // OpenInspectorEvent below) must not bounce the panel back to Inspector —
                        // that's what made Mix unusable as a "pick two tracks while staying put"
                        // workflow, since every click after the first kicked you out.
                        ActiveTab = SidebarTab.Inspector;
                    }
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

            // Open right sidebar when bridge between event is triggered (from Flow Builder/intelligence)
            ReactiveUI.MessageBus.Current.Listen<SLSKDONET.Events.FindBridgeBetweenTracksEvent>()
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(_ => _rightPanelService.OpenPanel(SimilarTracksVm, "SIMILAR TRACKS", "🔗"))
                .DisposeWith(_disposables);

            // Open right sidebar when "Find Similar" is requested for a single track (Library row button)
            ReactiveUI.MessageBus.Current.Listen<SLSKDONET.Events.FindSimilarTrackRequestEvent>()
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(_ =>
                {
                    ActiveTab = SidebarTab.Similarity;
                    _rightPanelService.OpenPanel(SimilarTracksVm, "SIMILAR TRACKS", "🔗");
                })
                .DisposeWith(_disposables);

            // Open the Mix tab when a transition badge is clicked in a playlist track list.
            ReactiveUI.MessageBus.Current.Listen<SLSKDONET.Events.OpenMixTransitionEvent>()
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(evt =>
                {
                    ActiveTab = SidebarTab.Mix;
                    _rightPanelService.OpenPanel(MixTransitionVm, "MIX", "🎛");
                    _ = MixTransitionVm.LoadPairAsync(evt.PlaylistId, evt.OutgoingPlaylistTrackId, evt.IncomingPlaylistTrackId);
                })
                .DisposeWith(_disposables);

            SwitchToPlayerCommand     = ReactiveCommand.Create(() =>
            {
                ActiveTab = SidebarTab.Player;
                _rightPanelService.OpenPanel(PlayerVm, "NOW PLAYING", "🎵");
            });
            SwitchToInspectorCommand  = ReactiveCommand.Create(() =>
            {
                ActiveTab = SidebarTab.Inspector;

                if (_lastInspectorContent != null)
                {
                    _rightPanelService.OpenPanel(_lastInspectorContent, "TRACK INSPECTOR", "🔬");
                }
                else
                {
                    _rightPanelService.IsPanelOpen = true;
                }
            });
            SwitchToSimilarityCommand = ReactiveCommand.Create(() =>
            {
                var context = _lastInspectorContent ?? CurrentContent;
                SimilarTracksVm.PrimeFromInspectorContext(context);
                ActiveTab = SidebarTab.Similarity;
                _rightPanelService.OpenPanel(SimilarTracksVm, "SIMILAR TRACKS", "🔗");
            });
            CloseCommand = ReactiveCommand.Create(() => _rightPanelService.ClosePanel());

            OpenNotificationsCommand = ReactiveCommand.Create(() =>
            {
                _rightPanelService.OpenPanel(NotificationCenter, "NOTIFICATIONS", "🔔");
            });
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
