using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using System;

namespace SLSKDONET;

/// <summary>
/// Avalonia application entry point (#298): limited to application lifetime and entry concerns.
/// DI wiring lives in <c>App.Composition.cs</c>, startup/shutdown orchestration in <c>App.Bootstrap.cs</c>,
/// global exception handling in <c>App.Exceptions.cs</c>, and scheduled maintenance in <c>App.Maintenance.cs</c>.
/// </summary>
public partial class App : Application
{
    public IServiceProvider? Services { get; private set; }

    /// <summary>
    /// True only once the user has explicitly chosen "Exit" from the tray menu (or another real
    /// shutdown path sets it). Closing the main window (X button, Alt+F4, taskbar close) does NOT
    /// set this — MainWindow.OnWindowClosing checks it to decide whether to actually exit or just
    /// hide to the tray, so the download engine keeps running unattended.
    /// </summary>
    public bool IsExitRequested { get; set; }
    private Views.Avalonia.ErrorStreamWindow? _errorStreamWindow;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        // Invariant numbers in every NumericUpDown; a typed decimal comma is entered as '.'.
        Views.Avalonia.NumericInputLocale.Install();
    }

    public override async void OnFrameworkInitializationCompleted()
    {
        SetupGlobalExceptionHandling();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Services = ConfigureServices();
            SLSKDONET.Services.Diagnostics.StartupTelemetry.MarkOnce("DI built");
            ActivateEarlyEventListeners(Services);
            desktop.Exit += async (_, __) => await ShutdownServicesAsync();
            await BootstrapDesktopAsync(desktop);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();
        ConfigureSharedServices(services);
        return services.BuildServiceProvider();
    }
}
