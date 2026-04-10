using System.Collections.Generic;
using System.Linq;

namespace SLSKDONET.Services;

/// <summary>
/// Global undo/redo stack for workstation structural actions.
/// </summary>
public sealed class UndoService : IUndoService
{
    private const int MaxEntries = 500;
    private readonly Stack<IUndoableOperation> _undo = new();
    private readonly Stack<IUndoableOperation> _redo = new();

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public void Push(IUndoableOperation operation)
    {
        if (operation == null)
        {
            return;
        }

        _undo.Push(operation);
        _redo.Clear();
        Trim(_undo);
    }

    public void Undo()
    {
        if (!CanUndo)
        {
            return;
        }

        var op = _undo.Pop();
        op.Undo();
        _redo.Push(op);
        Trim(_redo);
    }

    public void Redo()
    {
        if (!CanRedo)
        {
            return;
        }

        var op = _redo.Pop();
        op.Execute();
        _undo.Push(op);
        Trim(_undo);
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }

    private static void Trim(Stack<IUndoableOperation> stack)
    {
        if (stack.Count <= MaxEntries)
        {
            return;
        }

        // Stack.ToArray() returns newest->oldest. Reverse to oldest->newest,
        // drop oldest overflow, then rebuild preserving newest on top.
        var ordered = stack.ToArray();
        System.Array.Reverse(ordered);
        var keep = ordered.Skip(ordered.Length - MaxEntries);

        stack.Clear();
        foreach (var op in keep)
        {
            stack.Push(op);
        }
    }
}
