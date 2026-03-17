using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.ReactiveUI;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Formatting.Compact;
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

            // Determine a deterministic log directory
            var currentDirectory = Directory.GetCurrentDirectory();
            var csprojInCurrentDir = File.Exists(Path.Combine(currentDirectory, "SLSKDONET.csproj"));
            var isDevelopment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") == "Development" || csprojInCurrentDir;

            var logDirectory = isDevelopment
                ? Path.Combine(currentDirectory, "logs")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ORBIT", "logs");

            // Ensure log directory exists
            Directory.CreateDirectory(logDirectory);

            // Create a unique log file per app run
            var runId = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            var runJsonLogPath = Path.Combine(logDirectory, $"run_{runId}.json");
            var runTxtLogPath = Path.Combine(logDirectory, $"run_{runId}.txt");

            AppContext.SetData("Orbit.LogDirectory", logDirectory);
            AppContext.SetData("Orbit.RunJsonLogPath", runJsonLogPath);
            AppContext.SetData("Orbit.RunTxtLogPath", runTxtLogPath);

            // Initialize Serilog with proper log paths
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .ReadFrom.Configuration(configuration)
                .WriteTo.File(
                    path: runJsonLogPath,
                    formatter: new CompactJsonFormatter(),
                    rollingInterval: RollingInterval.Infinite,
                    retainedFileCountLimit: 7)
                .WriteTo.File(
                    path: runTxtLogPath,
                    outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}",
                    rollingInterval: RollingInterval.Infinite,
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
                Log.Information("Starting ORBIT application | RunJsonLog: {RunJsonLogPath} | RunTxtLog: {RunTxtLogPath}", runJsonLogPath, runTxtLogPath);
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
