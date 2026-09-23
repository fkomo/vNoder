using System.Windows;
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

    partial void OnNameChanged(string value) => Model.Name = value;

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
            Width = NodeMetrics.GroupCollapsedWidth;
            Height = NodeMetrics.GroupHeaderHeight;
            return;
        }

        var right = _members.Max(m => m.X + m.Width);
        var bottom = _members.Max(m => m.Y + m.MeasuredHeight);

        Width = right + NodeMetrics.GroupPadding - X;
        Height = bottom + NodeMetrics.GroupPadding - Y;
    }

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
