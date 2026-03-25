using System;
using System.Reactive.Disposables;
using ReactiveUI;
using SLSKDONET.Services;

namespace SLSKDONET.ViewModels
{
    public class SidebarViewModel : ReactiveObject, IDisposable
    {
        private readonly IRightPanelService _rightPanelService;
        private readonly CompositeDisposable _disposables = new();

        public SidebarViewModel(IRightPanelService rightPanelService)
        {
            _rightPanelService = rightPanelService;

            // Simple proxy properties for binding
            this.WhenAnyValue(x => x._rightPanelService.CurrentPanelVm)
                .Subscribe(_ => this.RaisePropertyChanged(nameof(CurrentContent)))
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
        }

        public object? CurrentContent => _rightPanelService.CurrentPanelVm;
        public bool IsVisible => _rightPanelService.IsPanelOpen;
        public string? ModeLabel => _rightPanelService.ModeLabel;
        public string? ModeIcon => _rightPanelService.ModeIcon;

        public void Close() => _rightPanelService.ClosePanel();

        public void Dispose() => _disposables.Dispose();
    }
}
