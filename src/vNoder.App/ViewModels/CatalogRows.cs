using vNoder.Core;

namespace vNoder.App.ViewModels;

/// <summary>
/// One row of the node catalog. The tree is flattened into a single list of rows, category headers
/// and nodes, rather than built as a WPF <c>TreeView</c>: one list keeps the search box in charge of
/// the keyboard (Up, Down and Enter move over every row, headers included) and keeps a single
/// selection to commit.
/// </summary>
public abstract class CatalogRow;

/// <summary>A category header: the tree's only branch level. Clicking or Enter opens and closes it.</summary>
public sealed class CatalogCategoryRow(string category, int count, bool isExpanded) : CatalogRow
{
    public string Category { get; } = category;

    /// <summary>How many nodes of this category match the current search and filter.</summary>
    public int Count { get; } = count;

    public bool IsExpanded { get; } = isExpanded;

    public string Glyph => IsExpanded ? "▾" : "▸";
}

/// <summary>A node type to add, indented under its category, which it therefore does not repeat.</summary>
public sealed class CatalogNodeRow(NodeDefinition definition) : CatalogRow
{
    public NodeDefinition Definition { get; } = definition;

    public string Name => Definition.Name;

    public string Category => Definition.Category;

    public string? Description => Definition.Description;
}
