using System;
using System.ComponentModel;
using SLSKDONET.Models;
using SLSKDONET.Views;

namespace SLSKDONET.ViewModels;

public partial class LibraryViewModel
{
    private bool _isMixHelperVisible;
    public bool IsMixHelperVisible
    {
        get => _isMixHelperVisible;
        set 
        { 
            if (SetProperty(ref _isMixHelperVisible, value))
            {
                UpdateWorkspaceFromState();
                OnPropertyChanged(nameof(IsRightPanelVisible));
                
                // Auto-dock player to bottom when sidepanel opens
                if (value && _playerViewModel != null)
                {
                    _playerViewModel.CurrentDockLocation = PlayerDockLocation.BottomBar;
                    _playerViewModel.IsPlayerVisible = true; // Ensure player is visible
                }
            }
        }
    }

    private ActiveWorkspace _currentWorkspace = ActiveWorkspace.Selector;
    public ActiveWorkspace CurrentWorkspace
    {
        get => _currentWorkspace;
        set
        {
            if (SetProperty(ref _currentWorkspace, value))
            {
                ApplyWorkspaceState();
            }
        }
    }

    public bool IsRightPanelVisible => IsMixHelperVisible;

    private bool _isDiscoveryLaneVisible;
    public bool IsDiscoveryLaneVisible
    {
        get => _isDiscoveryLaneVisible;
        set { _isDiscoveryLaneVisible = value; OnPropertyChanged(); }
    }

    private Avalonia.Controls.GridLength _discoveryLaneHeight = new(350);
    public Avalonia.Controls.GridLength DiscoveryLaneHeight
    {
        get => _discoveryLaneHeight;
        set { _discoveryLaneHeight = value; OnPropertyChanged(); }
    }

    private bool _isQuickLookVisible;
    public bool IsQuickLookVisible
    {
        get => _isQuickLookVisible;
        set { _isQuickLookVisible = value; OnPropertyChanged(); }
    }

    private bool _isUpgradeScoutVisible;
    public bool IsUpgradeScoutVisible
    {
        get => _isUpgradeScoutVisible;
        set { _isUpgradeScoutVisible = value; OnPropertyChanged(); }
    }

    private bool _isUpdatingState;

    private void UpdateWorkspaceFromState()
    {
        if (_isUpdatingState) return;
        _isUpdatingState = true;

        if (IsMixHelperVisible)
        {
            CurrentWorkspace = ActiveWorkspace.Preparer;
        }
        else if (IsDiscoveryLaneVisible)
        {
             CurrentWorkspace = ActiveWorkspace.Preparer;
        }
        else
        {
            CurrentWorkspace = ActiveWorkspace.Selector;
        }

        _isUpdatingState = false;
    }

    private void ApplyWorkspaceState()
    {
        if (_isUpdatingState) return;
        _isUpdatingState = true;

        switch (CurrentWorkspace)
        {
            case ActiveWorkspace.Selector:
                IsMixHelperVisible = false;
                IsDiscoveryLaneVisible = false;
                break;
            case ActiveWorkspace.Analyst:
                IsMixHelperVisible = false;
                IsDiscoveryLaneVisible = true; // Enabled for Analyst too
                break;
            case ActiveWorkspace.Preparer:
                IsMixHelperVisible = true;
                IsDiscoveryLaneVisible = true;
                break;
            case ActiveWorkspace.Forensic:
                IsMixHelperVisible = false;
                IsDiscoveryLaneVisible = false;
                break;
            case ActiveWorkspace.Industrial:
                IsMixHelperVisible = false;
                IsDiscoveryLaneVisible = false;
                break;
        }

        _isUpdatingState = false;
    }
}
