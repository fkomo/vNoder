using System.Text.Json;

namespace vNoder.Core;

/// <summary>Rebuilds a <see cref="Graph"/> from a <see cref="GraphDocument"/>.</summary>
public static class GraphRestore
{
    /// <summary>
    /// Replaces everything in <paramref name="graph"/> with the document's contents. Anything that
    /// cannot be restored is reported as a warning instead of throwing, so a file referencing one
    /// unknown node type still opens.
    /// </summary>
    public static GraphRestoreResult Into(Graph graph, GraphDocument document, NodeRegistry registry)
    {
        var warnings = new List<string>();

        if (document.FormatVersion > GraphDocument.CurrentFormatVersion)
        {
            warnings.Add(
                $"File format version {document.FormatVersion} is newer than this build understands "
                + $"(version {GraphDocument.CurrentFormatVersion}); parts of it may be missing.");
        }

        graph.Clear();

        if (document.IntervalMilliseconds is > 0 and var interval)
            graph.IntervalMilliseconds = interval;

        var byDocumentId = new Dictionary<Guid, NodeBase>();

        foreach (var entry in document.Nodes)
        {
            var definition = registry.Find(entry.Type);
            if (definition is null)
            {
                warnings.Add($"Unknown node type '{entry.Type}' was skipped.");
                continue;
            }

            if (byDocumentId.ContainsKey(entry.Id))
            {
                warnings.Add($"Duplicate node id {entry.Id:D} was skipped.");
                continue;
            }

            NodeBase node;
            try
            {
                node = definition.CreateInstance();
            }
            catch (Exception exception)
            {
                warnings.Add($"Could not create '{entry.Type}': {exception.Message}");
                continue;
            }

            // Keep the saved id so re-saving a file produces the same references.
            if (entry.Id != Guid.Empty)
                node.Id = entry.Id;

            node.Position = new GraphPoint(entry.X, entry.Y);
            node.Size = new GraphSize(entry.W ?? 0, entry.H ?? 0);
            RestoreState(node, entry, warnings);

            graph.Add(node);
            byDocumentId[entry.Id] = node;
        }

        foreach (var entry in document.Links)
        {
            if (!byDocumentId.TryGetValue(entry.FromNode, out var source)
                || !byDocumentId.TryGetValue(entry.ToNode, out var target))
            {
                // Expected whenever a node was skipped above; no second warning for it.
                continue;
            }

            var output = source.Outputs.FirstOrDefault(p => p.Name == entry.FromPort);
            var input = target.Inputs.FirstOrDefault(p => p.Name == entry.ToPort);

            if (output is null || input is null)
            {
                warnings.Add(
                    $"Link {source.Title}.{entry.FromPort} to {target.Title}.{entry.ToPort} was dropped: "
                    + "a port with that name no longer exists.");
                continue;
            }

            if (!graph.TryConnect(output, input, out _, out _, out var error))
                warnings.Add($"Link {output} to {input} was rejected: {error}");
        }

        RestoreGroups(graph, document, byDocumentId, warnings);

        return new GraphRestoreResult(warnings);
    }

    /// <summary>
    /// Adds a document's nodes to an existing graph without disturbing what is already there, giving
    /// every node a fresh id and shifting it by the given offset. This is the paste path, so the
    /// source ids are deliberately discarded: a fragment may be pasted many times into one graph.
    /// </summary>
    public static IReadOnlyList<NodeBase> Append(
        Graph graph,
        GraphDocument document,
        NodeRegistry registry,
        double offsetX,
        double offsetY)
    {
        var added = new List<NodeBase>();
        var byDocumentId = new Dictionary<Guid, NodeBase>();

        foreach (var entry in document.Nodes)
        {
            if (registry.Find(entry.Type) is not { } definition)
                continue;

            NodeBase node;
            try
            {
                node = definition.CreateInstance();
            }
            catch (Exception)
            {
                continue;
            }

            node.Position = new GraphPoint(entry.X + offsetX, entry.Y + offsetY);
            node.Size = new GraphSize(entry.W ?? 0, entry.H ?? 0);
            RestoreState(node, entry, []);

            graph.Add(node);
            added.Add(node);
            byDocumentId[entry.Id] = node;
        }

        foreach (var entry in document.Links)
        {
            if (!byDocumentId.TryGetValue(entry.FromNode, out var source)
                || !byDocumentId.TryGetValue(entry.ToNode, out var target))
            {
                continue;
            }

            var output = source.Outputs.FirstOrDefault(p => p.Name == entry.FromPort);
            var input = target.Inputs.FirstOrDefault(p => p.Name == entry.ToPort);

            if (output is not null && input is not null)
                graph.TryConnect(output, input, out _, out _, out _);
        }

        return added;
    }

    private static void RestoreGroups(
        Graph graph,
        GraphDocument document,
        Dictionary<Guid, NodeBase> byDocumentId,
        List<string> warnings)
    {
        if (document.Groups is null)
            return;

        foreach (var entry in document.Groups)
        {
            // Members that did not survive the load simply drop out of the group.
            var members = entry.NodeIds.Where(byDocumentId.ContainsKey).ToList();

            if (members.Count == 0)
            {
                warnings.Add($"Group '{entry.Name}' was dropped: none of its nodes loaded.");
                continue;
            }

            graph.AddGroup(new NodeGroup
            {
                Id = entry.Id == Guid.Empty ? Guid.NewGuid() : entry.Id,
                Name = entry.Name,
                IsCollapsed = entry.IsCollapsed,
                NodeIds = members,
            });
        }
    }

    private static void RestoreState(NodeBase node, NodeDocument entry, List<string> warnings)
    {
        if (entry.State is null or { Count: 0 })
            return;

        foreach (var property in NodeStateProperties.For(node.GetType()))
        {
            if (!entry.State.TryGetValue(property.Name, out var element))
                continue;

            try
            {
                property.SetValue(node, element.Deserialize(property.PropertyType));
            }
            catch (Exception exception)
            {
                // A changed property type is the usual cause; the node keeps its default.
                warnings.Add($"{node.Title}.{property.Name} kept its default: {exception.Message}");
            }
        }
    }
}
