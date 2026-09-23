namespace vNoder.Core;

/// <summary>Outcome of one evaluation pass.</summary>
public sealed class GraphRunResult(long tick, int nodesEvaluated, IReadOnlyList<NodeBase> failed)
{
    public long Tick { get; } = tick;
    public int NodesEvaluated { get; } = nodesEvaluated;

    /// <summary>Nodes whose evaluation threw; their <see cref="NodeBase.LastError"/> holds the message.</summary>
    public IReadOnlyList<NodeBase> Failed { get; } = failed;

    public bool HasErrors => Failed.Count > 0;
}
