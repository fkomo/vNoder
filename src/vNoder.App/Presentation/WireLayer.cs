using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using vNoder.App.ViewModels;

namespace vNoder.App.Presentation;

/// <summary>
/// Draws every wire as a bare <see cref="DrawingVisual"/>, and draws only the ones near the viewport.
/// </summary>
/// <remarks>
/// <para>
/// This replaces an <c>ItemsControl</c> of <c>Path</c> elements. Each of those was a full element
/// with a container, a template, four bindings and its own measure and arrange; a
/// <see cref="DrawingVisual"/> has none of that, just render data. A wire that moves re-renders only
/// its own visual, and a wire outside the viewport renders nothing at all.
/// </para>
/// <para>
/// Culling is against the curve's control-point hull (<see cref="LinkViewModel.Bounds"/>), widened by
/// a margin, and is re-run on every pan or zoom. That is a rectangle test per wire, which is far
/// cheaper than drawing them; only wires that cross the edge are re-rendered.
/// </para>
/// <para>
/// The layer is not hit-testable, so a click on a wire falls through to the canvas. It must sit on a
/// <c>Canvas</c> like everything else in scene space, and it measures to nothing, so it can never be
/// given a layout clip.
/// </para>
/// </remarks>
public sealed class WireLayer : FrameworkElement
{
    /// <summary>Screen pixels beyond the viewport within which wires are drawn anyway.</summary>
    private const double CullMargin = 200;

    public static readonly DependencyProperty LinksProperty = DependencyProperty.Register(
        nameof(Links),
        typeof(ObservableCollection<LinkViewModel>),
        typeof(WireLayer),
        new PropertyMetadata(null, (d, e) => ((WireLayer)d).OnLinksChanged(
            (ObservableCollection<LinkViewModel>?)e.OldValue,
            (ObservableCollection<LinkViewModel>?)e.NewValue)));

    public static readonly DependencyProperty ScaleProperty = DependencyProperty.Register(
        nameof(Scale),
        typeof(double),
        typeof(WireLayer),
        new PropertyMetadata(1.0, (d, _) => ((WireLayer)d).OnScaleChanged()));

    /// <summary>The visible part of the scene, in graph space. Empty means unknown: draw everything.</summary>
    public static readonly DependencyProperty ViewportProperty = DependencyProperty.Register(
        nameof(Viewport),
        typeof(Rect),
        typeof(WireLayer),
        new PropertyMetadata(Rect.Empty, (d, _) => ((WireLayer)d).RedrawAll(force: false)));

    private readonly VisualCollection _visuals;
    private readonly Dictionary<LinkViewModel, WireVisual> _byLink = [];
    private readonly Dictionary<Brush, Pen> _pens = [];

    public WireLayer()
    {
        _visuals = new VisualCollection(this);
        IsHitTestVisible = false;
    }

    public ObservableCollection<LinkViewModel>? Links
    {
        get => (ObservableCollection<LinkViewModel>?)GetValue(LinksProperty);
        set => SetValue(LinksProperty, value);
    }

    public double Scale
    {
        get => (double)GetValue(ScaleProperty);
        set => SetValue(ScaleProperty, value);
    }

    public Rect Viewport
    {
        get => (Rect)GetValue(ViewportProperty);
        set => SetValue(ViewportProperty, value);
    }

    /// <summary>How many wires currently have render data. For diagnostics and tests.</summary>
    public int DrawnCount => _byLink.Values.Count(v => v.IsDrawn);

    protected override int VisualChildrenCount => _visuals.Count;

    protected override Visual GetVisualChild(int index) => _visuals[index];

    protected override Size MeasureOverride(Size availableSize) => default;

    // ---- Tracking the link collection ---------------------------------------------------------

    private void OnLinksChanged(
        ObservableCollection<LinkViewModel>? oldLinks,
        ObservableCollection<LinkViewModel>? newLinks)
    {
        if (oldLinks is not null)
            oldLinks.CollectionChanged -= OnCollectionChanged;

        RemoveAll();

        if (newLinks is null)
            return;

        newLinks.CollectionChanged += OnCollectionChanged;
        foreach (var link in newLinks)
            Add(link);
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Move:
                return;

            case NotifyCollectionChangedAction.Reset:
                RemoveAll();
                foreach (var link in Links ?? [])
                    Add(link);
                return;
        }

        foreach (LinkViewModel link in e.OldItems ?? Array.Empty<LinkViewModel>())
            Remove(link);

        foreach (LinkViewModel link in e.NewItems ?? Array.Empty<LinkViewModel>())
            Add(link);
    }

    private void Add(LinkViewModel link)
    {
        if (_byLink.ContainsKey(link))
            return;

        var visual = new WireVisual(link);
        _byLink[link] = visual;
        _visuals.Add(visual);
        link.PropertyChanged += OnLinkPropertyChanged;
        Draw(visual, CullRect(), force: true);
    }

    private void Remove(LinkViewModel link)
    {
        if (!_byLink.Remove(link, out var visual))
            return;

        link.PropertyChanged -= OnLinkPropertyChanged;
        _visuals.Remove(visual);
    }

    private void RemoveAll()
    {
        foreach (var link in _byLink.Keys)
            link.PropertyChanged -= OnLinkPropertyChanged;

        _byLink.Clear();
        _visuals.Clear();
    }

    private void OnLinkPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is LinkViewModel link
            && e.PropertyName is nameof(LinkViewModel.Geometry) or nameof(LinkViewModel.IsVisible)
            && _byLink.TryGetValue(link, out var visual))
        {
            Draw(visual, CullRect(), force: true);
        }
    }

    // ---- Drawing ------------------------------------------------------------------------------

    private void OnScaleChanged()
    {
        // Thickness compensates for zoom, so every pen is stale.
        _pens.Clear();
        RedrawAll(force: true);
    }

    private void RedrawAll(bool force)
    {
        var cull = CullRect();
        foreach (var visual in _byLink.Values)
            Draw(visual, cull, force);
    }

    private Rect CullRect()
    {
        var viewport = Viewport;
        if (viewport.IsEmpty)
            return Rect.Empty;

        var margin = CullMargin / Math.Max(Scale, 0.01);
        viewport.Inflate(margin, margin);
        return viewport;
    }

    /// <summary>
    /// Renders one wire if it should be visible, or clears it if not. Without <paramref name="force"/>
    /// a wire already drawn is left alone, which is what makes a pan cheap: only wires crossing the
    /// edge of the viewport do any work.
    /// </summary>
    private void Draw(WireVisual visual, Rect cull, bool force)
    {
        var link = visual.Link;
        var wanted = link.IsVisible && (cull.IsEmpty || cull.IntersectsWith(link.Bounds));

        if (!wanted)
        {
            if (visual.IsDrawn)
            {
                visual.RenderOpen().Close();
                visual.IsDrawn = false;
            }

            return;
        }

        if (visual.IsDrawn && !force)
            return;

        using (var context = visual.RenderOpen())
            context.DrawGeometry(null, PenFor(link), link.Geometry);

        visual.IsDrawn = true;
    }

    private Pen PenFor(LinkViewModel link)
    {
        var brush = PortBrushConverter.For(link.Source.ValueType);
        if (_pens.TryGetValue(brush, out var pen))
            return pen;

        pen = new Pen(brush, ScaleToStrokeThicknessConverter.For(Scale));
        pen.Freeze();
        _pens[brush] = pen;
        return pen;
    }

    private sealed class WireVisual(LinkViewModel link) : DrawingVisual
    {
        public LinkViewModel Link { get; } = link;

        public bool IsDrawn { get; set; }
    }
}
