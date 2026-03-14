namespace SLSKDONET.Services;

/// <summary>
/// An abstraction for requesting simple text input from the user.
/// </summary>
public interface IUserInputService
{
    string? GetInput(string prompt, string title, string defaultValue = "");
}