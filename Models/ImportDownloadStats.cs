using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SLSKDONET.Models
{
    /// <summary>
    /// Statistics for download status of an import/playlist.
    /// Tracks completed, in-progress, queued, and failed counts.
    /// </summary>
    public class ImportDownloadStats : INotifyPropertyChanged
    {
        private int _completed;
        private int _inProgress;
        private int _queued;
        private int _failed;
        private int _total;

        public int Completed
        {
            get => _completed;
            set
            {
                if (SetProperty(ref _completed, value))
                {
                    OnPropertyChanged(nameof(ProgressPercentage));
                }
            }
        }

        public int InProgress
        {
            get => _inProgress;
            set
            {
                if (SetProperty(ref _inProgress, value))
                {
                    OnPropertyChanged(nameof(ProgressPercentage));
                }
            }
        }

        public int Queued
        {
            get => _queued;
            set => SetProperty(ref _queued, value);
        }

        public int Failed
        {
            get => _failed;
            set
            {
                if (SetProperty(ref _failed, value))
                {
                    OnPropertyChanged(nameof(ProgressPercentage));
                }
            }
        }

        public int Total
        {
            get => _total;
            set => SetProperty(ref _total, value);
        }

        /// <summary>
        /// Overall progress percentage (0-100).
        /// Completed / Total * 100
        /// </summary>
        public double ProgressPercentage
        {
            get
            {
                if (Total == 0) return 0;
                return (Completed / (double)Total) * 100;
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }
}
