using System;
using System.Reactive.Linq;
using System.Windows.Input;
using ReactiveUI;
using SLSKDONET.Models;

namespace SLSKDONET.ViewModels.Library
{
    public class CreateSmartPlaylistViewModel : ReactiveObject
    {
        private string _name = "New Smart Playlist";
        public string Name
        {
            get => _name;
            set => this.RaiseAndSetIfChanged(ref _name, value);
        }

        private double? _minEnergy;
        public double? MinEnergy
        {
            get => _minEnergy;
            set
            {
                this.RaiseAndSetIfChanged(ref _minEnergy, value);
                this.RaisePropertyChanged(nameof(PreviewProfile));
            }
        }

        private double? _maxEnergy;
        public double? MaxEnergy
        {
            get => _maxEnergy;
            set
            {
                this.RaiseAndSetIfChanged(ref _maxEnergy, value);
                this.RaisePropertyChanged(nameof(PreviewProfile));
            }
        }
        
        private double? _minValence;
        public double? MinValence
        {
            get => _minValence;
            set
            {
                this.RaiseAndSetIfChanged(ref _minValence, value);
                this.RaisePropertyChanged(nameof(PreviewProfile));
            }
        }

        private double? _maxValence;
        public double? MaxValence
        {
            get => _maxValence;
            set
            {
                this.RaiseAndSetIfChanged(ref _maxValence, value);
                this.RaisePropertyChanged(nameof(PreviewProfile));
            }
        }
        
        private double? _minBpm;
        public double? MinBpm
        {
            get => _minBpm;
            set => this.RaiseAndSetIfChanged(ref _minBpm, value);
        }
        
        private double? _maxBpm;
        public double? MaxBpm
        {
            get => _maxBpm;
            set => this.RaiseAndSetIfChanged(ref _maxBpm, value);
        }
        
        public SonicProfileData PreviewProfile
        {
            get
            {
                // Calculate average target logic for preview
                var e = (MinEnergy ?? 0 + MaxEnergy ?? 1.0) / 2.0;
                if (MinEnergy.HasValue && !MaxEnergy.HasValue) e = MinEnergy.Value;
                if (!MinEnergy.HasValue && MaxEnergy.HasValue) e = MaxEnergy.Value;
                
                var v = (MinValence ?? 0 + MaxValence ?? 1.0) / 2.0;
                if (MinValence.HasValue && !MaxValence.HasValue) v = MinValence.Value;
                if (!MinValence.HasValue && MaxValence.HasValue) v = MaxValence.Value;

                return new SonicProfileData(e, v, 0.0); // Assuming vocal toggle not yet in criteria
            }
        }

        private string? _genre;
        public string? Genre
        {
            get => _genre;
            set => this.RaiseAndSetIfChanged(ref _genre, value);
        }

        public ICommand SaveCommand { get; }
        public ICommand CancelCommand { get; }

        public event EventHandler<SmartPlaylistCriteria>? OnSave;
        public event EventHandler? OnCancel;

        public CreateSmartPlaylistViewModel()
        {
            SaveCommand = ReactiveCommand.Create(Save);
            CancelCommand = ReactiveCommand.Create(Cancel);
        }

        private void Save()
        {
            var criteria = new SmartPlaylistCriteria
            {
                MinEnergy = MinEnergy,
                MaxEnergy = MaxEnergy,
                MinValence = MinValence,
                MaxValence = MaxValence,
                MinBPM = MinBpm,
                MaxBPM = MaxBpm,
                Genre = Genre
            };

            OnSave?.Invoke(this, criteria);
        }

        private void Cancel()
        {
            OnCancel?.Invoke(this, EventArgs.Empty);
        }
    }
}
