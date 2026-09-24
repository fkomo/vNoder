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

    /// <summary>Drops every group. Used when groups are restored wholesale from a snapshot.</summary>
    internal void ClearGroups() => _groups.Clear();

    /// <summary>
    /// Puts nodes and links into the given orders. Node order is z-order in the view and the order a
    /// file lists things in, so restoring a snapshot in place has to reproduce it exactly, or an
    /// undo would not round-trip to the same document. Anything not ranked keeps its place at the end.
    /// </summary>
    internal void Reorder(IReadOnlyDictionary<NodeBase, int> nodeRank, IReadOnlyDictionary<Link, int> linkRank)
    {
        var nodes = _nodes.OrderBy(n => nodeRank.TryGetValue(n, out var r) ? r : int.MaxValue).ToList();
        _nodes.Clear();
        _nodes.AddRange(nodes);

        var links = _links.OrderBy(l => linkRank.TryGetValue(l, out var r) ? r : int.MaxValue).ToList();
        _links.Clear();
        _links.AddRange(links);
    }

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
    /// <remarks>
    /// A list may also feed an input that takes one of its items: the link then iterates
    /// (<see cref="Link.Iterate"/>), and the target runs once per item. Iterating is chosen only when
    /// the whole list does not fit, so a list into an "any" input stays whole until the user says
    /// otherwise. Call <see cref="Settle"/> afterwards: the target's outputs may have become lists.
    /// </remarks>
    public bool TryConnect(
        OutputPort source,
        InputPort target,
        out Link? link,
        out Link? replaced,
        out string? error)
    {
        link = null;
        replaced = null;

        var fit = Fit(source, target);
        if (fit == LinkFit.None)
        {
            error = $"{FriendlyTypeName(source.EffectiveType)} output cannot feed a {FriendlyTypeName(target.ValueType)} input.";
            return false;
        }

        link = Attach(source, target, fit == LinkFit.Each, out replaced, out error);
        return link is not null;
    }

    /// <summary>
    /// Connects without checking types, only that the link is not to itself and closes no cycle. For
    /// restoring a saved document, whose links arrive in no particular order: whether a link fits can
    /// depend on links upstream of it that are not restored yet, so types are checked once at the
    /// end, by <see cref="Settle"/>.
    /// </summary>
    internal Link? Attach(OutputPort source, InputPort target, bool iterate, out Link? replaced, out string? error)
    {
        replaced = null;
        error = null;

        if (source.Owner == target.Owner)
        {
            error = "A node cannot be wired to itself.";
            return null;
        }

        if (CreatesCycle(source.Owner, target.Owner))
        {
            error = "That connection would create a cycle.";
            return null;
        }

        if (target.Link is { } existing)
        {
            Disconnect(existing);
            replaced = existing;
        }

        var link = new Link(source, target) { Iterate = iterate };
        source.Add(link);
        target.Link = link;
        _links.Add(link);
        return link;
    }

    /// <summary>How an output could feed an input: whole, once per item, or not at all.</summary>
    public LinkFit Fit(OutputPort source, InputPort target)
    {
        var from = source.EffectiveType;

        if (PortTypeCompatibility.CanConnect(from, target.ValueType))
            return LinkFit.Whole;

        return CanIterateInto(from, target) ? LinkFit.Each : LinkFit.None;
    }

    /// <summary>
    /// Whether <paramref name="link"/> could be switched to <paramref name="iterate"/>, and if not, why.
    /// </summary>
    public bool CanSetIterate(Link link, bool iterate, out string? reason)
    {
        var from = link.Source.EffectiveType;
        var target = link.Target;
        reason = null;

        if (iterate)
        {
            if (ReferenceEquals(target, target.Owner.RunIfPort))
                reason = "Run If gates the whole node; it cannot run per item.";
            else if (!PortTypeCompatibility.CanIterate(from, target.ValueType))
                reason = $"A {FriendlyTypeName(from)} output is not a list of {FriendlyTypeName(target.ValueType)}.";
        }
        else if (!PortTypeCompatibility.CanConnect(from, target.ValueType))
        {
            reason = $"A {FriendlyTypeName(from)} output cannot feed a {FriendlyTypeName(target.ValueType)} input whole.";
        }

        return reason is null;
    }

    /// <summary>
    /// Switches a link between passing its list whole and running its target per item. Call
    /// <see cref="Settle"/> afterwards, since the target's outputs change type.
    /// </summary>
    public bool SetIterate(Link link, bool iterate, out string? reason)
    {
        if (!CanSetIterate(link, iterate, out reason))
            return false;

        link.Iterate = iterate;
        return true;
    }

    /// <summary>
    /// Makes every link consistent with what its source now carries, working downstream so each
    /// node's outputs are final before the links leaving it are checked. A link a list no longer
    /// fits whole starts iterating; an iterating link whose source is no longer a list goes back to
    /// whole; a link that fits neither way is removed, and returned.
    /// </summary>
    /// <remarks>
    /// This is what makes per-item evaluation propagate: iterate one link, and the chain below it
    /// runs per item too, as far as its inputs take single values. Idempotent, so running it over a
    /// graph restored from a settled document changes nothing.
    /// </remarks>
    public IReadOnlyList<Link> Settle()
    {
        var removed = new List<Link>();

        foreach (var node in TopologicalOrder())
        foreach (var input in node.Inputs)
        {
            if (input.Link is not { } link)
                continue;

            var from = link.Source.EffectiveType;
            var whole = PortTypeCompatibility.CanConnect(from, input.ValueType);
            var each = CanIterateInto(from, input);

            if (link.Iterate ? each : whole)
                continue;

            if (link.Iterate ? whole : each)
            {
                link.Iterate = !link.Iterate;
                continue;
            }

            Disconnect(link);
            removed.Add(link);
        }

        return removed;
    }

    private static bool CanIterateInto(Type from, InputPort target) =>
        !ReferenceEquals(target, target.Owner.RunIfPort)
        && PortTypeCompatibility.CanIterate(from, target.ValueType);

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

    internal static string FriendlyTypeName(Type type) => PortTypeCompatibility.Describe(type);
}

/// <summary>How an output could feed an input; see <see cref="Graph.Fit"/>.</summary>
public enum LinkFit
{
    /// <summary>The types do not fit either way.</summary>
    None,

    /// <summary>The value is passed as it is.</summary>
    Whole,

    /// <summary>The value is a list, and the input takes one of its items: the target runs per item.</summary>
    Each,
}
