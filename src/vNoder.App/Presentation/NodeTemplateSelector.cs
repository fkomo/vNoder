using System.Windows;
using System.Windows.Controls;
using vNoder.App.ViewModels;
using vNoder.Nodes;

namespace vNoder.App.Presentation;

/// <summary>
/// Picks the whole visual for a node: the ordinary node shell, or the note shell for a
/// <see cref="NoteNode"/>, which has no header or ports and is drawn like a linked note.
/// </summary>
public sealed class NodeTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Node { get; set; }

    public DataTemplate? Note { get; set; }

    public override DataTemplate? SelectTemplate(object? item, DependencyObject container) =>
        item is NodeViewModel { Model: NoteNode } ? Note : Node;
}
