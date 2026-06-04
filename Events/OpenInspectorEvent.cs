namespace SLSKDONET.Events;

public class OpenInspectorEvent
{
    public object ViewModel { get; }
    public string Title { get; }
    public string Icon { get; }
    public string Source { get; }

    public OpenInspectorEvent(object viewModel, string? title = null, string? icon = null, string? source = null)
    {
        ViewModel = viewModel;
        var normalizedSource = NormalizeSource(source);
        var presentation = ResolvePresentationDefaults(normalizedSource);

        Title = string.IsNullOrWhiteSpace(title) ? presentation.Title : title.Trim();
        Icon = string.IsNullOrWhiteSpace(icon) ? presentation.Icon : icon.Trim();
        Source = normalizedSource;
    }

    public static OpenInspectorEvent Create(object viewModel, string? source = null)
    {
        return new OpenInspectorEvent(viewModel, source: source);
    }

    public static (string Title, string Icon) ResolvePresentationDefaults(string? source)
    {
        return NormalizeSource(source) switch
        {
            "Library.TrackSelection.Double" => ("DOUBLE INSPECTOR", "🔗"),
            "Library.TrackSelection.Single" => ("TRACK INSPECTOR", "🔍"),
            "Library.TrackSelection.EmptyIntelligence" => ("INTELLIGENCE", "🧠"),
            "Library.ProjectSelection.EmptyIntelligence" => ("INTELLIGENCE", "🧠"),
            "Search.Selection.Single" => ("TRACK INSPECTOR", "🔍"),
            "Downloads.Selection.Single" => ("TRACK INSPECTOR", "🔍"),
            "FlowBuilder.TransitionInspector" => ("TRACK INSPECTOR", "🔬"),
            _ => ("INSPECTOR", "ℹ️")
        };
    }

    private static string NormalizeSource(string? source)
    {
        return string.IsNullOrWhiteSpace(source) ? "Unknown" : source.Trim();
    }
}
