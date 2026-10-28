using System;
using System.Windows.Input;

namespace SLSKDONET.Views;

/// <summary>
/// A simple synchronous command implementation for Avalonia.
/// </summary>
public class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public event EventHandler? CanExecuteChanged;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => _execute();

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// A generic version of RelayCommand that accepts a parameter.
/// </summary>
public class RelayCommand<T> : ICommand
{
    private readonly Action<T?> _execute;
    private readonly Func<T?, bool>? _canExecute;

    public event EventHandler? CanExecuteChanged;

    public RelayCommand(Action<T?> execute, Func<T?, bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(Coerce(parameter)) ?? true;

    public void Execute(object? parameter) => _execute(Coerce(parameter));

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    // Callers computing a command parameter often don't know (or can't easily match) the exact
    // numeric type a bound command declares — e.g. Avalonia's Point/Bounds math is always double,
    // while a waveform seek command might declare float. A direct unboxing cast throws
    // InvalidCastException for any non-exact numeric type (a boxed double can't be unboxed as
    // float even though an explicit conversion between them exists), which previously took the
    // whole app down from a benign click. Widen/narrow between numeric primitives instead; fall
    // back to the original direct cast (and its exception) for anything else.
    private static T? Coerce(object? parameter)
    {
        if (parameter is T typed) return typed;
        if (parameter is null) return default;

        var targetType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        if (parameter is IConvertible && targetType.IsPrimitive)
            return (T)Convert.ChangeType(parameter, targetType);

        return (T?)parameter;
    }
}
