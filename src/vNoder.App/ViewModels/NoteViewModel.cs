using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using vNoder.Core;

namespace vNoder.App.ViewModels;

/// <summary>
/// A note on the canvas: a translucent text box attached to one node or group, with a dotted leader
/// line to it. Its position is its target's corner plus the model's offset, so it follows the target
/// wherever it moves; the leader runs between the nearest edges of the two boxes.
/// </summary>
public sealed partial class NoteViewModel : ObservableObject
{
    private readonly NodeViewModel? _node;
    private readonly NodeGroupViewModel? _group;

    public NoteViewModel(GraphNote model, NodeViewModel node)
        : this(model)
    {
        _node = node;
        node.GeometryChanged += OnTargetGeometryChanged;
        node.PropertyChanged += OnNodePropertyChanged;
        Refresh();
    }

    public NoteViewModel(GraphNote model, NodeGroupViewModel group)
        : this(model)
    {
        _group = group;
        group.PropertyChanged += OnGroupPropertyChanged;
        Refresh();
    }

    private NoteViewModel(GraphNote model)
    {
        Model = model;
        _text = model.Text;
        _width = model.Width > 0 ? model.Width : NodeMetrics.NoteWidth;
        _height = model.Height > 0 ? model.Height : double.NaN;
    }

    public GraphNote Model { get; }

    [ObservableProperty]
    private double _width;

    /// <summary>
    /// The note's height. <see cref="double.NaN"/> (WPF's "Auto") until it is resized, so a new note
    /// grows with its text; after that it is fixed, and a longer text scrolls.
    /// </summary>
    [ObservableProperty]
    private double _height;

    /// <summary>Sizes the note, within <see cref="NodeMetrics.NoteMinWidth"/> and <see cref="NodeMetrics.NoteMinHeight"/>.</summary>
    public void Resize(double width, double height)
    {
        Width = Model.Width = Math.Max(NodeMetrics.NoteMinWidth, width);
        Height = Model.Height = Math.Max(NodeMetrics.NoteMinHeight, height);
        Refresh();
    }

    [ObservableProperty]
    private double _x;

    [ObservableProperty]
    private double _y;

    [ObservableProperty]
    private string _text;

    /// <summary>False while notes are hidden, or while the target is (inside a collapsed group).</summary>
    [ObservableProperty]
    private bool _isVisible;

    /// <summary>Whether the workspace is showing notes at all; set by the workspace.</summary>
    public bool IsShown
    {
        get => _isShown;
        set
        {
            _isShown = value;
            Refresh();
        }
    }

    private bool _isShown = true;

    /// <summary>The leader line to the target, in graph space. Empty while the two boxes overlap.</summary>
    [ObservableProperty]
    private Geometry _leader = Geometry.Empty;

    /// <summary>Set on a new note, so its text box takes focus as soon as it is drawn.</summary>
    public bool FocusOnLoad { get; set; }

    /// <summary>Raised when the text is edited, so the workspace can record it as an undo step.</summary>
    public event EventHandler? Edited;

    /// <summary>The cursor is over the note. Set by the view; lights up the note's target in return.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHighlighted))]
    private bool _isHovered;

    partial void OnIsHoveredChanged(bool value)
    {
        if (_node is not null)
            _node.IsNoteHovered = value;

        if (_group is not null)
            _group.IsNoteHovered = value;
    }

    /// <summary>
    /// Drawn brighter, with its leader: the note itself is hovered, or its target is hovered or
    /// (a node) selected, so it is plain which note belongs to what.
    /// </summary>
    public bool IsHighlighted =>
        IsHovered
        || _node is { IsHovered: true } or { IsSelected: true }
        || _group is { IsHovered: true };

    partial void OnTextChanged(string value)
    {
        Model.Text = value;
        Edited?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Rendered height, reported by the view; the leader needs it to meet the note's edge.</summary>
    private double _measuredHeight = 24;

    public double MeasuredHeight => _measuredHeight;

    public void SetMeasuredHeight(double height)
    {
        if (Math.Abs(_measuredHeight - height) < 0.5)
            return;

        _measuredHeight = height;
        Refresh();
    }

    /// <summary>The target's box in graph space.</summary>
    private Rect TargetBounds => _node?.Bounds ?? _group!.Bounds;

    private bool TargetVisible => _node?.IsVisible ?? true;

    /// <summary>Moves the note relative to its target; the offset is what is saved.</summary>
    public void MoveTo(double offsetX, double offsetY)
    {
        Model.OffsetX = offsetX;
        Model.OffsetY = offsetY;
        Refresh();
    }

    public void Refresh()
    {
        var target = TargetBounds;
        X = target.X + Model.OffsetX;
        Y = target.Y + Model.OffsetY;
        IsVisible = IsShown && TargetVisible;
        Leader = BuildLeader(target, new Rect(X, Y, Width, _measuredHeight));
    }

    /// <summary>
    /// A straight line between the two boxes, along the line joining their centres and clipped to
    /// each box's edge, so it reads as "this is about that" from any side.
    /// </summary>
    private static Geometry BuildLeader(Rect target, Rect note)
    {
        if (target.IsEmpty || target.IntersectsWith(note))
            return Geometry.Empty;

        var from = Centre(target);
        var to = Centre(note);

        var geometry = new LineGeometry(ClipToEdge(target, from, to), ClipToEdge(note, to, from));
        geometry.Freeze();
        return geometry;
    }

    private static Point Centre(Rect rect) => new(rect.X + (rect.Width / 2), rect.Y + (rect.Height / 2));

    /// <summary>Where the ray from <paramref name="centre"/> towards <paramref name="toward"/> leaves the box.</summary>
    private static Point ClipToEdge(Rect rect, Point centre, Point toward)
    {
        var dx = toward.X - centre.X;
        var dy = toward.Y - centre.Y;

        var tx = dx == 0 ? double.PositiveInfinity : rect.Width / 2 / Math.Abs(dx);
        var ty = dy == 0 ? double.PositiveInfinity : rect.Height / 2 / Math.Abs(dy);
        var t = Math.Min(Math.Min(tx, ty), 1);

        return new Point(centre.X + (dx * t), centre.Y + (dy * t));
    }

    private void OnTargetGeometryChanged(object? sender, EventArgs e) => Refresh();

    /// <summary>A node's moves arrive through <see cref="NodeViewModel.GeometryChanged"/>; this is only visibility.</summary>
    private void OnNodePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NodeViewModel.IsVisible))
            Refresh();
        else if (e.PropertyName is nameof(NodeViewModel.IsHovered) or nameof(NodeViewModel.IsSelected))
            OnPropertyChanged(nameof(IsHighlighted));
    }

    private void OnGroupPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(NodeGroupViewModel.X) or nameof(NodeGroupViewModel.Y)
            or nameof(NodeGroupViewModel.Width) or nameof(NodeGroupViewModel.Height))
        {
            Refresh();
        }
        else if (e.PropertyName == nameof(NodeGroupViewModel.IsHovered))
        {
            OnPropertyChanged(nameof(IsHighlighted));
        }
    }

    public void Detach()
    {
        // A note replaced while hovered (deleted from its own menu, or rebuilt by an undo) must not
        // leave its target lit.
        IsHovered = false;

        if (_node is not null)
        {
            _node.GeometryChanged -= OnTargetGeometryChanged;
            _node.PropertyChanged -= OnNodePropertyChanged;
        }

        if (_group is not null)
            _group.PropertyChanged -= OnGroupPropertyChanged;
    }
}
