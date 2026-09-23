using CommunityToolkit.Mvvm.ComponentModel;
using vNoder.Core;

namespace vNoder.App.ViewModels;

public sealed partial class NodeViewModel : ObservableObject
{
    public NodeViewModel(NodeBase model)
    {
        Model = model;
        _x = model.Position.X;
        _y = model.Position.Y;
        _width = model.Size.HasWidth ? model.Size.Width : NodeMetrics.Width;

        // NaN is WPF's "Auto". Resizable nodes instead start at a fixed height, so that a long value
        // arriving at a display node does not resize the node under the user.
        _contentHeight = model.Size.HasHeight ? model.Size.Height
            : model.IsResizable ? NodeMetrics.DefaultContentHeight
            : double.NaN;

        Inputs = [.. model.Inputs.Select(p => new PortViewModel(this, p))];
        Outputs = [.. model.Outputs.Select(p => new PortViewModel(this, p))];

        var rowCount = Math.Max(Inputs.Count, Outputs.Count);
        Rows = [.. Enumerable.Range(0, rowCount).Select(i => new NodeRowViewModel(
            i < Inputs.Count ? Inputs[i] : null,
            i < Outputs.Count ? Outputs[i] : null))];
    }

    public NodeBase Model { get; }

    public IReadOnlyList<PortViewModel> Inputs { get; }
    public IReadOnlyList<PortViewModel> Outputs { get; }
    public IReadOnlyList<NodeRowViewModel> Rows { get; }

    public string Title => Model.Title;
    public string Category => Model.Category;

    /// <summary>Whether this node type offers a resize grip; see <see cref="NodeDefinitionAttribute"/>.</summary>
    public bool IsResizable => Model.IsResizable;

    /// <summary>
    /// The collapsed group hiding this node, if any. While set, the node's ports anchor to the group
    /// box instead of to the node, so wires crossing the boundary still land somewhere sensible.
    /// </summary>
    public NodeGroupViewModel? CollapsedHost { get; internal set; }

    [ObservableProperty]
    private double _x;

    [ObservableProperty]
    private double _y;

    [ObservableProperty]
    private double _width;

    /// <summary>Height of the inline editor area. <see cref="double.NaN"/> means size to content.</summary>
    [ObservableProperty]
    private double _contentHeight;

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>False while the node is inside a collapsed group.</summary>
    [ObservableProperty]
    private bool _isVisible = true;

    public string? Error => Model.LastError;
    public bool HasError => Model.LastError is not null;

    /// <summary>
    /// Rendered height, reported by the view. Node bodies vary with their inline editor, so this
    /// cannot be derived from <see cref="NodeMetrics"/> the way port anchors can; groups need it to
    /// size their box.
    /// </summary>
    public double MeasuredHeight { get; private set; } = NodeMetrics.HeaderHeight;

    /// <summary>
    /// Smallest body height that still shows the whole inline editor. Reported by the view from the
    /// editor template's own <c>MinHeight</c>, because only the template knows what it contains.
    /// </summary>
    public double MinContentHeight { get; private set; } = NodeMetrics.MinContentHeight;

    /// <summary>True once the view has measured the editor, so the walk is done only once.</summary>
    public bool HasMeasuredMinContentHeight { get; private set; }

    public void SetMinContentHeight(double value)
    {
        HasMeasuredMinContentHeight = true;

        if (value <= 0)
            return;

        MinContentHeight = Math.Max(value, NodeMetrics.MinContentHeight);

        // A height loaded from a file, or set before the editor was measured, may be too small.
        if (!double.IsNaN(ContentHeight) && ContentHeight < MinContentHeight)
            ContentHeight = MinContentHeight;

        // Width has a floor too, and a file could carry a narrower one.
        if (Width < NodeMetrics.MinWidth)
            Width = NodeMetrics.MinWidth;
    }

    /// <summary>Raised whenever this node's position or size changes, so wires and groups can follow.</summary>
    public event EventHandler? GeometryChanged;

    partial void OnXChanged(double value) => OnGeometryChanged();

    partial void OnYChanged(double value) => OnGeometryChanged();

    partial void OnWidthChanged(double value)
    {
        Model.Size = new GraphSize(value, Model.Size.Height);
        OnGeometryChanged();
    }

    partial void OnContentHeightChanged(double value)
    {
        // The floor belongs to the property, not to the resize gesture: any caller that shrinks a node
        // past its editor would otherwise cut the editor in half.
        if (!double.IsNaN(value) && value < MinContentHeight)
        {
            ContentHeight = MinContentHeight;
            return;
        }

        Model.Size = new GraphSize(Model.Size.Width, double.IsNaN(value) ? 0 : value);
        OnGeometryChanged();
    }

    private bool _movingBothAxes;

    private void OnGeometryChanged()
    {
        Model.Position = new GraphPoint(X, Y);

        if (_movingBothAxes)
            return;

        GeometryChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Moves the node, updating both coordinates before notifying so that wires are recomputed once
    /// against the final position rather than twice against an intermediate one.
    /// </summary>
    public void MoveTo(double x, double y)
    {
        if (X == x && Y == y)
            return;

        _movingBothAxes = true;
        try
        {
            X = x;
            Y = y;
        }
        finally
        {
            _movingBothAxes = false;
        }

        OnGeometryChanged();
    }

    public void SetMeasuredHeight(double height)
    {
        if (Math.Abs(MeasuredHeight - height) < 0.5)
            return;

        MeasuredHeight = height;
        GeometryChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Called on the UI thread once an evaluation pass has finished: pushes any model state the node
    /// displays into its bindings, and re-reads the error state.
    /// </summary>
    public void RefreshAfterEvaluation()
    {
        Model.RaiseBindingsChanged();
        OnPropertyChanged(nameof(Error));
        OnPropertyChanged(nameof(HasError));
    }

    public void RefreshPortConnections()
    {
        foreach (var port in Inputs.Concat(Outputs))
            port.RaiseConnectionChanged();

        // Inline editors are bound to the model's ports, so they need the model to notify as well.
        Model.RaiseBindingsChanged();
    }
}
