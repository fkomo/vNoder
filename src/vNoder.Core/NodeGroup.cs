namespace vNoder.Core;

/// <summary>
/// A named box drawn around a set of nodes. Purely presentational: <see cref="GraphExecutor"/> never
/// looks at groups, and grouping changes nothing about evaluation order or connectivity. It lives
/// here rather than in the UI only so that it travels with the saved document, as node positions do.
/// </summary>
public sealed class NodeGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "Group";

    public bool IsCollapsed { get; set; }

    /// <summary>Ids of the member nodes. A node may belong to at most one group.</summary>
    public List<Guid> NodeIds { get; init; } = [];

    public override string ToString() => $"{Name} ({NodeIds.Count} nodes)";
}
