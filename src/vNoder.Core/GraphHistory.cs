namespace vNoder.Core;

/// <summary>
/// Undo and redo for a <see cref="Graph"/>, as a stack of whole-document snapshots.
/// </summary>
/// <remarks>
/// <para>
/// Snapshots rather than per-operation commands, deliberately. A snapshot is the same
/// <see cref="GraphDocument"/> a file saves, so everything that persists is undoable with nothing
/// further to write — in particular a new node's inline state, captured by reflection, needs no undo
/// code any more than it needs serializer code. A command per operation would be one more place to
/// edit whenever an operation is added.
/// </para>
/// <para>
/// The caller commits <em>after</em> each change. <see cref="Commit"/> compares the new snapshot with
/// the current one and records nothing when they are equal, so a click that moved nothing, or a
/// rejected connection, leaves no empty step behind. Restoring goes through
/// <see cref="GraphRestore.Reconcile"/>, which edits the graph in place and keeps the node instances
/// that survive.
/// </para>
/// </remarks>
public sealed class GraphHistory
{
    private readonly Graph _graph;
    private readonly NodeRegistry _registry;

    /// <summary>States before each change, oldest first; each labelled with the change it precedes.</summary>
    private readonly LinkedList<Entry> _undo = new();
    private readonly Stack<Entry> _redo = new();

    public GraphHistory(Graph graph, NodeRegistry registry, int capacity = 200)
    {
        _graph = graph;
        _registry = registry;
        Capacity = Math.Max(1, capacity);
        Current = Snapshot();
    }

    /// <summary>How many undo steps are kept; the oldest is dropped beyond this.</summary>
    public int Capacity { get; }

    /// <summary>
    /// The committed state, serialized. Comparing it with the state last saved is how the document
    /// knows it is clean again after undoing back to it.
    /// </summary>
    public string Current { get; private set; }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public string? UndoLabel => _undo.Last?.Value.Label;
    public string? RedoLabel => _redo.TryPeek(out var entry) ? entry.Label : null;

    /// <summary>Forgets all history and takes the graph as it stands as the starting point.</summary>
    public void Reset()
    {
        _undo.Clear();
        _redo.Clear();
        Current = Snapshot();
    }

    /// <summary>
    /// Records the graph's present state as the result of a change called <paramref name="label"/>.
    /// Returns false, recording nothing, when the graph has not actually changed.
    /// </summary>
    public bool Commit(string label)
    {
        var snapshot = Snapshot();
        if (snapshot == Current)
            return false;

        _undo.AddLast(new Entry(label, Current));
        if (_undo.Count > Capacity)
            _undo.RemoveFirst();

        _redo.Clear();
        Current = snapshot;
        return true;
    }

    /// <summary>
    /// Folds any uncommitted difference into the current state without making it a step of its own.
    /// Used on save, so that what is marked clean is what was actually written.
    /// </summary>
    public void Amend() => Current = Snapshot();

    /// <summary>Restores the state before the last change. Returns that change's label, or null.</summary>
    public string? Undo()
    {
        if (_undo.Last is not { } last)
            return null;

        _undo.RemoveLast();
        _redo.Push(last.Value with { State = Current });
        Restore(last.Value.State);
        return last.Value.Label;
    }

    /// <summary>Re-applies the last undone change. Returns its label, or null.</summary>
    public string? Redo()
    {
        if (!_redo.TryPop(out var next))
            return null;

        _undo.AddLast(next with { State = Current });
        Restore(next.State);
        return next.Label;
    }

    private void Restore(string state)
    {
        Current = state;
        GraphRestore.Reconcile(_graph, GraphFile.Deserialize(state), _registry);
    }

    private string Snapshot() => GraphFile.Serialize(GraphFile.Capture(_graph));

    private sealed record Entry(string Label, string State);
}
