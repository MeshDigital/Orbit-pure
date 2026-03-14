using ReactiveUI;

namespace SLSKDONET.Models
{
    public class FileMoveOperation : ReactiveObject
    {
        public string SourcePath { get; set; } = string.Empty;
        public string DestinationPath { get; set; } = string.Empty;
        
        // Display Metadata
        public string TrackTitle { get; set; } = string.Empty;
        public string Artist { get; set; } = string.Empty;
        public string PredictedVibe { get; set; } = string.Empty;
        public double Confidence { get; set; }

        private bool _isChecked = true;
        public bool IsChecked
        {
            get => _isChecked;
            set => this.RaiseAndSetIfChanged(ref _isChecked, value);
        }
        
        // Status tracking
        private string _status = "Pending";
        public string Status
        {
            get => _status;
            set => this.RaiseAndSetIfChanged(ref _status, value);
        }

        private string _errorMessage = string.Empty;
        public string ErrorMessage
        {
            get => _errorMessage;
            set => this.RaiseAndSetIfChanged(ref _errorMessage, value);
        }

        public bool IsCollisionExpected { get; set; }

        public string DisplayName => $"{Artist} - {TrackTitle}";
        public string ChangeSummary => $"{GetDir(SourcePath)} -> {GetDir(DestinationPath)}";

        private string GetDir(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            try
            {
                var dir = System.IO.Path.GetDirectoryName(path);
                var parent = System.IO.Path.GetFileName(dir);
                return parent ?? dir ?? "";
            }
            catch { return path; }
        }
    }
}
