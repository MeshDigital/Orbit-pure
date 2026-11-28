using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using SLSKDONET.Models;

namespace SLSKDONET.Views;

/// <summary>One page in the Back / Forward "recent pages" menu.</summary>
public sealed record PageHistoryItem(string Label, ICommand Command);

/// <summary>
/// Browser-style Back / Forward across pages (Home → Library · Hip Hop → Cue Forge → Back). Pages
/// are cached, so going back returns to the page as it was left (filter, selection, scroll).
/// </summary>
public partial class MainViewModel
{
    private const int HistoryMenuSize = 10;

    private RelayCommand? _goBackCommand;
    private RelayCommand? _goForwardCommand;

    public ICommand GoBackCommand => _goBackCommand ??= new RelayCommand(() => _navigationService.GoBack(), () => _navigationService.CanGoBack);
    public ICommand GoForwardCommand => _goForwardCommand ??= new RelayCommand(() => _navigationService.GoForward(), () => _navigationService.CanGoForward);

    public bool CanGoBack => _navigationService.CanGoBack;
    public bool CanGoForward => _navigationService.CanGoForward;

    public string BackTooltip => _navigationService.BackHistory.Count > 0
        ? $"Back to {PageLabel(_navigationService.BackHistory[0])} (Alt+←) · right-click for recent pages"
        : "Back (Alt+←)";

    public string ForwardTooltip => _navigationService.ForwardHistory.Count > 0
        ? $"Forward to {PageLabel(_navigationService.ForwardHistory[0])} (Alt+→) · right-click for more"
        : "Forward (Alt+→)";

    /// <summary>Recent pages Back can jump to, most recent first.</summary>
    public IReadOnlyList<PageHistoryItem> BackItems => _navigationService.BackHistory
        .Take(HistoryMenuSize)
        .Select((page, i) => new PageHistoryItem(PageLabel(page), new RelayCommand(() => _navigationService.GoBack(i + 1))))
        .ToList();

    /// <summary>Pages Forward can re-open, nearest first.</summary>
    public IReadOnlyList<PageHistoryItem> ForwardItems => _navigationService.ForwardHistory
        .Take(HistoryMenuSize)
        .Select((page, i) => new PageHistoryItem(PageLabel(page), new RelayCommand(() => _navigationService.GoForward(i + 1))))
        .ToList();

    private void OnNavigationHistoryChanged(object? sender, EventArgs e)
    {
        _goBackCommand?.RaiseCanExecuteChanged();
        _goForwardCommand?.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        OnPropertyChanged(nameof(BackTooltip));
        OnPropertyChanged(nameof(ForwardTooltip));
        OnPropertyChanged(nameof(BackItems));
        OnPropertyChanged(nameof(ForwardItems));
    }

    /// <summary>The name a history entry is shown with.</summary>
    internal string PageLabel(object page) => ResolvePageType(page.GetType(), PageType.Home) switch
    {
        PageType.Library => LibraryViewModel.SelectedProject is { } project ? $"Library · {project.SourceTitle}" : "Library",
        PageType.CueForge => "Cue Forge",
        PageType.NowPlaying => "Now Playing",
        PageType.Projects => "Download Center",
        PageType.EditLab => "Edit Lab",
        PageType.FlowBuilder => "Transitions",
        PageType.Home when page is not Avalonia.HomePage => page.GetType().Name.Replace("Page", ""),
        var t => t.ToString(),
    };
}
