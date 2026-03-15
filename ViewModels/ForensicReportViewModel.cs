using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using SLSKDONET.Services.Audio;
using SLSKDONET.Services;
using SLSKDONET.Views;

namespace SLSKDONET.ViewModels;

/// <summary>
/// ViewModel for displaying forensic analysis results for a track.
/// </summary>
public class ForensicReportViewModel : INotifyPropertyChanged
{
    private readonly AudioIntegrityService _integrityService;
    private readonly IDialogService _dialogService;

    public ForensicReportViewModel(AudioIntegrityService integrityService, IDialogService dialogService)
    {
        _integrityService = integrityService;
        _dialogService = dialogService;
        AnalyzeCommand = new AsyncRelayCommand(AnalyzeAsync);
    }

    private string _filePath = string.Empty;
    public string FilePath
    {
        get => _filePath;
        set { _filePath = value; OnPropertyChanged(); }
    }

    private string _artist = string.Empty;
    public string Artist
    {
        get => _artist;
        set { _artist = value; OnPropertyChanged(); }
    }

    private string _title = string.Empty;
    public string Title
    {
        get => _title;
        set { _title = value; OnPropertyChanged(); }
    }

    private bool _isAnalyzing;
    public bool IsAnalyzing
    {
        get => _isAnalyzing;
        set { _isAnalyzing = value; OnPropertyChanged(); }
    }

    private string _result = string.Empty;
    public string Result
    {
        get => _result;
        set { _result = value; OnPropertyChanged(); }
    }

    private double _highFreqEnergyDb;
    public double HighFreqEnergyDb
    {
        get => _highFreqEnergyDb;
        set { _highFreqEnergyDb = value; OnPropertyChanged(); }
    }

    private double _lowFreqEnergyDb;
    public double LowFreqEnergyDb
    {
        get => _lowFreqEnergyDb;
        set { _lowFreqEnergyDb = value; OnPropertyChanged(); }
    }

    private double _energyRatio;
    public double EnergyRatio
    {
        get => _energyRatio;
        set { _energyRatio = value; OnPropertyChanged(); }
    }

    public ICommand AnalyzeCommand { get; }

    public void SetTrack(string filePath, string artist, string title)
    {
        FilePath = filePath;
        Artist = artist;
        Title = title;
    }

    private async Task AnalyzeAsync()
    {
        if (string.IsNullOrEmpty(FilePath))
            return;

        try
        {
            IsAnalyzing = true;
            Result = "Analyzing...";

            var integrityResult = await Task.Run(() => _integrityService.CheckSpectralIntegrityAsync(FilePath));

            HighFreqEnergyDb = integrityResult.HighFreqEnergyDb;
            LowFreqEnergyDb = integrityResult.LowFreqEnergyDb;
            EnergyRatio = HighFreqEnergyDb - LowFreqEnergyDb;

            Result = integrityResult.IsGenuineLossless
                ? "✅ Genuine Lossless Recording"
                : "⚠️ Suspicious - Possible Transcode";

            Result += $"\n\nHigh Frequency Energy (16kHz+): {HighFreqEnergyDb:F1} dB" +
                     $"\nLow Frequency Energy (1kHz-15kHz): {LowFreqEnergyDb:F1} dB" +
                     $"\nEnergy Difference: {EnergyRatio:F1} dB" +
                     $"\n\n{integrityResult.Reason}";
        }
        catch (Exception ex)
        {
            Result = $"Analysis failed: {ex.Message}";
        }
        finally
        {
            IsAnalyzing = false;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}