namespace vNoder.Core;

public abstract class Port
{
    protected Port(NodeBase owner, string name, Type valueType, int index)
    {
        Owner = owner;
        Name = name;
        ValueType = valueType;
        Index = index;
    }

    public NodeBase Owner { get; }
    public string Name { get; }
    public Type ValueType { get; }

    /// <summary>Position of this port within its node's input or output list; drives anchor layout.</summary>
    public int Index { get; }

    public override string ToString() => $"{Owner.Title}.{Name}";
}

/// <summary>An input accepts at most one incoming link.</summary>
public sealed class InputPort : Port
{
    internal InputPort(NodeBase owner, string name, Type valueType, int index)
        : base(owner, name, valueType, index)
    {
    }

    public Link? Link { get; internal set; }

    public bool IsConnected => Link is not null;

    /// <summary>Value pulled from the connected output before this node is evaluated.</summary>
    public object? Value { get; internal set; }

    /// <summary>Reads the input as <typeparamref name="T"/>, falling back when unset or mistyped.</summary>
    public T ValueOr<T>(T fallback) => Value is T typed ? typed : fallback;
}

/// <summary>An output may fan out to many inputs.</summary>
public sealed class OutputPort : Port
{
    private readonly List<Link> _links = [];

    internal OutputPort(NodeBase owner, string name, Type valueType, int index)
        : base(owner, name, valueType, index)
    {
    }

    public IReadOnlyList<Link> Links => _links;

    /// <summary>
    /// What this output actually carries: its declared type, or a list of it while the node runs per
    /// item. Done stays a single flag, since it reports on the node, not on an item.
    /// </summary>
    public Type EffectiveType =>
        Owner.RunsPerItem && !ReferenceEquals(this, Owner.DonePort)
            ? PortTypeCompatibility.ListOf(ValueType)
            : ValueType;

    /// <summary>Value written by the owning node during evaluation.</summary>
    public object? Value { get; set; }

    internal void Add(Link link) => _links.Add(link);

    internal void Remove(Link link) => _links.Remove(link);
}
