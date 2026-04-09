using Avalonia;
using Avalonia.Controls;
using SLSKDONET.ViewModels.Workstation;

namespace SLSKDONET.Views.Avalonia;

public partial class WorkstationPage : UserControl
{
    public WorkstationPage()
    {
        InitializeComponent();
        if (!Design.IsDesignMode &&
            Application.Current is App app && app.Services != null)
        {
            DataContext = app.Services.GetService(typeof(WorkstationViewModel))
                          as WorkstationViewModel;
        }
    }
}
