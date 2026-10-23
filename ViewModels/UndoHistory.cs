namespace Sonator.ViewModels;

// Keeps snapshots of earlier states for undo and redo. Recording a new change clears the redo history.
internal sealed class UndoHistory<T> where T : class
{
    private readonly Stack<T> _undoStates = new();
    private readonly Stack<T> _redoStates = new();

    public bool CanUndo => _undoStates.Count > 0;

    public bool CanRedo => _redoStates.Count > 0;

    // Every state kept, to undo and to redo.
    public IEnumerable<T> States => _undoStates.Concat(_redoStates);

    public void RecordChange(T stateBeforeChange)
    {
        _undoStates.Push(stateBeforeChange);
        _redoStates.Clear();
    }

    // Returns the state to restore. The current state is kept so it can be redone.
    public T Undo(T currentState)
    {
        if (!CanUndo)
        {
            throw new InvalidOperationException("There is no change to undo.");
        }

        _redoStates.Push(currentState);
        return _undoStates.Pop();
    }

    // Returns the state to restore. The current state is kept so it can be undone.
    public T Redo(T currentState)
    {
        if (!CanRedo)
        {
            throw new InvalidOperationException("There is no change to redo.");
        }

        _undoStates.Push(currentState);
        return _redoStates.Pop();
    }

    public void Clear()
    {
        _undoStates.Clear();
        _redoStates.Clear();
    }
}
