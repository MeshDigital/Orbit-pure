using System;
using ReactiveUI;
using SLSKDONET.Services;

namespace SLSKDONET.ViewModels
{
    public class SidebarViewModel : ReactiveObject
    {
        private readonly IRightPanelService _rightPanelService;

        public SidebarViewModel(IRightPanelService rightPanelService)
        {
            _rightPanelService = rightPanelService;

            // Simple proxy properties for binding
            this.WhenAnyValue(x => x._rightPanelService.CurrentPanelVm).Subscribe(_ => this.RaisePropertyChanged(nameof(CurrentContent)));
            this.WhenAnyValue(x => x._rightPanelService.IsPanelOpen).Subscribe(_ => this.RaisePropertyChanged(nameof(IsVisible)));
            this.WhenAnyValue(x => x._rightPanelService.ModeLabel).Subscribe(_ => this.RaisePropertyChanged(nameof(ModeLabel)));
            this.WhenAnyValue(x => x._rightPanelService.ModeIcon).Subscribe(_ => this.RaisePropertyChanged(nameof(ModeIcon)));
        }

        public object? CurrentContent => _rightPanelService.CurrentPanelVm;
        public bool IsVisible => _rightPanelService.IsPanelOpen;
        public string? ModeLabel => _rightPanelService.ModeLabel;
        public string? ModeIcon => _rightPanelService.ModeIcon;

        public void Close() => _rightPanelService.ClosePanel();
    }
}
