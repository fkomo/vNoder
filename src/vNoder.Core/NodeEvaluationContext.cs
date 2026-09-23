namespace vNoder.Core;

/// <summary>Per-run state handed to every node's <see cref="NodeBase.EvaluateAsync"/>.</summary>
public sealed class NodeEvaluationContext(long tick, CancellationToken cancellationToken)
{
    /// <summary>0 for a one-shot run; increments once per clock tick in continuous mode.</summary>
    public long Tick { get; } = tick;

    public CancellationToken CancellationToken { get; } = cancellationToken;
}
