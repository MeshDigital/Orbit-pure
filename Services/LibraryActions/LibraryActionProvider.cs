using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace SLSKDONET.Services.LibraryActions;

/// <summary>
/// Central provider for all library actions.
/// Discovers available actions and filters them based on context.
/// </summary>
public class LibraryActionProvider
{
    private readonly ILogger<LibraryActionProvider> _logger;
    private readonly List<ILibraryAction> _actions;

    public LibraryActionProvider(
        ILogger<LibraryActionProvider> logger,
        IEnumerable<ILibraryAction> actions)
    {
        _logger = logger;
        _actions = actions.ToList();
        
        _logger.LogInformation("LibraryActionProvider initialized with {Count} actions", _actions.Count);
    }

    /// <summary>
    /// Get all actions that can execute in the given context
    /// </summary>
    public List<ILibraryAction> GetAvailableActions(LibraryContext context)
    {
        return _actions.Where(a => a.CanExecute(context)).ToList();
    }

    /// <summary>
    /// Get all registered actions regardless of context
    /// </summary>
    public List<ILibraryAction> GetAllActions()
    {
        return _actions.ToList();
    }
}
