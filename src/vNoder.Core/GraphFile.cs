using System.Text.Json;
using System.Text.Json.Serialization;

namespace vNoder.Core;

/// <summary>Reads and writes <see cref="Graph"/> as JSON.</summary>
public static class GraphFile
{
    /// <summary>Conventional extension for a saved graph.</summary>
    public const string Extension = ".vnoder";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ---- Capture --------------------------------------------------------------------------

    public static GraphDocument Capture(Graph graph, GraphViewState? view = null)
    {
        var document = new GraphDocument
        {
            View = view,
            IntervalMilliseconds = graph.IntervalMilliseconds,
        };

        foreach (var node in graph.Nodes)
        {
            document.Nodes.Add(new NodeDocument
            {
                Id = node.Id,
                Type = TypeKey(node),
                X = node.Position.X,
                Y = node.Position.Y,
                W = node.Size.HasWidth ? node.Size.Width : null,
                H = node.Size.HasHeight ? node.Size.Height : null,
                Pairing = PairingText(node.Pairing),
                SkipFailedItems = node.SkipFailedItems ? true : null,
                State = CaptureState(node),
            });
        }

        foreach (var link in graph.Links)
        {
            document.Links.Add(new LinkDocument
            {
                FromNode = link.Source.Owner.Id,
                FromPort = link.Source.Name,
                ToNode = link.Target.Owner.Id,
                ToPort = link.Target.Name,
                Each = link.Iterate ? true : null,
            });
        }

        foreach (var group in graph.Groups)
        {
            document.Groups ??= [];
            document.Groups.Add(new GroupDocument
            {
                Id = group.Id,
                Name = group.Name,
                IsCollapsed = group.IsCollapsed,
                NodeIds = [.. group.NodeIds],
            });
        }

        foreach (var note in graph.Notes)
        {
            document.Notes ??= [];
            document.Notes.Add(CaptureNote(note));
        }

        return document;
    }

    /// <summary>
    /// Captures just the given nodes, plus only those links whose <em>both</em> ends are among them.
    /// Used for the clipboard: a copied fragment carries its internal wiring and nothing else.
    /// </summary>
    public static GraphDocument CaptureSubset(Graph graph, IReadOnlyCollection<NodeBase> nodes)
    {
        var included = nodes.ToHashSet();
        var document = new GraphDocument();

        foreach (var node in graph.Nodes.Where(included.Contains))
        {
            document.Nodes.Add(new NodeDocument
            {
                Id = node.Id,
                Type = TypeKey(node),
                X = node.Position.X,
                Y = node.Position.Y,
                W = node.Size.HasWidth ? node.Size.Width : null,
                H = node.Size.HasHeight ? node.Size.Height : null,
                Pairing = PairingText(node.Pairing),
                SkipFailedItems = node.SkipFailedItems ? true : null,
                State = CaptureState(node),
            });
        }

        foreach (var link in graph.Links)
        {
            if (!included.Contains(link.Source.Owner) || !included.Contains(link.Target.Owner))
                continue;

            document.Links.Add(new LinkDocument
            {
                FromNode = link.Source.Owner.Id,
                FromPort = link.Source.Name,
                ToNode = link.Target.Owner.Id,
                ToPort = link.Target.Name,
                Each = link.Iterate ? true : null,
            });
        }

        // Notes on copied nodes travel with them; notes on groups do not, since groups are not copied.
        var ids = included.Select(n => n.Id).ToHashSet();
        foreach (var note in graph.Notes.Where(n => ids.Contains(n.TargetId)))
        {
            document.Notes ??= [];
            document.Notes.Add(CaptureNote(note));
        }

        return document;
    }

    private static NoteDocument CaptureNote(GraphNote note) => new()
    {
        Id = note.Id,
        Target = note.TargetId,
        X = note.OffsetX,
        Y = note.OffsetY,
        W = note.Width > 0 ? note.Width : null,
        H = note.Height > 0 ? note.Height : null,
        Text = note.Text,
    };

    public static string Serialize(GraphDocument document) =>
        JsonSerializer.Serialize(document, Options);

    /// <summary>
    /// Parses a document that is known to be well formed, such as an undo snapshot. Unlike
    /// <see cref="TryParse"/> this accepts an empty graph and throws on malformed input.
    /// </summary>
    public static GraphDocument Deserialize(string json) =>
        JsonSerializer.Deserialize<GraphDocument>(json, Options)
        ?? throw new InvalidDataException("The text did not contain a graph.");

    /// <summary>What a node is saved as: its definition path, so files survive class renames.</summary>
    internal static string TypeKey(NodeBase node) =>
        NodeDefinitionAttribute.For(node.GetType())?.Path ?? node.GetType().FullName ?? node.GetType().Name;

    /// <summary>
    /// A pairing as saved: by name in camel case ("longest", "crossProduct"), null for the default so it
    /// is left out of the file. By name rather than number, unlike inline enums: it is one field the
    /// format owns, so it can afford to be readable.
    /// </summary>
    internal static string? PairingText(ItemPairing pairing) =>
        pairing == ItemPairing.ByIndex ? null : JsonNamingPolicy.CamelCase.ConvertName(pairing.ToString());

    /// <summary>The pairing a saved name stands for; anything missing or unknown is the default.</summary>
    internal static ItemPairing ParsePairing(string? text) =>
        Enum.TryParse<ItemPairing>(text, ignoreCase: true, out var pairing) && Enum.IsDefined(pairing) ? pairing : ItemPairing.ByIndex;

    /// <summary>Parses a document, returning null rather than throwing on anything unrecognisable.</summary>
    public static GraphDocument? TryParse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            var document = JsonSerializer.Deserialize<GraphDocument>(json, Options);
            return document?.Nodes.Count > 0 ? document : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Dictionary<string, JsonElement>? CaptureState(NodeBase node)
    {
        var properties = NodeStateProperties.For(node.GetType());
        if (properties.Count == 0)
            return null;

        var state = new Dictionary<string, JsonElement>(properties.Count);
        foreach (var property in properties)
            state[property.Name] = JsonSerializer.SerializeToElement(property.GetValue(node), Options);

        return state;
    }

    // ---- File IO --------------------------------------------------------------------------

    public static async Task SaveAsync(Graph graph, string path, GraphViewState? view = null, CancellationToken cancellationToken = default)
    {
        var document = Capture(graph, view);

        // Serialize to a buffer first so a failure cannot leave a half-written file behind.
        var json = JsonSerializer.SerializeToUtf8Bytes(document, Options);
        await File.WriteAllBytesAsync(path, json, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<GraphDocument> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);

        var document = await JsonSerializer.DeserializeAsync<GraphDocument>(stream, Options, cancellationToken)
            .ConfigureAwait(false);

        return document ?? throw new InvalidDataException("The file did not contain a graph.");
    }
}
