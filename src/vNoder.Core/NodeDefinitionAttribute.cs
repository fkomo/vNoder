using System.Reflection;

namespace vNoder.Core;

/// <summary>
/// Marks a <see cref="NodeBase"/> subclass as a node type discoverable by <see cref="NodeRegistry"/>.
/// This attribute plus an <c>EvaluateAsync</c> override is the whole contract for a new node type —
/// there is intentionally no registration list to maintain elsewhere.
/// </summary>
/// <param name="path">"Category/Name", e.g. "Math/Add".</param>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class NodeDefinitionAttribute(string path) : Attribute
{
    public string Path { get; } = path;

    /// <summary>Optional one-line description surfaced in the search popup.</summary>
    public string? Description { get; init; }

    /// <summary>
    /// Whether the node shows a resize grip. Off by default: most nodes are a fixed list of ports and
    /// gain nothing from being resized. Turn it on for nodes with a body worth enlarging, such as a
    /// value preview.
    /// </summary>
    public bool Resizable { get; init; }

    public static NodeDefinitionAttribute? For(Type nodeType) =>
        nodeType.GetCustomAttribute<NodeDefinitionAttribute>(inherit: false);
}
