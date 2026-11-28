using Avalonia.Controls;
using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using SLSKDONET.Models;

namespace SLSKDONET.Services;

/// <summary>
/// Avalonia-based navigation service.
/// Uses ContentControl binding instead of WPF Frame navigation.
/// Pages are registered as view models/controls and swapped via ContentControl.Content binding.
/// Phase 1A: Implements ViewCache pattern to prevent state loss during navigation.
/// </summary>
public interface INavigationService
{
    void RegisterPage(string key, Type pageType);
    void NavigateTo(string pageKey);
    void NavigateTo(PageType pageKey); // Added Overload
    void GoBack();

    /// <summary>Re-opens the page left with <see cref="GoBack"/> (browser-style forward).</summary>
    void GoForward() { }

    /// <summary>Goes back <paramref name="steps"/> pages at once (the recent-pages menu).</summary>
    void GoBack(int steps) { for (int i = 0; i < steps; i++) GoBack(); }

    /// <summary>Goes forward <paramref name="steps"/> pages at once.</summary>
    void GoForward(int steps) { for (int i = 0; i < steps; i++) GoForward(); }

    bool CanGoBack => false;
    bool CanGoForward => false;

    /// <summary>Pages Back would return to, most recent first.</summary>
    IReadOnlyList<object> BackHistory => Array.Empty<object>();

    /// <summary>Pages Forward would re-open, nearest first.</summary>
    IReadOnlyList<object> ForwardHistory => Array.Empty<object>();

    /// <summary>Raised whenever the back or forward history changes.</summary>
    event EventHandler? HistoryChanged { add { } remove { } }

    object? CurrentPage { get; }
    event EventHandler<UserControl>? Navigated;

    /// <summary>Predicate returning whether Labs pages (Sets, Workstation, EditLab) may be opened (#538).</summary>
    Func<bool>? IsLabsEnabled { get; set; }

    /// <summary>Raised when navigation targets a Labs page while Labs is off (#538).</summary>
    event EventHandler<string>? LabsNavigationBlocked;
}

public class NavigationService : INavigationService
{
    private readonly ILogger<NavigationService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly PerformanceTracker _perfTracker;
    private readonly Dictionary<string, Type> _pages = new();
    /// <summary>Back history, oldest first (the end is the page Back returns to).</summary>
    private readonly List<object> _back = new();
    /// <summary>Forward history, farthest first (the end is the page Forward re-opens).</summary>
    private readonly List<object> _forward = new();
    internal const int MaxHistory = 30;
    private object? _currentPage;

    // Phase 1A: ViewCache pattern - prevents state loss during navigation
    private readonly Dictionary<Type, UserControl> _viewCache = new();

    public object? CurrentPage => _currentPage;
    public Func<bool>? IsLabsEnabled { get; set; }
    public event EventHandler<string>? LabsNavigationBlocked;

    public static bool IsLabsPage(string? pageKey) =>
        string.Equals(pageKey, "Sets", StringComparison.OrdinalIgnoreCase)
        || string.Equals(pageKey, "Workstation", StringComparison.OrdinalIgnoreCase)
        || string.Equals(pageKey, "EditLab", StringComparison.OrdinalIgnoreCase);

