namespace vNoder.Core;

/// <summary>One entry in the node catalog, built by <see cref="NodeRegistry"/> from an attribute.</summary>
public sealed class NodeDefinition
{
    public NodeDefinition(
        Type nodeType,
        string category,
        string name,
        string? description,
        bool isResizable,
        IReadOnlyList<string>? formerPaths = null)
    {
        FormerPaths = formerPaths ?? [];
        NodeType = nodeType;
        Category = category;
        Name = name;
        Description = description;
        IsResizable = isResizable;
    }

    public Type NodeType { get; }
    public string Category { get; }
    public string Name { get; }
    public string? Description { get; }
    public bool IsResizable { get; }

    public string Path => $"{Category}/{Name}";

    /// <summary>Earlier paths that still resolve to this definition; see <see cref="NodeDefinitionAttribute.FormerPaths"/>.</summary>
    public IReadOnlyList<string> FormerPaths { get; }

    private IReadOnlyList<Type>? _inputTypes;
    private IReadOnlyList<Type>? _iterableInputTypes;

    /// <summary>
    /// The port types this node accepts. Ports are declared in the constructor, so this is discovered
    /// by instantiating the node once and caching the result.
    /// </summary>
    public IReadOnlyList<Type> InputTypes
    {
        get
        {
            if (_inputTypes is null)
                DiscoverInputs();

            return _inputTypes!;
        }
    }

    /// <summary>
    /// True when an output of <paramref name="outputType"/> could feed one of this node's inputs,
    /// whole or once per item (every input but Run If can run per item).
    /// </summary>
    public bool AcceptsInput(Type outputType) =>
        InputTypes.Any(input => PortTypeCompatibility.CanConnect(outputType, input))
        || _iterableInputTypes!.Any(input => PortTypeCompatibility.CanIterate(outputType, input));

    private void DiscoverInputs()
    {
        try
        {
            var node = CreateInstance();
            _inputTypes = [.. node.Inputs.Select(p => p.ValueType)];
            _iterableInputTypes = [.. node.Inputs.Where(p => p.Name != NodeBase.RunIfPortName).Select(p => p.ValueType)];
        }
        catch (Exception)
        {
            // A node that cannot be constructed cannot accept anything either.
            _inputTypes = [];
            _iterableInputTypes = [];
        }
    }

    public NodeBase CreateInstance() =>
        (NodeBase)(Activator.CreateInstance(NodeType)
                   ?? throw new InvalidOperationException($"Could not instantiate node type {NodeType}."));

    /// <summary>Substring match over name, category and description, used by the search popup.</summary>
    public bool Matches(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return true;

        return Name.Contains(query, StringComparison.OrdinalIgnoreCase)
               || Category.Contains(query, StringComparison.OrdinalIgnoreCase)
               || (Description?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    public override string ToString() => Path;
}
