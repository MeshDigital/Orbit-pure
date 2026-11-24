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
    /// Phase 12: Global Exception Handling - Setup safety net for beta testing
    /// </summary>
    private void SetupGlobalExceptionHandling()
    {
        // Handle unhandled exceptions on the UI thread
        AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
        {
            var exception = e.ExceptionObject as Exception;
            HandleGlobalException(exception, "AppDomain Unhandled Exception", e.IsTerminating);
        };

        // Handle unobserved task exceptions (fire-and-forget tasks)
        TaskScheduler.UnobservedTaskException += (sender, e) =>
        {
            HandleGlobalException(e.Exception, "Unobserved Task Exception", false);
            e.SetObserved(); // Prevent the exception from crashing the finalizer thread
        };

        Serilog.Log.Information("✅ Global exception handling initialized");
    }

    /// <summary>
    /// Phase 12: Global exception handler - Stream errors to persistent window
    /// </summary>
    /// <summary>
    /// Returns true for transient Soulseek P2P network noise that should not surface in the UI.
    /// These are expected cancellation/disposal/network failures during distributed parent negotiation.
    /// </summary>
    private static bool IsTransientSoulseekError(Exception ex)
    {
        var rootCauses = GetRootCauseExceptions(ex).ToList();
        return rootCauses.Count > 0 && rootCauses.All(IsTransientSoulseekRootCause);
    }

    private static IEnumerable<Exception> GetRootCauseExceptions(Exception ex)
    {
        if (ex is AggregateException aggregate)
        {
            foreach (var inner in aggregate.Flatten().InnerExceptions)
            {
                foreach (var nested in GetRootCauseExceptions(inner))
                {
                    yield return nested;
                }
            }

            yield break;
        }

        if (ex.InnerException is not null)
        {
            foreach (var inner in GetRootCauseExceptions(ex.InnerException))
            {
                yield return inner;
            }

            yield break;
        }

        yield return ex;
    }

    private static bool IsTransientSoulseekRootCause(Exception ex)
    {
        if (ex is OperationCanceledException)
            return true;

        // Soulseek.NET known teardown race: Timer can be null/disposed while network read loop
        // is unwinding. This surfaces as NullReferenceException in Soulseek.Extensions.Reset.
        // Treat as transient noise so it does not pollute the error stream.
        if (ex is NullReferenceException)
        {
            var stack = ex.StackTrace ?? string.Empty;
            if (stack.Contains("Soulseek.Extensions.Reset", StringComparison.OrdinalIgnoreCase) ||
                stack.Contains("Soulseek.Network.Tcp.Connection.ReadInternalAsync", StringComparison.OrdinalIgnoreCase) ||
                ex.ToString().Contains("Soulseek.Extensions.Reset(Timer timer)", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (ex.Message.Contains("Transfer failed: Transfer complete", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("Transfer complete", StringComparison.OrdinalIgnoreCase))
            return true;

        if (ex is InvalidOperationException ioe &&
            ioe.Message.Contains("Not listening. You must call the Start() method before calling this method.", StringComparison.OrdinalIgnoreCase))
            return true;

        if (ex is ObjectDisposedException ode)
        {
            if (ode.ObjectName?.Contains("MessageConnection", StringComparison.OrdinalIgnoreCase) == true)
                return true;

            if (ode.Message.Contains("MessageConnection", StringComparison.OrdinalIgnoreCase))
                return true;

            // Socket disposed inside Soulseek TCP listener during reconnect/dispose cycle.
            // Source is System.Net.Sockets but the stack trace reveals Soulseek internals.
            var odeStack = ode.StackTrace ?? string.Empty;
            if (odeStack.Contains("Soulseek.Network.Tcp.Listener", StringComparison.OrdinalIgnoreCase) ||
                odeStack.Contains("ListenContinuouslyAsync", StringComparison.OrdinalIgnoreCase))
                return true;

            // "Connection" object from Soulseek.NET disposed mid-transfer
            if (ode.ObjectName?.Equals("Connection", StringComparison.OrdinalIgnoreCase) == true)
                return true;
        }

        if (ex is IOException ioEx)
        {
            var ioMessage = ioEx.ToString();
            if (ioMessage.Contains("Unable to read data from the transport connection", StringComparison.OrdinalIgnoreCase) ||
                ioMessage.Contains("Failed to read", StringComparison.OrdinalIgnoreCase) ||
                ioMessage.Contains("connected party did not properly respond", StringComparison.OrdinalIgnoreCase) ||
                ioMessage.Contains("connected host has failed to respond", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // SocketException: network timeout / connection refused / peer abort during Soulseek churn.
        if (ex is SocketException se && (se.NativeErrorCode == 10060 || se.NativeErrorCode == 10061 || se.NativeErrorCode == 10054 || se.NativeErrorCode == 10053 || se.NativeErrorCode == 995))
            return true;

        // Timeout / inactivity / cancelled I/O noise from Soulseek.NET internals
        if (ex is TimeoutException)
            return true;

        var msg = ex.Message;
        var stackTraceText = ex.StackTrace ?? string.Empty;
        if (msg.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("failed to respond", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("Unable to read data from the transport connection", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("Failed to read", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("Inactivity timeout", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("Remote connection closed", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("I/O operation has been aborted", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("No connection could be made", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("An existing connection was forcibly closed", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("The operation was canceled", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("MessageConnection", StringComparison.OrdinalIgnoreCase) ||
            stackTraceText.Contains("Soulseek.Network.Tcp.Connection.ReadInternalAsync", StringComparison.OrdinalIgnoreCase) ||
            stackTraceText.Contains("Soulseek.Network.MessageConnection.ReadContinuouslyAsync", StringComparison.OrdinalIgnoreCase))
            return true;

        // Any exception type declared in the Soulseek namespace (e.g. TransferReportedFailedException)
        if (ex.GetType().Namespace?.StartsWith("Soulseek", StringComparison.OrdinalIgnoreCase) == true)
            return true;

        // Any exception originating from Soulseek.NET library itself
        if (ex.Source?.Contains("Soulseek", StringComparison.OrdinalIgnoreCase) == true)
            return true;

        // "Download reported as failed by remote client" — peer aborted transfer, expected during P2P churn
        if (ex.Message.Contains("reported as failed by remote client", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private Views.Avalonia.ErrorStreamWindow CreateErrorStreamWindow()
    {
        var window = new Views.Avalonia.ErrorStreamWindow();
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_errorStreamWindow, window))
            {
                _errorStreamWindow = null;
            }
        };

        return window;
    }

    private async void HandleGlobalException(Exception? exception, string source, bool isTerminating)
    {
        try
        {
            var errorMessage = exception?.Message ?? "Unknown error";
            var stackTrace = exception?.ToString() ?? "No stack trace available";

            // Filter transient Soulseek P2P network noise — expected failures during
            // distributed parent negotiation and peer connection cycling.
            if (exception != null && IsTransientSoulseekError(exception))
            {
                Serilog.Log.Debug("[Noise Filter] Suppressed transient Soulseek error: {Message}", exception.Message);
                return;
            }

            if (isTerminating)
            {
                Serilog.Log.Fatal(exception, "🚨 {Source}: {Message}", source, errorMessage);
            }
            else
            {
                Serilog.Log.Warning(exception, "⚠️ {Source}: {Message}", source, errorMessage);
            }

            // Add to error stream window on UI thread
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                try
                {
                    // Create window if needed
                    if (_errorStreamWindow == null)
                    {
                        _errorStreamWindow = CreateErrorStreamWindow();
                    }

                    // Add the error
                    _errorStreamWindow.AddError(source, errorMessage, stackTrace);

                    // Show window if not already visible
                    if (!_errorStreamWindow.IsVisible)
                    {
                        _errorStreamWindow.Show();
                        _errorStreamWindow.Activate();
                    }
                    else
                    {
                        // Bring to front
                        _errorStreamWindow.Activate();
                    }

                    // If terminating, show a brief alert
                    if (isTerminating)
                    {
                        var alert = new Window
                        {
                            Title = "Critical Error",
                            Content = new TextBlock
                            {
                                Text = "A critical error occurred and the application will terminate.\nCheck the Error Stream window for details.",
                                TextWrapping = TextWrapping.Wrap,
                                Margin = new Thickness(20)
                            },
                            SizeToContent = SizeToContent.WidthAndHeight,
                            WindowStartupLocation = WindowStartupLocation.CenterScreen
                        };
                        alert.Show();
                    }
                }
                catch (Exception ex)
                {
                    Serilog.Log.Fatal(ex, "Failed to show error stream window");
                    // Last resort: console output
                    Console.WriteLine($"CRITICAL ERROR: {source} - {errorMessage}");
                }
            });
        }
        catch (Exception handlerEx)
        {
            // Absolute last resort - log to console if everything fails
            Console.WriteLine($"CRITICAL: Exception handler failed: {handlerEx}");
            Console.WriteLine($"Original error: {exception}");
        }
    }
}
