using System;
using System.Threading.Tasks;
using SLSKDONET.Views;
using Xunit;

namespace SLSKDONET.Tests.UI;

/// <summary>
/// AsyncRelayCommand&lt;T&gt; used a direct "(T?)parameter" cast in CanExecute and at the top of the
/// async-void Execute, both outside its try/catch. A mismatched CommandParameter (a boxed double
/// for a float command, a XAML string literal for an int command, or the wrong VM type) threw
/// InvalidCastException straight into the UI thread.
/// </summary>
public class AsyncRelayCommandTests
{
    [Fact]
    public void CanExecute_IncompatibleType_ReturnsFalse_InsteadOfThrowing()
    {
        var command = new AsyncRelayCommand<Placeholder>(_ => Task.CompletedTask);

        var ex = Record.Exception(() => command.CanExecute("not a Placeholder"));

        Assert.Null(ex);
        Assert.False(command.CanExecute("not a Placeholder"));
    }

    [Fact]
    public void Execute_IncompatibleType_IsIgnored_InsteadOfThrowing()
    {
        bool ran = false;
        var command = new AsyncRelayCommand<Placeholder>(_ => { ran = true; return Task.CompletedTask; });

        var ex = Record.Exception(() => command.Execute(42));

        Assert.Null(ex);
        Assert.False(ran);
    }

    [Fact]
    public void Execute_BoxedDouble_AgainstFloatCommand_Converts()
    {
        float received = -1;
        var command = new AsyncRelayCommand<float>(v => { received = v; return Task.CompletedTask; });

        command.Execute(0.25d);

        Assert.Equal(0.25f, received, 5);
    }

    [Fact]
    public void Execute_XamlStringLiteral_AgainstIntCommand_Converts()
    {
        int received = -1;
        var command = new AsyncRelayCommand<int>(v => { received = v; return Task.CompletedTask; });

        command.Execute("16");

        Assert.Equal(16, received);
    }

    [Fact]
    public void Execute_StringAgainstEnumCommand_Parses()
    {
        DayOfWeek? received = null;
        var command = new AsyncRelayCommand<DayOfWeek>(v => { received = v; return Task.CompletedTask; });

        command.Execute("Friday");

        Assert.Equal(DayOfWeek.Friday, received);
    }

    [Fact]
    public void Execute_ThrowingHandler_DoesNotEscape()
    {
        var command = new AsyncRelayCommand<object>(_ => throw new InvalidOperationException("boom"));

        var ex = Record.Exception(() => command.Execute(null));

        Assert.Null(ex);
        Assert.True(command.CanExecute(null)); // _isExecuting reset in finally
    }

    [Fact]
    public void Execute_NullParameter_PassesDefault()
    {
        Placeholder? received = new();
        var command = new AsyncRelayCommand<Placeholder>(v => { received = v; return Task.CompletedTask; });

        command.Execute(null);

        Assert.Null(received);
    }

    private sealed class Placeholder { }
}
