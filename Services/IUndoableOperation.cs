namespace SLSKDONET.Services;

/// <summary>
/// Represents a reversible structural action in the workstation timeline.
/// </summary>
public interface IUndoableOperation
{
    string Description { get; }
    void Execute();
    void Undo();
}
