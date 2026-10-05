using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using vNoder.Core;

namespace vNoder.App.ViewModels;

public sealed partial class NodeViewModel : ObservableObject
{
    public NodeViewModel(NodeBase model)
    {
        // A node that cannot be resized has no business carrying a size, which it can only have from a
        // file saved while its type was still resizable (the file nodes were). Honouring it would pin
        // the editor to a height it no longer needs, with no grip to undo it.
        if (!model.IsResizable && (model.Size.HasWidth || model.Size.HasHeight))
            model.Size = GraphSize.Auto;

        Model = model;
        _x = model.Position.X;
        _y = model.Position.Y;
        _width = WidthOf(model);
        _contentHeight = ContentHeightOf(model);

        Inputs = [.. model.Inputs.Select(p => new PortViewModel(this, p))];
        Outputs = [.. model.Outputs.Select(p => new PortViewModel(this, p))];

        var rowCount = Math.Max(Inputs.Count, Outputs.Count);
        Rows = [.. Enumerable.Range(0, rowCount).Select(i => new NodeRowViewModel(
            i < Inputs.Count ? Inputs[i] : null,
            i < Outputs.Count ? Outputs[i] : null))];

        MeasuredHeight = EstimateHeight();
    }

    private static double WidthOf(NodeBase model) =>
        model.Size.HasWidth ? model.Size.Width : NodeMetrics.Width;

    /// <summary>
    /// NaN is WPF's "Auto". Resizable nodes instead start at a fixed height, so that a long value
    /// arriving at a display node does not resize the node under the user.
    /// </summary>
    private static double ContentHeightOf(NodeBase model) =>
        model.Size.HasHeight ? model.Size.Height
        : model.IsResizable ? NodeMetrics.DefaultContentHeight
        : double.NaN;

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

    /// <summary>True while a Run Once is evaluating this node; see <see cref="RunFeedback"/>.</summary>
    [ObservableProperty]
    private bool _isRunning;

    /// <summary>Set to play the node's completion flash; the node template animates on the rising edge.</summary>
    [ObservableProperty]
    private bool _isFlashing;

    /// <summary>Whether the current flash reports a failure, which colours it red.</summary>
    [ObservableProperty]
    private bool _flashFailed;

    /// <summary>Plays the completion flash, restarting it if one is already showing.</summary>
    public void Flash(bool failed)
    {
        FlashFailed = failed;
        IsFlashing = false;
        IsFlashing = true;
    }

    public void EndFlash() => IsFlashing = false;

    /// <summary>False while the node is inside a collapsed group.</summary>
    [ObservableProperty]
    private bool _isVisible = true;

    /// <summary>The cursor is over the node. Set by the view, so the node's linked notes can light up.</summary>
    [ObservableProperty]
    private bool _isHovered;

    /// <summary>The cursor is over one of this node's linked notes; the node lights up in return.</summary>
    [ObservableProperty]
    private bool _isNoteHovered;

    public string? Error => Model.LastError;
    public bool HasError => Model.LastError is not null;

    /// <summary>The last pass skipped this node (Run If, or a failed or skipped input); drawn dimmed.</summary>
    public bool IsSkipped => Model.IsSkipped;

    /// <summary>
    /// Header badge for a node that runs once per list item: "×" plus the item count of the last run,
    /// or just "×" before it has run that way. Null for an ordinary node.
    /// </summary>
    public string? ItemBadge => Model.RunsPerItem
        ? Model.ItemCount is { } count ? $"×{count}" : "×"
        : null;

    /// <summary>Items a per-item node skipped because they failed (<see cref="NodeBase.ItemWarning"/>); shown in amber.</summary>
    public string? ItemWarning => Model.ItemWarning;

    public bool HasItemWarning => Model.ItemWarning is not null;

    /// <summary>
    /// Rendered height, reported by the view. Node bodies vary with their inline editor, so this
    /// cannot be derived from <see cref="NodeMetrics"/> the way port anchors can; groups need it to
    /// size their box.
    /// </summary>
    /// <remarks>
    /// Until the node has been realized and measured this is an estimate, since a node far outside
    /// the viewport has no visual to measure (see <see cref="NodeRealizer"/>). The estimate only has
    /// to be good enough to decide when the node comes into view; the real height replaces it then.
    /// </remarks>
    public double MeasuredHeight { get; private set; }

    public bool HasMeasuredHeight { get; private set; }

    /// <summary>The node's box in graph space, for culling and marquee selection.</summary>
    public Rect Bounds => new(X, Y, Width, MeasuredHeight);

    private double EstimateHeight() =>
        (2 * NodeMetrics.BorderThickness) + NodeMetrics.HeaderHeight + NodeMetrics.PortsTopPadding
        + (Rows.Count * NodeMetrics.PortRowHeight)
        + (double.IsNaN(ContentHeight) ? NodeMetrics.EstimatedEditorHeight : ContentHeight)
        + NodeMetrics.EditorVerticalMargin;

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
        if (!_syncing)
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

        if (!_syncing)
            Model.Size = new GraphSize(Model.Size.Width, double.IsNaN(value) ? 0 : value);

        if (!HasMeasuredHeight)
            MeasuredHeight = EstimateHeight();

        OnGeometryChanged();
    }

    private bool _movingBothAxes;

    /// <summary>
    /// Set while the view-model is being pulled from its model, so the model is not written back.
    /// An unresized node's model has no size at all, and writing the view's default into it would
    /// make the next snapshot differ from the one just restored.
    /// </summary>
    private bool _syncing;

    /// <summary>
    /// Re-reads position and size from the model, after something other than this view-model changed
    /// them — an undo, which restores the model in place.
    /// </summary>
    public void SyncFromModel()
    {
        _syncing = true;
        try
        {
            MoveTo(Model.Position.X, Model.Position.Y);
            Width = WidthOf(Model);
            ContentHeight = ContentHeightOf(Model);
        }
        finally
        {
            _syncing = false;
        }
    }

    private void OnGeometryChanged()
    {
        if (!_syncing)
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
        var wasEstimate = !HasMeasuredHeight;
        HasMeasuredHeight = true;

        if (!wasEstimate && Math.Abs(MeasuredHeight - height) < 0.5)
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
        OnPropertyChanged(nameof(IsSkipped));
        OnPropertyChanged(nameof(ItemBadge));
        OnPropertyChanged(nameof(ItemWarning));
        OnPropertyChanged(nameof(HasItemWarning));
    }

    /// <summary>
    /// Re-reads what the ports carry. A node's outputs become lists when one of its links starts
    /// iterating, which no wire touching the node itself may have announced.
    /// </summary>
    public void RefreshPortTypes()
    {
        foreach (var port in Inputs.Concat(Outputs))
            port.RaiseTypeChanged();

        OnPropertyChanged(nameof(ItemBadge));
    }

    public void RefreshPortConnections()
    {
        foreach (var port in Inputs.Concat(Outputs))
            port.RaiseConnectionChanged();

        // Inline editors are bound to the model's ports, so they need the model to notify as well.
        Model.RaiseBindingsChanged();
    }
}
