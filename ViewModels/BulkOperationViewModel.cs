using System;
using System.ComponentModel;
using System.Reactive.Disposables;
using System.Runtime.CompilerServices;
using SLSKDONET.Services;
using Avalonia.Threading;

namespace SLSKDONET.ViewModels
{
    public class BulkOperationViewModel : INotifyPropertyChanged, IDisposable
    {
        private readonly IEventBus _eventBus;
        private readonly CompositeDisposable _disposables = new();
        
        private string _title = "Bulk Operation";
        public string Title
        {
            get => _title;
            set => SetProperty(ref _title, value);
        }

        private int _total;
        public int Total
        {
            get => _total;
            set => SetProperty(ref _total, value);
        }

        private int _processed;
        public int Processed
        {
            get => _processed;
            set => SetProperty(ref _processed, value);
        }

        private bool _isVisible;
        public bool IsVisible
        {
            get => _isVisible;
            set => SetProperty(ref _isVisible, value);
        }

        public double ProgressPercent => Total > 0 ? (double)Processed / Total * 100 : 0;

        public event PropertyChangedEventHandler? PropertyChanged;

        public BulkOperationViewModel(IEventBus eventBus)
        {
            _eventBus = eventBus;
            
            _eventBus.GetEvent<BulkOperationStartedEvent>().Subscribe(OnStarted).DisposeWith(_disposables);
            _eventBus.GetEvent<BulkOperationProgressEvent>().Subscribe(OnProgress).DisposeWith(_disposables);
            _eventBus.GetEvent<BulkOperationCompletedEvent>().Subscribe(OnCompleted).DisposeWith(_disposables);
        }

        private void OnStarted(BulkOperationStartedEvent evt)
        {
            Dispatcher.UIThread.Post(() =>
            {
                Title = evt.Title;
                Total = evt.TotalCount;
                Processed = 0;
                IsVisible = true;
            });
        }

        private void OnProgress(BulkOperationProgressEvent evt)
        {
            Dispatcher.UIThread.Post(() =>
            {
                Processed = evt.Processed;
                Total = evt.Total; // Ensure total is synced if it changed (rare)
                OnPropertyChanged(nameof(ProgressPercent));
            });
        }

        private void OnCompleted(BulkOperationCompletedEvent evt)
        {
            Dispatcher.UIThread.Post(async () =>
            {
                Processed = Total;
                OnPropertyChanged(nameof(ProgressPercent));
                
                // Keep visible for a moment to show 100%
                await System.Threading.Tasks.Task.Delay(1500);
                IsVisible = false;
            });
        }

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (System.Collections.Generic.EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        public void Dispose()
        {
            _disposables.Dispose();
        }
    }
}
