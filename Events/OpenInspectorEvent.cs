namespace SLSKDONET.Events;

public class OpenInspectorEvent
{
    public object ViewModel { get; }
    public string Title { get; }
    public string Icon { get; }

    public OpenInspectorEvent(object viewModel, string title = "INSPECTOR", string icon = "ℹ️")
    {
        ViewModel = viewModel;
        Title = title;
        Icon = icon;
    }
}
