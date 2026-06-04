using System;
using System.Reflection;
using System.Threading.Tasks;
using Moq;
using SLSKDONET.Services;
using SLSKDONET.ViewModels;
using SLSKDONET.Views.Avalonia;
using Xunit;

namespace SLSKDONET.Tests.ViewModels;

public class PlayerViewModelNavigationResilienceTests
{
    [Fact]
    public async Task OpenPlayerViewAsync_DebouncesRepeatedRequests()
    {
        var service = new Mock<INavigationService>();
        service.SetupGet(s => s.CurrentPage).Returns(CreateNowPlayingPageStub());

        var sut = CreateUninitializedSut(service.Object);

        var first = InvokeOpenPlayerViewAsync(sut);
        var second = InvokeOpenPlayerViewAsync(sut);

        await Task.WhenAll(first, second);

        service.Verify(s => s.NavigateTo("Player"), Times.Once);
        service.Verify(s => s.NavigateTo("Home"), Times.Never);
    }

    [Fact]
    public async Task OpenPlayerViewAsync_FallsBackHomeWhenPlayerDoesNotSettle()
    {
        var service = new Mock<INavigationService>();
        service.SetupGet(s => s.CurrentPage).Returns((object?)null);

        var sut = CreateUninitializedSut(service.Object);

        await InvokeOpenPlayerViewAsync(sut);

        service.Verify(s => s.NavigateTo("Player"), Times.Once);
        service.Verify(s => s.NavigateTo("Home"), Times.Once);
    }

    private static PlayerViewModel CreateUninitializedSut(INavigationService navigationService)
    {
        var sut = (PlayerViewModel)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PlayerViewModel));

        SetField(sut, "_navigationService", navigationService);
        SetField(sut, "_rightPanelService", new RightPanelService());
        SetProperty(sut, nameof(PlayerViewModel.IsExpandedPlayerOpen), true);
        SetProperty(sut, nameof(PlayerViewModel.IsQueueOpen), true);

        return sut;
    }

    private static object CreateNowPlayingPageStub()
        => System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(NowPlayingPage));

    private static Task InvokeOpenPlayerViewAsync(PlayerViewModel sut)
    {
        var method = typeof(PlayerViewModel).GetMethod("OpenPlayerViewAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Method not found: OpenPlayerViewAsync");

        return (Task)method.Invoke(sut, Array.Empty<object>())!;
    }

    private static void SetField(object instance, string name, object value)
    {
        var field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Field not found: {name}");
        field.SetValue(instance, value);
    }

    private static void SetProperty(object instance, string name, object value)
    {
        var property = instance.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public)
            ?? throw new InvalidOperationException($"Property not found: {name}");
        property.SetValue(instance, value);
    }
}