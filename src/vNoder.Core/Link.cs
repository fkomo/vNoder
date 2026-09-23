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

    public override string ToString() => $"{Source} -> {Target}";
}
