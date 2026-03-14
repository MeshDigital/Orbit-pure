using Avalonia.Controls;
using System;
using System.Globalization;

namespace SLSKDONET.Views.Avalonia;

/// <summary>
/// Phase 6D: Home page with dashboard stats and quick actions.
/// </summary>
public partial class HomePage : UserControl
{
    public HomePage()
    {
        InitializeComponent();
    }

    public HomePage(SLSKDONET.ViewModels.HomeViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}

