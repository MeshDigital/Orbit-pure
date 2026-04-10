namespace SLSKDONET.Services;

public interface IUndoService
{
    bool CanUndo { get; }
    bool CanRedo { get; }

    void Push(IUndoableOperation operation);
    void Undo();
    void Redo();
    void Clear();
}
