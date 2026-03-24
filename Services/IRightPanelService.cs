using System;
using SLSKDONET.ViewModels;
using ReactiveUI;

namespace SLSKDONET.Services
{
    public interface IRightPanelService
    {
        object? CurrentPanelVm { get; }
        bool IsPanelOpen { get; set; }
        string? ModeLabel { get; }
        string? ModeIcon { get; }

        void OpenPanel(object vm, string label, string icon);
        void ClosePanel();
        void SetFallback(object vm, string label, string icon);
    }

    public class RightPanelService : ReactiveUI.ReactiveObject, IRightPanelService
    {
        private object? _currentPanelVm;
        private bool _isPanelOpen;
        private string? _modeLabel;
        private string? _modeIcon;

        private object? _fallbackVm;
        private string? _fallbackLabel;
        private string? _fallbackIcon;

        public object? CurrentPanelVm
        {
            get => _currentPanelVm;
            private set => this.RaiseAndSetIfChanged(ref _currentPanelVm, value);
        }

        public bool IsPanelOpen
        {
            get => _isPanelOpen;
            set => this.RaiseAndSetIfChanged(ref _isPanelOpen, value);
        }

        public string? ModeLabel
        {
            get => _modeLabel;
            private set => this.RaiseAndSetIfChanged(ref _modeLabel, value);
        }

        public string? ModeIcon
        {
            get => _modeIcon;
            private set => this.RaiseAndSetIfChanged(ref _modeIcon, value);
        }

        public void OpenPanel(object vm, string label, string icon)
        {
            CurrentPanelVm = vm;
            ModeLabel = label;
            ModeIcon = icon;
            IsPanelOpen = true;
        }

        public void ClosePanel()
        {
            if (_fallbackVm != null && CurrentPanelVm != _fallbackVm)
            {
                CurrentPanelVm = _fallbackVm;
                ModeLabel = _fallbackLabel;
                ModeIcon = _fallbackIcon;
            }
            else
            {
                IsPanelOpen = false;
            }
        }

        public void SetFallback(object vm, string label, string icon)
        {
            _fallbackVm = vm;
            _fallbackLabel = label;
            _fallbackIcon = icon;
            
            if (!IsPanelOpen || CurrentPanelVm == null)
            {
                CurrentPanelVm = vm;
                ModeLabel = label;
                ModeIcon = icon;
                // Don't auto-open if it's just a fallback (e.g. player)
            }
        }
    }
}
