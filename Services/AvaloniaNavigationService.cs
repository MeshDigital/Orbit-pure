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
    object? CurrentPage { get; }
    event EventHandler<UserControl>? Navigated;
}

public class NavigationService : INavigationService
{
    private readonly ILogger<NavigationService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly Dictionary<string, Type> _pages = new();
    private readonly Stack<object?> _pageHistory = new();
    private object? _currentPage;
    
    // Phase 1A: ViewCache pattern - prevents state loss during navigation
    private readonly Dictionary<Type, UserControl> _viewCache = new();

    public object? CurrentPage => _currentPage;

    public NavigationService(IServiceProvider serviceProvider, ILogger<NavigationService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
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
        if (_pages.TryGetValue(pageKey, out var pageType))
        {
            _logger.LogInformation("Navigating to page: {PageKey}", pageKey);
            
            // Save current page in history (if exists)
            if (_currentPage != null)
            {
                _pageHistory.Push(_currentPage);
            }

            // Phase 1A: Check view cache first
            if (!_viewCache.TryGetValue(pageType, out var page))
            {
                // Create new page instance via DI and cache it
                page = _serviceProvider.GetService(pageType) as UserControl;
                if (page != null)
                {
                    _viewCache[pageType] = page;
                    _logger.LogDebug("Created and cached new view: {PageType}", pageType.Name);
                }
            }
            else
            {
                _logger.LogDebug("Reusing cached view: {PageType}", pageType.Name);
            }

            if (page != null)
            {
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

    public void GoBack()
    {
        if (_pageHistory.Count > 0)
        {
            var previousPage = _pageHistory.Pop();
            _currentPage = previousPage;
            OnPageChanged();
            if (previousPage is UserControl control)
            {
                Navigated?.Invoke(this, control);
            }
        }
    }

    private void OnPageChanged()
    {
        // Notify listeners (will be implemented via property change in MainViewModel)
    }
}
