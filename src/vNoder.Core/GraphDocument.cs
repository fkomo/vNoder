using System.Text.Json;
using System.Text.Json.Serialization;

namespace vNoder.Core;

/// <summary>
/// The on-disk shape of a graph. Nodes are identified by their <see cref="NodeDefinition.Path"/>
/// ("Math/Add"), so a saved file is readable and resolves through <see cref="NodeRegistry"/> without
/// depending on CLR type names.
/// </summary>
public sealed class GraphDocument
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; set; } = CurrentFormatVersion;

    /// <summary>Optional zoom/pan, so a file reopens looking the way it was left.</summary>
    public GraphViewState? View { get; set; }

    /// <summary>Continuous-run tick interval. Null on a clipboard fragment.</summary>
    public double? IntervalMilliseconds { get; set; }

    public List<NodeDocument> Nodes { get; set; } = [];

    public List<LinkDocument> Links { get; set; } = [];

    /// <summary>Presentational node groupings.</summary>
    public List<GroupDocument>? Groups { get; set; }
}

public sealed class GroupDocument
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "Group";
    public bool IsCollapsed { get; set; }
    public List<Guid> NodeIds { get; set; } = [];
}

public sealed class GraphViewState
{
    public double Scale { get; set; } = 1;
    public double OffsetX { get; set; }
    public double OffsetY { get; set; }
}

public sealed class NodeDocument
{
    public Guid Id { get; set; }

    /// <summary>The node definition path, e.g. "Generators/Random Number".</summary>
    public string Type { get; set; } = string.Empty;

    public double X { get; set; }
    public double Y { get; set; }

    /// <summary>Set only once the user has resized the node; null means the view's default.</summary>
    public double? W { get; set; }
    public double? H { get; set; }

    /// <summary>Inline editable properties, captured by <see cref="NodeStateProperties"/>.</summary>
    public Dictionary<string, JsonElement>? State { get; set; }
}

public sealed class LinkDocument
{
    public Guid FromNode { get; set; }
    public string FromPort { get; set; } = string.Empty;
    public Guid ToNode { get; set; }
    public string ToPort { get; set; } = string.Empty;

    /// <summary>True when the link runs its target per item (<see cref="Link.Iterate"/>); omitted otherwise.</summary>
    public bool? Each { get; set; }
}

/// <summary>
/// Outcome of restoring a document. A file that is partly unreadable still loads: whatever could not
/// be restored is reported here rather than thrown, so one unknown node type does not cost the user
/// the rest of the graph.
/// </summary>
public sealed class GraphRestoreResult(IReadOnlyList<string> warnings)
{
    public IReadOnlyList<string> Warnings { get; } = warnings;

    public bool IsComplete => Warnings.Count == 0;
}
