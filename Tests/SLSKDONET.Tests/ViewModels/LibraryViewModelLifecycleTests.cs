using System;
using System.Reflection;
using SLSKDONET.ViewModels;
using Xunit;

namespace SLSKDONET.Tests.ViewModels;

public class LibraryViewModelLifecycleTests
{
    [Fact]
    public void LifecycleSummary_UsesCurrentCounterValues()
    {
        var sut = CreateUninitializedVm();
        SetField(sut, "_desiredDownloadCount", 5);
        SetField(sut, "_ingestionBacklogCount", 2);
        SetField(sut, "_physicalOnDiskCount", 21);
        SetField(sut, "_staleIndexedCount", 3);

        Assert.Equal(
            "Wanted downloads: 5 • Ingestion backlog: 2 • Physical on-disk indexed: 21 • Stale indexed rows: 3",
            sut.LibraryCountDifferentiationSummary);
    }

    private static LibraryViewModel CreateUninitializedVm()
        => (LibraryViewModel)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(LibraryViewModel));

    private static void SetField(object instance, string name, object value)
    {
        var field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Field not found: {name}");
        field.SetValue(instance, value);
    }

}