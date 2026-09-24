using System.Globalization;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using vNoder.Core;

namespace vNoder.App.ViewModels;

/// <summary>
/// A named box drawn behind a set of nodes. Purely visual: it never touches links or evaluation, it
/// only moves its members together and can hide them when collapsed.
/// </summary>
public sealed partial class NodeGroupViewModel : ObservableObject
{
    private readonly List<NodeViewModel> _members;

    public NodeGroupViewModel(NodeGroup model, IEnumerable<NodeViewModel> members)
    {
        Model = model;
        _members = [.. members];
        _name = model.Name;
        _isCollapsed = model.IsCollapsed;

        foreach (var member in _members)
            member.GeometryChanged += OnMemberGeometryChanged;

        Refresh();
        ApplyCollapsedState();
    }

    public NodeGroup Model { get; }

    public IReadOnlyList<NodeViewModel> Members => _members;

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private bool _isCollapsed;

    [ObservableProperty]
    private double _x;

    [ObservableProperty]
    private double _y;

    [ObservableProperty]
    private double _width;

    [ObservableProperty]
    private double _height;

    /// <summary>True while the cursor is inside this box. Set by the workspace, because the box is
    /// deliberately not hit-testable and so cannot use <c>IsMouseOver</c>.</summary>
    [ObservableProperty]
    private bool _isHovered;

    /// <summary>Raised when collapsing changes member visibility, so wires can be re-evaluated.</summary>
    public event EventHandler? VisibilityChanged;

    /// <summary>
    /// Raised when a collapsed box changes size without any member moving (a rename), since the wires
    /// crossing into it anchor to its edge.
    /// </summary>
    public event EventHandler? AnchorsMoved;

    partial void OnNameChanged(string value)
    {
        Model.Name = value;

        // A collapsed box is sized to its title, so a rename can change its width, and with it where
        // the wires crossing into the box meet it.
        var width = Width;
        Refresh();
        if (IsCollapsed && Width != width)
            AnchorsMoved?.Invoke(this, EventArgs.Empty);
    }

    partial void OnIsCollapsedChanged(bool value)
    {
        Model.IsCollapsed = value;

        Refresh();
        ApplyCollapsedState();
        VisibilityChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Hides or shows members and points them at this box for anchoring. Bounds are refreshed first,
    /// so a port that re-anchors here reads the box it will actually be drawn against.
    /// </summary>
    private void ApplyCollapsedState()
    {
        foreach (var member in _members)
        {
            member.IsVisible = !IsCollapsed;
            member.CollapsedHost = IsCollapsed ? this : null;
        }
    }

    private void OnMemberGeometryChanged(object? sender, EventArgs e) => Refresh();

    /// <summary>
    /// Recomputes the box from its members. Node heights come from what the view measured, since a
    /// node body's height depends on its inline editor and cannot be derived from
    /// <see cref="NodeMetrics"/>.
    /// </summary>
    public void Refresh()
    {
        if (_members.Count == 0)
            return;

        var left = _members.Min(m => m.X);
        var top = _members.Min(m => m.Y);

        X = left - NodeMetrics.GroupPadding;
        Y = top - NodeMetrics.GroupPadding - NodeMetrics.GroupHeaderHeight;

        if (IsCollapsed)
        {
            // Collapsed, the box is just its header: the members are hidden.
            Width = CollapsedWidth();
            Height = NodeMetrics.GroupHeaderHeight;
            return;
        }

        var right = _members.Max(m => m.X + m.Width);
        var bottom = _members.Max(m => m.Y + m.MeasuredHeight);

        Width = right + NodeMetrics.GroupPadding - X;
        Height = bottom + NodeMetrics.GroupPadding - Y;
    }

    /// <summary>
    /// Wide enough for the whole header, name and member count, and never narrower than
    /// <see cref="NodeMetrics.GroupCollapsedWidth"/>. Measured here rather than left to the view,
    /// because wires crossing into a collapsed group anchor to this box's edge, so its width is
    /// geometry, not just presentation.
    /// </summary>
    private double CollapsedWidth()
    {
        // Must match the header in the GroupTemplate: 12pt SemiBold name, 11pt count 8px after it,
        // and 10px margins either side.
        var content = MeasureText(Name, 12, FontWeights.SemiBold) + 8 + MeasureText(MemberCountLabel, 11, FontWeights.Normal);
        return Math.Max(NodeMetrics.GroupCollapsedWidth, Math.Ceiling(content + (2 * 10) + 2));
    }

    private static double MeasureText(string text, double size, FontWeight weight) =>
        new FormattedText(
                text,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface(SystemFonts.MessageFontFamily, FontStyles.Normal, weight, FontStretches.Normal),
                size,
                Brushes.Black,
                null,
                TextFormattingMode.Display,
                1.0)
            .WidthIncludingTrailingWhitespace;

    /// <summary>Moves every member by the same delta; the box follows through <see cref="Refresh"/>.</summary>
    public void MoveBy(double dx, double dy)
    {
        foreach (var member in _members)
            member.MoveTo(member.X + dx, member.Y + dy);
    }

    /// <summary>Shown beside the name while collapsed, so a folded group still says how big it is.</summary>
    public string MemberCountLabel => _members.Count == 1 ? "1 node" : $"{_members.Count} nodes";

    /// <summary>The box in graph space, for hit-testing what the cursor is over.</summary>
    public Rect Bounds => new(X, Y, Width, Height);

    /// <summary>Captures member positions so a drag can be applied against a fixed origin.</summary>
    public IReadOnlyList<(NodeViewModel Node, double X, double Y)> CaptureMemberOrigins() =>
        [.. _members.Select(m => (m, m.X, m.Y))];

    public void Detach()
    {
        foreach (var member in _members)
        {
            member.GeometryChanged -= OnMemberGeometryChanged;
            member.IsVisible = true;
            member.CollapsedHost = null;
        }
    }
}
