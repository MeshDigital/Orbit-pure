using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Input;
using ReactiveUI;
using Xunit;

namespace SLSKDONET.Tests.UI;

/// <summary>
/// A XAML literal like CommandParameter="4" is a <see cref="string"/>. ReactiveCommand&lt;int, _&gt;
/// throws InvalidOperationException from ICommand.Execute for a string, which escapes into the UI
/// thread. The Flow Builder cue editor's Loop 4/8/16/off buttons did exactly that.
/// </summary>
public class XamlCommandParameterTypeTests
{
    [Fact]
    public void ReactiveIntCommand_RejectsStringParameter()
    {
        ICommand command = ReactiveCommand.Create<int>(_ => { });

        Assert.Throws<InvalidOperationException>(() => command.Execute("4"));
    }

    [Fact]
    public void TrackCueEditorStrip_LoopButtons_PassTypedIntegers()
    {
        var path = FindRepoFile(Path.Combine("Views", "Avalonia", "Controls", "TrackCueEditorStrip.axaml"));
        var xaml = File.ReadAllText(path);

        Assert.DoesNotMatch(new Regex(@"SetSelectedLoopBarsCommand\}""\s+CommandParameter=""\d+"""), xaml);
        Assert.Equal(4, Regex.Matches(xaml, @"<x:Int32>\d+</x:Int32>").Count);
    }

    private static string FindRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, relative)))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, relative);
    }
}
