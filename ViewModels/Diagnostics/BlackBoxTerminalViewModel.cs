using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SLSKDONET.ViewModels.Diagnostics
{
    public record TerminalLogEntry(DateTime Timestamp, string Stage, string Level, string Message);

    public class BlackBoxTerminalViewModel : INotifyPropertyChanged, IDisposable
    {
        private readonly string _trackHash;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _tailTask;

        public ObservableCollection<TerminalLogEntry> TerminalLogs { get; } = new();

        public event PropertyChangedEventHandler? PropertyChanged;

        public BlackBoxTerminalViewModel(string trackHash)
        {
            _trackHash = trackHash;
            var logPath = ResolveLogFilePath(trackHash);
            
            if (logPath != null)
            {
                _tailTask = Task.Run(() => StartTailingAsync(logPath, _cts.Token));
            }
            else
            {
                _tailTask = Task.CompletedTask;
            }
        }

        private string? ResolveLogFilePath(string trackHash)
        {
            var baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ORBIT",
                "TrackLogs"
            );

            // Try current month first
            var currentMonthPath = Path.Combine(baseDir, DateTime.Now.ToString("yyyy-MM"), $"{trackHash}_audit.log");
            if (File.Exists(currentMonthPath)) return currentMonthPath;

            // Scan other monthly subdirs
            if (Directory.Exists(baseDir))
            {
                var directories = Directory.GetDirectories(baseDir, "????-??");
                foreach (var dir in directories.OrderByDescending(d => d))
                {
                    var path = Path.Combine(dir, $"{trackHash}_audit.log");
                    if (File.Exists(path)) return path;
                }
            }

            // Fallback: default folder/file
            var defaultFolder = Path.Combine(baseDir, DateTime.Now.ToString("yyyy-MM"));
            return Path.Combine(defaultFolder, $"{trackHash}_audit.log");
        }

        private async Task StartTailingAsync(string filePath, CancellationToken ct)
        {
            try
            {
                var dir = Path.GetDirectoryName(filePath);
                if (dir != null && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                if (!File.Exists(filePath))
                {
                    await File.WriteAllTextAsync(filePath, "", Encoding.UTF8, ct);
                }

                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(fs, Encoding.UTF8);

                string? line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    ParseAndAddLine(line);
                }

                while (!ct.IsCancellationRequested)
                {
                    bool hasNewContent = false;
                    while ((line = await reader.ReadLineAsync()) != null)
                    {
                        ParseAndAddLine(line);
                        hasNewContent = true;
                    }

                    if (!hasNewContent)
                    {
                        await Task.Delay(150, ct);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Clean exit
            }
            catch (Exception)
            {
                // Silent catch for background thread safety
            }
        }

        private static readonly Regex LogLineRegex = new(@"^\[(?<timestamp>[^\]]+)\]\s+(?<icon>\S+)\s+(?<rest>.+)$", RegexOptions.Compiled);
        private static readonly Regex StageRegex = new(@"^\[(?<stage>[^\]]+)\]\s*(?<msg>.+)$", RegexOptions.Compiled);

        private void ParseAndAddLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;

            var match = LogLineRegex.Match(line);
            if (!match.Success) return;

            var timestampStr = match.Groups["timestamp"].Value;
            var icon = match.Groups["icon"].Value;
            var rest = match.Groups["rest"].Value;

            if (!DateTime.TryParse(timestampStr, out var timestamp))
            {
                timestamp = DateTime.Now;
            }

            string stage = "GENERAL";
            string message = rest;
            var stageMatch = StageRegex.Match(rest);
            if (stageMatch.Success)
            {
                stage = stageMatch.Groups["stage"].Value;
                message = stageMatch.Groups["msg"].Value;
            }

            string level = "INFO";
            if (icon == "❌")
            {
                level = "ERROR";
            }
            else if (stage.StartsWith("CANDIDATE REJECTED") || stage.Contains("REJECTED"))
            {
                level = "REJECTED";
                stage = "CANDIDATE";
            }
            else if (stage.StartsWith("CANDIDATE ACCEPTED") || stage.Contains("ACCEPTED"))
            {
                level = "ACCEPTED";
                stage = "CANDIDATE";
            }
            else if (stage.StartsWith("CANDIDATE EVALUATED") || stage.Contains("EVALUATED"))
            {
                level = "INFO";
                stage = "CANDIDATE";
            }
            else if (stage.Equals("Spectral", StringComparison.OrdinalIgnoreCase))
            {
                level = "SPECTRAL";
            }
            else if (icon == "🚧" || icon == "⚠️" || stage.Contains("Stall") || stage.Contains("STALLED"))
            {
                level = "WARN";
            }

            var entry = new TerminalLogEntry(timestamp, stage.ToUpperInvariant(), level, message);

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                TerminalLogs.Add(entry);
                if (TerminalLogs.Count > 1000)
                {
                    TerminalLogs.RemoveAt(0);
                }
            });
        }

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public void Dispose()
        {
            _cts.Cancel();
            _cts.Dispose();
        }
    }
}
