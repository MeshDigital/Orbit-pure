using SLSKDONET.Views;
using Xunit;

namespace SLSKDONET.Tests.UI;

public class RelayCommandTests
{
    // Regression test for the crash reported when clicking the Now Playing waveform:
    // WaveformControl computes its seek progress as a double (Avalonia's Point/Bounds are
    // always double), but PlayerViewModel.SeekCommand is a RelayCommand<float>. Execute's old
    // direct "(T?)parameter" unboxing cast threw InvalidCastException for a boxed double being
    // unboxed as float, which was unhandled and crashed the whole app.
    [Fact]
    public void Execute_BoxedDouble_AgainstFloatCommand_DoesNotThrow_AndConvertsValue()
    {
        float? received = null;
        var command = new RelayCommand<float>(v => received = v);

        object parameter = 0.42d; // boxed double, matches WaveformControl's Math.Clamp(..., 0.0, 1.0) output

        var ex = Record.Exception(() => command.Execute(parameter));

        Assert.Null(ex);
        Assert.NotNull(received);
        Assert.Equal(0.42f, received!.Value, 5);
    }

    [Fact]
    public void CanExecute_BoxedDouble_AgainstFloatCommand_DoesNotThrow()
    {
        var command = new RelayCommand<float>(_ => { }, v => v > 0);

        var ex = Record.Exception(() => command.CanExecute(0.75d));

        Assert.Null(ex);
    }

    [Fact]
    public void Execute_ExactMatchingType_StillWorksUnchanged()
    {
        float? received = null;
        var command = new RelayCommand<float>(v => received = v);

        command.Execute(0.5f);

        Assert.Equal(0.5f, received);
    }

    [Fact]
    public void Execute_NullParameter_PassesDefault()
    {
        object? received = "unset";
        var command = new RelayCommand<string>(v => received = v);

        command.Execute(null);

        Assert.Null(received);
    }

    [Fact]
    public void Execute_IncompatibleReferenceType_StillThrows()
    {
        var command = new RelayCommand<PlaceholderTarget>(_ => { });

        Assert.Throws<System.InvalidCastException>(() => command.Execute("not a PlaceholderTarget"));
    }

    private sealed class PlaceholderTarget { }
}
