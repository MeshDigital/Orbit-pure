using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Models;
using System.Reactive.Linq;
using System.Reactive.Subjects;

namespace SLSKDONET.Services.Library;

public class ColumnConfigurationService : IDisposable
{
    private readonly ILogger<ColumnConfigurationService> _logger;
    private readonly string _configPath;
    private readonly Subject<List<ColumnDefinition>> _saveSubject = new();
    private readonly IDisposable _saveSubscription;

    public ColumnConfigurationService(ILogger<ColumnConfigurationService> logger)
    {
        _logger = logger;
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _configPath = Path.Combine(appData, "ORBIT", "column_config.json");

        _saveSubscription = _saveSubject
            .Throttle(TimeSpan.FromSeconds(2))
            .Subscribe(async config => await SaveToFileAsync(config));
    }

    public async Task<List<ColumnDefinition>> LoadConfigurationAsync()
    {
        try
        {
            if (!File.Exists(_configPath))
            {
                return GetDefaultConfiguration();
            }

            var json = await File.ReadAllTextAsync(_configPath);
            var config = JsonSerializer.Deserialize<List<ColumnDefinition>>(json);
            return config ?? GetDefaultConfiguration();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load column configuration");
            return GetDefaultConfiguration();
        }
    }

    public void SaveConfiguration(List<ColumnDefinition> config)
    {
        _saveSubject.OnNext(config);
    }

    private async Task SaveToFileAsync(List<ColumnDefinition> config)
    {
        try
        {
            var directory = Path.GetDirectoryName(_configPath);
            if (directory != null) Directory.CreateDirectory(directory);

            var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            await FileExtensions.WriteToSafeAsync(_configPath, json);
            _logger.LogInformation("Column configuration saved to {Path}", _configPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save column configuration");
        }
    }

    public List<ColumnDefinition> GetDefaultConfiguration()
    {
        return new List<ColumnDefinition>
        {
            new() { Id = "Status", Header = " ", Width = 40, DisplayOrder = 0, IsVisible = true, PropertyPath = "StatusSymbol", CanSort = true },
            new() { Id = "Artist", Header = "Artist", Width = 200, DisplayOrder = 1, IsVisible = true, PropertyPath = "Artist", CanSort = true },
            new() { Id = "Title", Header = "Title", Width = 250, DisplayOrder = 2, IsVisible = true, PropertyPath = "Title", CanSort = true },
            new() { Id = "Duration", Header = "Time", Width = 80, DisplayOrder = 3, IsVisible = true, PropertyPath = "DurationFormatted", CanSort = true, DataType = typeof(TimeSpan) },
            new() { Id = "BPM", Header = "BPM", Width = 70, DisplayOrder = 4, IsVisible = true, PropertyPath = "BPM", CanSort = true, DataType = typeof(double), CellTemplateKey = "BpmTemplate" },
            new() { Id = "Key", Header = "Key", Width = 60, DisplayOrder = 5, IsVisible = true, PropertyPath = "MusicalKey", CanSort = true, CellTemplateKey = "KeyTemplate" },
            new() { Id = "Bitrate", Header = "Bitrate", Width = 80, DisplayOrder = 6, IsVisible = true, PropertyPath = "BitrateFormatted", CanSort = true },
            new() { Id = "Format", Header = "Format", Width = 70, DisplayOrder = 7, IsVisible = false, PropertyPath = "Format", CanSort = true },
            new() { Id = "Album", Header = "Album", Width = 200, DisplayOrder = 8, IsVisible = true, PropertyPath = "Album", CanSort = true },
            new() { Id = "Genres", Header = "Genres", Width = 150, DisplayOrder = 9, IsVisible = false, PropertyPath = "Genres", CanSort = true },
            new() { Id = "AddedAt", Header = "Added", Width = 120, DisplayOrder = 10, IsVisible = false, PropertyPath = "AddedAt", CanSort = true, DataType = typeof(DateTime) }
        };
    }

    public void Dispose()
    {
        _saveSubscription.Dispose();
        _saveSubject.Dispose();
    }
}

public static class FileExtensions
{
    public static async Task WriteToSafeAsync(string path, string content)
    {
        var tempPath = path + ".tmp";
        await File.WriteAllTextAsync(tempPath, content);
        if (File.Exists(path)) File.Delete(path);
        File.Move(tempPath, path);
    }
}
