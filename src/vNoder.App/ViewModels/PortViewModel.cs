using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using vNoder.Core;

namespace vNoder.App.ViewModels;

public sealed class PortViewModel : ObservableObject
{
    public PortViewModel(NodeViewModel node, Port model)
    {
        Node = node;
        Model = model;
        IsInput = model is InputPort;
    }

    public NodeViewModel Node { get; }
    public Port Model { get; }
    public bool IsInput { get; }

    public string Name => Model.Name;
    public Type ValueType => Model.ValueType;
    public string TypeLabel => PortTypeNames.For(Model.ValueType);

    public InputPort? AsInput => Model as InputPort;
    public OutputPort? AsOutput => Model as OutputPort;

    public bool IsConnected => Model switch
    {
        InputPort input => input.IsConnected,
        OutputPort output => output.Links.Count > 0,
        _ => false,
    };

    /// <summary>
    /// Anchor point in graph space, where wires meet this port. Output anchors track the node's own
    /// width so that resizing a node keeps its wires attached. While the node is hidden inside a
    /// collapsed group, the anchor moves to the edge of the group box, which is what lets a collapsed
    /// group still show the wires that cross its boundary.
    /// </summary>
    public Point Anchor
    {
        get
        {
            if (Node.CollapsedHost is { } host)
                return new Point(IsInput ? host.X : host.X + host.Width, host.Y + (host.Height / 2));

            return new Point(
                Node.X + (IsInput ? 0 : Node.Width),
                Node.Y + NodeMetrics.PortCentreY(Model.Index));
        }
    }

    /// <summary>
    /// True while this port is the one the cursor could act on: an output to start a wire from, or,
    /// mid-drag, a compatible input to drop on.
    /// </summary>
    public bool IsHighlighted
    {
        get;
        private set;
    }

    public void SetHighlighted(bool value)
    {
        if (IsHighlighted == value)
            return;

        IsHighlighted = value;
        OnPropertyChanged(nameof(IsHighlighted));
    }

    public void RaiseConnectionChanged() => OnPropertyChanged(nameof(IsConnected));
}

/// <summary>Short human labels for port types, used in tooltips.</summary>
public static class PortTypeNames
{
    public static string For(Type type) => type switch
    {
        _ when type == typeof(double) => "number",
        _ when type == typeof(string) => "text",
        _ when type == typeof(bool) => "boolean",
        _ when type == typeof(byte[]) => "bytes",
        _ when type == typeof(string[]) => "text list",
        _ when type == typeof(object) => "any",
        _ => type.Name,
    };
}
