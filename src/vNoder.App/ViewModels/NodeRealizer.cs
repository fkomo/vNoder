using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Threading;

namespace vNoder.App.ViewModels;

/// <summary>
/// Virtualizes the node layer: <see cref="Realized"/> holds only the nodes near the viewport, and the
/// node <c>ItemsControl</c> is bound to it rather than to every node, so a node far off screen has no
/// visual tree at all — no templates, no bindings, no layout.
/// </summary>
/// <remarks>
/// <para>
/// Only the visuals are virtual. Every node keeps its view-model, and wire anchors come from
/// <see cref="NodeMetrics"/> rather than from visuals, so wires, groups, selection and the model are
/// all unaffected by whether a node is realized.
/// </para>
/// <para>
/// A node is realized once it comes within <see cref="EnterMargin"/> screen pixels of the viewport and
/// released only once it is more than <see cref="LeaveMargin"/> away. The gap between the two keeps a
/// node at the edge from being created and destroyed on every pixel of a pan, and the lead means a
/// node is already there by the time it scrolls into sight. Updates are coalesced to one per frame.
/// </para>
/// <para>
/// Building a node's visual is the expensive part (a template with text boxes), and on a regular
/// layout a whole column of nodes crosses the margin in the same frame, which showed up as a hitch
/// mid-pan. So only nodes actually on screen are realized unconditionally; nodes that are merely in
/// the margin are realized at most <see cref="MarginBudget"/> per update, the rest in follow-up
/// updates queued at background priority, behind input and rendering.
/// </para>
/// </remarks>
public sealed class NodeRealizer
{
    private const double EnterMargin = 200;
    private const double LeaveMargin = 600;

    /// <summary>How many off-screen nodes may be realized ahead of need in one update.</summary>
    private const int MarginBudget = 3;

    private readonly ObservableCollection<NodeViewModel> _all;
    private readonly Func<Rect> _viewport;
    private readonly Func<double> _scale;
    private readonly Dispatcher _dispatcher;
    private readonly HashSet<NodeViewModel> _realized = [];
    private bool _scheduled;
    private bool _catchUpScheduled;
    private NodeViewModel? _pinned;

    public NodeRealizer(
        ObservableCollection<NodeViewModel> all,
        Func<Rect> viewport,
        Func<double> scale,
        Dispatcher dispatcher)
    {
        _all = all;
        _viewport = viewport;
        _scale = scale;
        _dispatcher = dispatcher;
        _all.CollectionChanged += OnAllChanged;
    }

    /// <summary>The nodes that currently have visuals, in the same relative order as all nodes.</summary>
    public ObservableCollection<NodeViewModel> Realized { get; } = [];

    /// <summary>
    /// A node kept realized wherever it is: the one holding keyboard focus. Releasing it would destroy
    /// the text box the user is typing into.
    /// </summary>
    public NodeViewModel? Pinned
    {
        get => _pinned;
        set
        {
            if (ReferenceEquals(_pinned, value))
                return;

            _pinned = value;
            Invalidate();
        }
    }

    /// <summary>Schedules an update for the next frame; any number of calls before then cost one.</summary>
    public void Invalidate()
    {
        if (_scheduled)
            return;

        _scheduled = true;
        _dispatcher.BeginInvoke(Update, DispatcherPriority.Render);
    }

    /// <summary>Brings <see cref="Realized"/> up to date now, within the margin budget.</summary>
    public void Update()
    {
        _scheduled = false;
        _catchUpScheduled = false;

        var viewport = _viewport();
        if (viewport.IsEmpty || viewport.Width <= 0 || viewport.Height <= 0)
            return;

        var scale = Math.Max(_scale(), 0.01);
        var enter = Inflate(viewport, EnterMargin / scale);
        var keep = Inflate(viewport, LeaveMargin / scale);

        var wanted = new List<NodeViewModel>();
        var budget = MarginBudget;
        var deferred = false;

        foreach (var node in _all)
        {
            bool want;
            if (ReferenceEquals(node, _pinned) || _realized.Contains(node))
                want = ReferenceEquals(node, _pinned) || (node.IsVisible && keep.IntersectsWith(node.Bounds));
            else if (!node.IsVisible)
                want = false; // Inside a collapsed group: nothing to draw.
            else if (viewport.IntersectsWith(node.Bounds))
                want = true; // On screen: never deferred.
            else if (enter.IntersectsWith(node.Bounds))
            {
                want = budget > 0;
                budget--;
                deferred |= !want;
            }
            else
                want = false;

            if (want)
                wanted.Add(node);
        }

        Apply(wanted);

        if (deferred && !_catchUpScheduled)
        {
            _catchUpScheduled = true;
            _dispatcher.BeginInvoke(CatchUp, DispatcherPriority.Background);
        }
    }

    /// <summary>
    /// Schedules an update at background priority, for changes that only might move a node across
    /// the margin: a node being measured, or dragged. Using <see cref="Invalidate"/> here would let
    /// each realized node's first measurement trigger another update within the same frame, each
    /// with a fresh budget, which cascades through a whole column of nodes and defeats the budget.
    /// </summary>
    public void InvalidateLater()
    {
        if (_scheduled || _catchUpScheduled)
            return;

        _catchUpScheduled = true;
        _dispatcher.BeginInvoke(CatchUp, DispatcherPriority.Background);
    }

    private void CatchUp()
    {
        // A regular update may already have run since this was queued, and cleared the flag.
        if (_catchUpScheduled)
            Update();
    }

    private static Rect Inflate(Rect rect, double by)
    {
        rect.Inflate(by, by);
        return rect;
    }

    /// <summary>
    /// Makes <see cref="Realized"/> equal <paramref name="wanted"/> with as few collection changes as
    /// possible. Nodes that stay keep their containers; order follows <paramref name="wanted"/>, which
    /// is the order of all nodes and therefore the z-order.
    /// </summary>
    private void Apply(List<NodeViewModel> wanted)
    {
        var wantedSet = wanted.ToHashSet();

        for (var i = Realized.Count - 1; i >= 0; i--)
        {
            if (!wantedSet.Contains(Realized[i]))
            {
                _realized.Remove(Realized[i]);
                Realized.RemoveAt(i);
            }
        }

        for (var i = 0; i < wanted.Count; i++)
        {
            var node = wanted[i];
            if (i < Realized.Count && ReferenceEquals(Realized[i], node))
                continue;

            if (_realized.Contains(node))
                Realized.Move(Realized.IndexOf(node), i);
            else
            {
                Realized.Insert(i, node);
                _realized.Add(node);
            }
        }
    }

    private void OnAllChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // A removed node must not linger for a frame, so it goes at once; additions wait for the
        // next update, which also decides whether they are anywhere near the viewport.
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            _realized.Clear();
            Realized.Clear();
        }
        else if (e.Action is NotifyCollectionChangedAction.Remove or NotifyCollectionChangedAction.Replace
                 && e.OldItems is not null)
        {
            foreach (NodeViewModel node in e.OldItems)
            {
                if (_realized.Remove(node))
                    Realized.Remove(node);

                if (ReferenceEquals(node, _pinned))
                    _pinned = null;
            }
        }

        Invalidate();
    }
}
