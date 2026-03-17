using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.ReactiveUI;
using Microsoft.Extensions.Configuration;
using Serilog;
using System;
using System.IO;

namespace SLSKDONET
{
    class Program
    {
        // Initialization code. Don't use any Avalonia or WPF types until Get
        // Instance() is called.
        [STAThread]
        public static void Main(string[] args)
        {
            // Build configuration for Serilog
            var configuration = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production"}.json", optional: true)
                .Build();

            // Determine log directory based on environment
            var isDevelopment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") == "Development" ||
                               (Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") == null && 
                                Directory.GetCurrentDirectory().Contains("GitHub")); // Development heuristic
            
            string logDirectory;
            if (isDevelopment)
            {
                // In development, use project root logs directory
                logDirectory = Path.Combine(Directory.GetCurrentDirectory(), "logs");
            }
            else
            {
                // In production, use user app data directory
                logDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ORBIT", "logs");
            }

            // Ensure log directory exists
            Directory.CreateDirectory(logDirectory);

            // Initialize Serilog with proper log paths
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .ReadFrom.Configuration(configuration)
                .WriteTo.File(
                    path: Path.Combine(logDirectory, "log.json"), 
                    formatter: new Serilog.Formatting.Compact.CompactJsonFormatter(),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 7)
                .WriteTo.Logger(l => l
                    .Filter.ByIncludingOnly(e => e.Properties.ContainsKey("SourceContext") && e.Properties["SourceContext"].ToString().Contains("DownloadManager"))
                    .WriteTo.File(Path.Combine(logDirectory, "downloads.log"), rollingInterval: RollingInterval.Day, outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}"))
                 .WriteTo.Logger(l => l
                    .Filter.ByIncludingOnly(e => e.Properties.ContainsKey("SourceContext") && e.Properties["SourceContext"].ToString().Contains("BuildService"))
                    .WriteTo.File(Path.Combine(logDirectory, "build_runs.log"), rollingInterval: RollingInterval.Day))
                .CreateLogger();

            try
            {
                Log.Information("Starting ORBIT application");
                BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "Application terminated unexpectedly");
                throw;
            }
            finally
            {
                Log.CloseAndFlush();
            }
        }

        // Avalonia configuration, don't remove; also used by visual designer.
        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .LogToTrace()
                .UseReactiveUI();
    }
}