    public NavigationService(IServiceProvider serviceProvider, ILogger<NavigationService> logger, PerformanceTracker perfTracker)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _perfTracker = perfTracker;
    }

    public void RegisterPage(string key, Type pageType)
    {
        _pages[key] = pageType;
    }

    public event EventHandler<UserControl>? Navigated;

    public void NavigateTo(PageType pageKey)
    {
        NavigateTo(pageKey.ToString());
    }

    public void NavigateTo(string pageKey)
    {
        if (IsLabsPage(pageKey) && IsLabsEnabled is not null && !IsLabsEnabled())
        {
            _logger.LogInformation("Blocked navigation to Labs page '{PageKey}' while Labs is off.", pageKey);
            LabsNavigationBlocked?.Invoke(this, pageKey);
            return;
        }

        if (_pages.TryGetValue(pageKey, out var pageType))
        {
            // Measures page resolve (first visit: DI construction; later visits: cache lookup, near-
            // instant) plus the content swap. A page's own async data load (e.g. UsersViewModel.
            // LoadAsync, which is what was actually slow) runs after this and isn't captured here —
            // pages that want that measured opt in with their own _perfTracker.Measure(...) scope.
            using var _ = _perfTracker.Measure($"Nav:{pageKey}");
            _logger.LogInformation("Navigating to page: {PageKey}", pageKey);

            // Save current page in history (if exists). Re-navigating to the page already shown
            // (e.g. the import preview's Retry) must not push it, or Back/Cancel lands on itself.
            bool leavingPage = _currentPage != null && _currentPage.GetType() != pageType;

            // Phase 1A: Check view cache first
            if (!_viewCache.TryGetValue(pageType, out var page))
            {
                try
                {
                    // Create new page instance via DI and cache it
                    page = _serviceProvider.GetService(pageType) as UserControl;
                    if (page != null)
                    {
                        _viewCache[pageType] = page;
                        _logger.LogDebug("Created and cached new view: {PageType}", pageType.Name);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Navigation failed while creating page: {PageKey} ({PageType})", pageKey, pageType);
                    return;
                }
            }
            else
            {
                _logger.LogDebug("Reusing cached view: {PageType}", pageType.Name);
            }

            if (page != null)
            {
                // A new visit starts a new branch: Forward only makes sense right after Back.
                if (leavingPage)
                {
                    PushBounded(_back, _currentPage!);
                    _forward.Clear();
                    HistoryChanged?.Invoke(this, EventArgs.Empty);
                }
                _currentPage = page;
                OnPageChanged();
                Navigated?.Invoke(this, page);
            }
            else
            {
                _logger.LogError("Failed to resolve page control for key: {PageKey}, Type: {PageType}", pageKey, pageType);
            }
        }
        else
        {
            _logger.LogWarning("Navigation failed: Page key '{PageKey}' not registered", pageKey);
        }
    }

    public bool CanGoBack => _back.Count > 0;
    public bool CanGoForward => _forward.Count > 0;
    public IReadOnlyList<object> BackHistory => Reversed(_back);
    public IReadOnlyList<object> ForwardHistory => Reversed(_forward);
    public event EventHandler? HistoryChanged;

    public void GoBack() => GoBack(1);
    public void GoForward() => GoForward(1);

    public void GoBack(int steps) => Step(_back, _forward, steps);
    public void GoForward(int steps) => Step(_forward, _back, steps);

    /// <summary>Moves <paramref name="steps"/> pages along <paramref name="from"/>, pushing every page
    /// passed (and the current one) onto <paramref name="to"/>, so the opposite direction retraces it.</summary>
    private void Step(List<object> from, List<object> to, int steps)
    {
        if (steps <= 0 || from.Count == 0) return;
        steps = Math.Min(steps, from.Count);
        object? target = null;
        for (int i = 0; i < steps; i++)
        {
            if (_currentPage != null) PushBounded(to, _currentPage);
            target = from[^1];
            from.RemoveAt(from.Count - 1);
            _currentPage = target;
        }
        HistoryChanged?.Invoke(this, EventArgs.Empty);
        OnPageChanged();
        if (target is UserControl control)
            Navigated?.Invoke(this, control);
    }

    private static void PushBounded(List<object> list, object page)
    {
        list.Add(page);
        if (list.Count > MaxHistory) list.RemoveAt(0);
    }

    private static IReadOnlyList<object> Reversed(List<object> list)
    {
        var copy = new List<object>(list);
        copy.Reverse();
        return copy;
    }

    private void OnPageChanged()
    {
        // Notify listeners (will be implemented via property change in MainViewModel)
    }
}
