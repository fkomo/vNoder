using System.Windows;
using System.Windows.Controls;

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

        return element.TryFindResource(new DataTemplateKey(item.GetType())) as DataTemplate ?? Empty;
    }
}
