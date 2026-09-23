namespace vNoder.Core;

/// <summary>Nodes plus the links between them, with connection rules and evaluation ordering.</summary>
public sealed class Graph
{
    private readonly List<NodeBase> _nodes = [];
    private readonly List<Link> _links = [];
    private readonly List<NodeGroup> _groups = [];

    public IReadOnlyList<NodeBase> Nodes => _nodes;
    public IReadOnlyList<Link> Links => _links;

    /// <summary>Presentation-only groupings; ignored entirely by evaluation.</summary>
    public IReadOnlyList<NodeGroup> Groups => _groups;

    /// <summary>
    /// Tick interval for continuous runs, in milliseconds. Graph-level configuration rather than a
    /// node: every graph can run continuously, so there is nothing to wire up and nothing to forget.
    /// </summary>
    public double IntervalMilliseconds { get; set; } = 500;

    /// <summary>Empties the graph. Used when loading a file over the current one.</summary>
    public void Clear()
    {
        foreach (var link in _links.ToList())
            Disconnect(link);

        _nodes.Clear();
        _links.Clear();
        _groups.Clear();
    }

    public void AddGroup(NodeGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);

        // A node belongs to at most one group, so joining a new one leaves the old.
        foreach (var existing in _groups)
            existing.NodeIds.RemoveAll(group.NodeIds.Contains);

        _groups.RemoveAll(g => g.NodeIds.Count == 0);
        _groups.Add(group);
    }

    public void RemoveGroup(NodeGroup group) => _groups.Remove(group);

    /// <summary>The group a node belongs to, if any.</summary>
    public NodeGroup? GroupOf(NodeBase node) => _groups.FirstOrDefault(g => g.NodeIds.Contains(node.Id));

    public void Add(NodeBase node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!_nodes.Contains(node))
            _nodes.Add(node);
    }

    /// <summary>Removes a node and every link touching it. Returns the links that were removed.</summary>
    public IReadOnlyList<Link> Remove(NodeBase node)
    {
        var removed = _links
            .Where(l => l.Source.Owner == node || l.Target.Owner == node)
            .ToList();

        foreach (var link in removed)
            Disconnect(link);

        _nodes.Remove(node);

        foreach (var group in _groups)
            group.NodeIds.Remove(node.Id);

        // A group that has lost all its members is meaningless.
        _groups.RemoveAll(g => g.NodeIds.Count == 0);

        return removed;
    }

    /// <summary>
    /// Connects an output to an input, enforcing type compatibility and rejecting cycles. Because an
    /// input takes at most one link, an existing link into <paramref name="target"/> is replaced and
    /// reported through <paramref name="replaced"/>.
    /// </summary>
    public bool TryConnect(
        OutputPort source,
        InputPort target,
        out Link? link,
        out Link? replaced,
        out string? error)
    {
        link = null;
        replaced = null;
        error = null;

        if (source.Owner == target.Owner)
        {
            error = "A node cannot be wired to itself.";
            return false;
        }

        if (!PortTypeCompatibility.CanConnect(source.ValueType, target.ValueType))
        {
            error = $"{FriendlyTypeName(source.ValueType)} output cannot feed a {FriendlyTypeName(target.ValueType)} input.";
            return false;
        }

        if (CreatesCycle(source.Owner, target.Owner))
        {
            error = "That connection would create a cycle.";
            return false;
        }

        if (target.Link is { } existing)
        {
            Disconnect(existing);
            replaced = existing;
        }

        link = new Link(source, target);
        source.Add(link);
        target.Link = link;
        _links.Add(link);
        return true;
    }

    public void Disconnect(Link link)
    {
        link.Source.Remove(link);
        if (ReferenceEquals(link.Target.Link, link))
        {
            link.Target.Link = null;
            link.Target.Value = null;
        }

        _links.Remove(link);
    }

    /// <summary>True when wiring <paramref name="from"/> into <paramref name="to"/> would close a loop.</summary>
    public bool CreatesCycle(NodeBase from, NodeBase to)
    {
        if (from == to)
            return true;

        // Walk downstream from the prospective target; reaching the source means a loop.
        var pending = new Stack<NodeBase>();
        var seen = new HashSet<NodeBase>();
        pending.Push(to);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (current == from)
                return true;

            if (!seen.Add(current))
                continue;

            foreach (var output in current.Outputs)
            foreach (var link in output.Links)
                pending.Push(link.Target.Owner);
        }

        return false;
    }

    /// <summary>
    /// Evaluation order via Kahn's algorithm. Cycles are rejected at connection time, so the result
    /// always covers every node; any node whose dependencies are somehow unresolved is appended last
    /// rather than dropped.
    /// </summary>
    public IReadOnlyList<NodeBase> TopologicalOrder()
    {
        var incoming = _nodes.ToDictionary(
            node => node,
            node => node.Inputs.Count(input => input.IsConnected));

        var ready = new Queue<NodeBase>(_nodes.Where(node => incoming[node] == 0));
        var ordered = new List<NodeBase>(_nodes.Count);

        while (ready.Count > 0)
        {
            var node = ready.Dequeue();
            ordered.Add(node);

            foreach (var output in node.Outputs)
            foreach (var link in output.Links)
            {
                var downstream = link.Target.Owner;
                if (--incoming[downstream] == 0)
                    ready.Enqueue(downstream);
            }
        }

        if (ordered.Count != _nodes.Count)
            ordered.AddRange(_nodes.Except(ordered));

        return ordered;
    }

    internal static string FriendlyTypeName(Type type) => type switch
    {
        _ when type == typeof(double) => "number",
        _ when type == typeof(string) => "text",
        _ when type == typeof(bool) => "boolean",
        _ when type == typeof(byte[]) => "bytes",
        _ when type == typeof(string[]) => "text list",
        _ when type == typeof(object) => "any",
        _ => type.Name,
    };
}
