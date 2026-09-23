using System.Windows;
using System.Windows.Controls;
using vNoder.Core;

namespace vNoder.App.Presentation;

/// <summary>
/// Picks the inline editor for a node from the implicit <see cref="DataTemplate"/> declared for its
/// model type, falling back to <see cref="Empty"/> for node types that have no editable state.
/// </summary>
/// <remarks>
/// A bare <c>ContentControl</c> would fall back to <c>ToString()</c> for node types without a
/// template, which is why the lookup is explicit here.
/// </remarks>
public sealed class NodeEditorTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Empty { get; set; }

    public override DataTemplate? SelectTemplate(object? item, DependencyObject container)
    {
        if (item is null || container is not FrameworkElement element)
            return Empty;

        // Walk up the node's base types, so a family of nodes sharing inline state on a common base
        // (the trig nodes' Degrees) shares one template declared for that base.
        for (var type = item.GetType(); type is not null && type != typeof(NodeBase); type = type.BaseType)
        {
            if (element.TryFindResource(new DataTemplateKey(type)) is DataTemplate template)
                return template;
        }

        return Empty;
    }
}
