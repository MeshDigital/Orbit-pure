using System;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows.Input;

namespace SLSKDONET.Views;

/// <summary>
/// An ICommand implementation that supports asynchronous operations.
/// Avalonia-compatible version without WPF CommandManager dependency.
/// </summary>
public class AsyncRelayCommand<T> : ICommand
{
    private readonly Func<T?, Task> _execute;
    private readonly Func<T?, bool>? _canExecute;
    private bool _isExecuting;

    public event EventHandler? CanExecuteChanged;

    public AsyncRelayCommand(Func<T?, Task> execute, Func<T?, bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter)
    {
        if (_isExecuting) return false;
        // A parameter that can't be turned into T (wrong VM type, or a string where a number
        // was declared) disables the command instead of throwing. The old direct "(T?)parameter"
        // cast ran outside any try/catch — from Avalonia's CanExecute polling and at the top of
        // the async-void Execute — so a mismatched CommandParameter crashed the app.
        if (!TryCoerce(parameter, out var value)) return false;
        return _canExecute?.Invoke(value) ?? true;
    }

    public async void Execute(object? parameter)
    {
        if (!TryCoerce(parameter, out var value))
        {
            Serilog.Log.Warning("AsyncRelayCommand<{Type}> ignored a {ParamType} parameter it can't convert",
                typeof(T).Name, parameter?.GetType().Name ?? "null");
            return;
        }

        if (!_isExecuting && (_canExecute?.Invoke(value) ?? true))
        {
            try
            {
                _isExecuting = true;
                RaiseCanExecuteChanged();
                await _execute(value);
            }
            catch (Exception ex)
            {
                // Do not rethrow from async void command execution: that tears down the UI
                // thread. But don't hide it either — this used to go to Debug.WriteLine only,
                // which is invisible in release builds.
                Serilog.Log.Error(ex, "AsyncRelayCommand<{Type}> failed", typeof(T).Name);
            }
            finally
            {
                _isExecuting = false;
                RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// Raises the CanExecuteChanged event to re-evaluate the command's execution status.
    /// </summary>
    public void RaiseCanExecuteChanged()
    {
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Converts a bound CommandParameter to T. Exact matches and null pass through; numeric
    /// primitives and XAML string literals ("4") convert via IConvertible. Anything else fails.
    /// </summary>
    internal static bool TryCoerce(object? parameter, out T? value)
    {
        if (parameter is T typed) { value = typed; return true; }
        if (parameter is null) { value = default; return true; }

        var targetType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        if (parameter is IConvertible && (targetType.IsPrimitive || targetType == typeof(decimal)))
        {
            try
            {
                value = (T)Convert.ChangeType(parameter, targetType, System.Globalization.CultureInfo.InvariantCulture);
                return true;
            }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
            {
            }
        }
        else if (targetType.IsEnum && parameter is string s && Enum.TryParse(targetType, s, ignoreCase: true, out var parsed))
        {
            value = (T)parsed!;
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>
    /// Executes the command asynchronously and returns the task.
    /// </summary>
    public async Task ExecuteAsync(T? parameter)
    {
        if (CanExecute(parameter))
        {
            try
            {
                _isExecuting = true;
                RaiseCanExecuteChanged();
                await _execute(parameter);
            }
            finally
            {
                _isExecuting = false;
                RaiseCanExecuteChanged();
            }
        }
    }
}

/// <summary>
/// A non-generic version of AsyncRelayCommand.
/// </summary>
public class AsyncRelayCommand : AsyncRelayCommand<object>
{
    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
        : base(async _ => await execute(), _ => canExecute?.Invoke() ?? true)
    {
    }
}