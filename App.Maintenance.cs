using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;
using SLSKDONET.Configuration;
using SLSKDONET.Data;
using SLSKDONET.Services;
using SLSKDONET.Services.InputParsers;
using SLSKDONET.Services.Audio;
using SLSKDONET.Services.Entertainment;
using SLSKDONET.Services.AutoDownload;
using SLSKDONET.Services.Library;
using SLSKDONET.ViewModels;
using SLSKDONET.Services.Input;
using SLSKDONET.Views;
using SLSKDONET.Views.Avalonia;
using SLSKDONET.ViewModels.Settings;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;

namespace SLSKDONET;

public partial class App
{
    /// <summary>
    /// Phase 8: Maintenance Task - runs once per launch, on a pool thread.
    /// - Deletes backup files older than 7 days
    /// - Daily library snapshot, weekly VACUUM (see LibraryDatabaseMaintenance)
    /// </summary>
    private async Task RunMaintenanceTasksAsync()
    {
        try
        {
            await PerformMaintenanceAsync();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "[Maintenance] Startup maintenance failed");
        }
    }

    private async Task PerformMaintenanceAsync()
    {
        var config = Services?.GetService<AppConfig>();
        if (config == null) return;
        
        Serilog.Log.Information("[Maintenance] Starting daily maintenance tasks...");
        
        // Task 1: Clean old backup files (7-day retention)
        if (!string.IsNullOrEmpty(config.DownloadDirectory) && Directory.Exists(config.DownloadDirectory))
        {
            try
            {
                var backupFiles = Directory.GetFiles(config.DownloadDirectory, "*.backup", SearchOption.AllDirectories)
                    .Where(f => File.GetCreationTime(f) < DateTime.Now.AddDays(-7))
                    .ToList();
                
                if (backupFiles.Any())
                {
                    foreach (var backupFile in backupFiles)
                    {
                        try
                        {
                            File.Delete(backupFile);
                            Serilog.Log.Debug("[Maintenance] Deleted old backup: {File}", Path.GetFileName(backupFile));
                        }
                        catch (Exception ex)
                        {
                            Serilog.Log.Warning(ex, "[Maintenance] Failed to delete backup: {File}", backupFile);
                        }
                    }
                    
                    Serilog.Log.Information("[Maintenance] Cleaned {Count} old backup files (>7 days)", backupFiles.Count);
                }
                else
                {
                    Serilog.Log.Debug("[Maintenance] No old backups to clean");
                }
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "[Maintenance] Backup cleanup failed");
            }
        }
        
        // Task 2: weekly VACUUM (backlog E1).
        // This used to VACUUM on every launch from a UI-thread continuation; SQLite's "async"
        // calls are synchronous, so it froze the UI (~10 s) and locked the DB at startup. Now it
        // is gated by a marker file and always runs on a pool thread.
        var vacuumed = await Task.Run(() => SLSKDONET.Services.LibraryDatabaseMaintenance.RunScheduledVacuum(
            onError: ex => Serilog.Log.Warning(ex, "[Maintenance] Weekly VACUUM failed")));
        if (vacuumed)
            Serilog.Log.Information("[Maintenance] Weekly VACUUM completed");

        // Task 3: Schedule batch sync of embeddings
        try
        {
            var embeddingService = Services?.GetService<SLSKDONET.Services.Embeddings.IEmbeddingExtractionService>();
            if (embeddingService != null)
            {
                embeddingService.ScheduleBatchSync();
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "[Maintenance] Failed to schedule embedding batch sync: {Message}", ex.Message);
        }
        
        Serilog.Log.Information("[Maintenance] Daily maintenance completed");
    }
}
