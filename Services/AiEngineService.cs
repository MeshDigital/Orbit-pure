using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace SLSKDONET.Services;

public enum AiEngineStatus
{
    NotInstalled,
    Checking,
    Installing,
    Ready,
    Running,
    Error
}

/// <summary>
/// Checks, installs, and launches the optional EDMFormer phrase-detection microservice.
/// Exposes status via INotifyPropertyChanged so the Settings UI can bind directly.
/// </summary>
public sealed class AiEngineService : INotifyPropertyChanged
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };

    private AiEngineStatus _status = AiEngineStatus.NotInstalled;
    private string _statusText = "Not yet checked";
    private CancellationTokenSource? _pollCts;

    public event PropertyChangedEventHandler? PropertyChanged;

    public AiEngineStatus Status
    {
        get => _status;
        private set { if (_status != value) { _status = value; Notify(); Notify(nameof(IsAvailable)); } }
    }

    public string StatusText
    {
        get => _statusText;
        private set { if (_statusText != value) { _statusText = value; Notify(); } }
    }

    public bool IsAvailable => _status == AiEngineStatus.Running;

    // ── Paths ───────────────────────────────────────────────────────────────

    private static string AppDir =>
        Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)
        ?? AppContext.BaseDirectory;

    private static string InstallerScript => Path.Combine(AppDir, "Tools", "install_edmformer.ps1");
    private static string ServerScript    => Path.Combine(AppDir, "Tools", "edmformer_server.py");
    private static string ModelWeights    => Path.Combine(AppDir, "Tools", "EDMFormer", "src",
                                                          "SongFormer", "ckpts", "SongFormer.safetensors");

    // ── Public API ──────────────────────────────────────────────────────────

    public async Task CheckStatusAsync()
    {
        SetStatus(AiEngineStatus.Checking, "Checking AI engine...");
        try
        {
            if (!await IsCondaAvailableAsync())
            {
                SetStatus(AiEngineStatus.NotInstalled, "conda not found — install Miniconda first");
                return;
            }
            if (!await CondaEnvExistsAsync("edmformer"))
            {
                SetStatus(AiEngineStatus.NotInstalled, "conda env 'edmformer' missing — click Install");
                return;
            }
            if (!File.Exists(ModelWeights))
            {
                SetStatus(AiEngineStatus.NotInstalled, "Model weights not downloaded — click Install");
                return;
            }
            if (await ProbeHealthEndpointAsync())
            {
                SetStatus(AiEngineStatus.Running, "AI engine running — phrase detection active");
                return;
            }
            SetStatus(AiEngineStatus.Ready, "Installed — server not running (click Start)");
        }
        catch (Exception ex)
        {
            SetStatus(AiEngineStatus.Error, $"Check failed: {ex.Message}");
        }
    }

    public async Task StartInstallAsync()
    {
        if (!File.Exists(InstallerScript))
        {
            SetStatus(AiEngineStatus.Error, $"Installer not found: {InstallerScript}");
            return;
        }

        SetStatus(AiEngineStatus.Installing, "Installing — follow progress in the terminal window...");

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName  = "pwsh.exe",
                Arguments = $"-ExecutionPolicy Bypass -File \"{InstallerScript}\"",
                UseShellExecute = true,
                WorkingDirectory = AppDir
            };
            Process.Start(psi);

            // Poll every 5 s until status leaves Installing
            _pollCts?.Cancel();
            _pollCts = new CancellationTokenSource();
            var token = _pollCts.Token;
            _ = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested && _status == AiEngineStatus.Installing)
                {
                    await Task.Delay(5_000, token).ConfigureAwait(false);
                    if (!token.IsCancellationRequested)
                        await CheckStatusAsync().ConfigureAwait(false);
                }
            }, token);
        }
        catch (Exception ex)
        {
            SetStatus(AiEngineStatus.Error, $"Install launch failed: {ex.Message}");
        }
    }

    public async Task StartServerAsync()
    {
        if (!File.Exists(ServerScript))
        {
            SetStatus(AiEngineStatus.Error, $"Server script not found: {ServerScript}");
            return;
        }
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName  = "cmd.exe",
                Arguments = $"/c start \"ORBIT AI Engine\" conda run -n edmformer python \"{ServerScript}\"",
                UseShellExecute = true,
                WorkingDirectory = AppDir
            };
            Process.Start(psi);
            SetStatus(AiEngineStatus.Checking, "Starting server...");
            await Task.Delay(3_000).ConfigureAwait(false);
            await CheckStatusAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            SetStatus(AiEngineStatus.Error, $"Server start failed: {ex.Message}");
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private void SetStatus(AiEngineStatus status, string text)
    {
        Dispatcher.UIThread.Post(() =>
        {
            StatusText = text;
            Status     = status;
        });
    }

    private void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static async Task<bool> IsCondaAvailableAsync()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe", Arguments = "/c where conda",
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            await p.WaitForExitAsync().ConfigureAwait(false);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    private static async Task<bool> CondaEnvExistsAsync(string envName)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "conda", Arguments = "env list",
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            string output = await p.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            await p.WaitForExitAsync().ConfigureAwait(false);
            foreach (var line in output.Split('\n'))
            {
                var t = line.TrimStart();
                if (t.StartsWith(envName, StringComparison.OrdinalIgnoreCase)
                    && (t.Length == envName.Length || char.IsWhiteSpace(t[envName.Length])))
                    return true;
            }
            return false;
        }
        catch { return false; }
    }

    private static async Task<bool> ProbeHealthEndpointAsync()
    {
        try
        {
            var json = await _http.GetStringAsync("http://127.0.0.1:7774/health").ConfigureAwait(false);
            return json.Contains("ready", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}
