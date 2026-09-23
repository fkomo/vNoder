using System.Reflection;

namespace vNoder.Core;

/// <summary>
/// The node catalog. Built by reflecting over assemblies for <see cref="NodeDefinitionAttribute"/>,
/// so adding a node type never means editing this class.
/// </summary>
public sealed class NodeRegistry
{
    private readonly List<NodeDefinition> _definitions = [];

    public IReadOnlyList<NodeDefinition> Definitions => _definitions;

    /// <summary>Scans an assembly for node types. Safe to call more than once; duplicates are ignored.</summary>
    public NodeRegistry Scan(Assembly assembly)
    {
        foreach (var type in assembly.GetTypes())
        {
            if (type.IsAbstract || !typeof(NodeBase).IsAssignableFrom(type))
                continue;

            var attribute = NodeDefinitionAttribute.For(type);
            if (attribute is null)
                continue;

            if (_definitions.Any(d => d.NodeType == type))
                continue;

            var (category, name) = SplitPath(attribute.Path, type);
            _definitions.Add(new NodeDefinition(type, category, name, attribute.Description, attribute.Resizable));
        }

        _definitions.Sort(static (a, b) =>
        {
            var byCategory = string.Compare(a.Category, b.Category, StringComparison.OrdinalIgnoreCase);
            return byCategory != 0
                ? byCategory
                : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });

        return this;
    }

    /// <summary>Scans the assembly that declares <typeparamref name="T"/>.</summary>
    public NodeRegistry ScanAssemblyOf<T>() => Scan(typeof(T).Assembly);

    public IEnumerable<NodeDefinition> Search(string query) => _definitions.Where(d => d.Matches(query));

    /// <summary>
    /// Resolves a definition by its "Category/Name" path, as stored in a saved file. Falls back to a
    /// CLR type name so files written before a node carried a definition path still open.
    /// </summary>
    public NodeDefinition? Find(string path) =>
        _definitions.FirstOrDefault(d => string.Equals(d.Path, path, StringComparison.OrdinalIgnoreCase))
        ?? _definitions.FirstOrDefault(d => d.NodeType.FullName == path || d.NodeType.Name == path);

    private static (string Category, string Name) SplitPath(string path, Type type)
    {
        var separator = path.LastIndexOf('/');
        if (separator <= 0 || separator == path.Length - 1)
            return ("Misc", string.IsNullOrWhiteSpace(path) ? type.Name : path);

        return (path[..separator], path[(separator + 1)..]);
    }
}
