namespace vNoder.Core;

/// <summary>A directed edge from an output port to an input port.</summary>
public sealed class Link
{
    internal Link(OutputPort source, InputPort target)
    {
        Source = source;
        Target = target;
    }

    public Guid Id { get; } = Guid.NewGuid();
    public OutputPort Source { get; }
    public InputPort Target { get; }

    /// <summary>
    /// Runs the target once per item of the list this link carries, instead of passing the list
    /// whole. A node with any such input runs per item, and its outputs become lists; see
    /// <see cref="GraphExecutor"/>. Set automatically when only an item fits the input, and by hand
    /// on an input that would also take the whole list ("any"); <see cref="Graph.Settle"/> keeps it
    /// consistent as the graph changes.
    /// </summary>
    public bool Iterate { get; internal set; }

    public override string ToString() => $"{Source} -> {Target}{(Iterate ? " (each)" : string.Empty)}";
}
