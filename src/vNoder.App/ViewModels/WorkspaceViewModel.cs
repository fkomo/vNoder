using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using vNoder.Core;
using vNoder.Nodes;

namespace vNoder.App.ViewModels;

public sealed partial class WorkspaceViewModel : ObservableObject
{
    private readonly Dispatcher _dispatcher;

    /// <summary>Wires touching a node, so a drag refreshes only what moved.</summary>
    private readonly Dictionary<Guid, List<LinkViewModel>> _linksByNode = [];

    /// <summary>Keeps the elapsed time moving between evaluations, which may be seconds apart.</summary>
    private readonly DispatcherTimer _runClock;

    private DateTime _runStartedUtc;
    private long _runIterations;

    public WorkspaceViewModel()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;

        _runClock = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(200),
        };
        _runClock.Tick += (_, _) => OnPropertyChanged(nameof(RunProgressText));

        Registry = new NodeRegistry().ScanAssemblyOf<OutputNode>();
        Graph = new Graph();
        Executor = new GraphExecutor(Graph);
        Executor.Evaluated += OnEvaluated;

        SearchResults = [.. Registry.Definitions];

        History = new GraphHistory(Graph, Registry);
        _savedState = History.Current;

        _editCommitTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = EditCommitDelay,
        };
        _editCommitTimer.Tick += (_, _) => FlushPendingEdit();

        Realizer = new NodeRealizer(Nodes, () => VisibleGraphRect, () => Scale, _dispatcher);
        _runFeedback = new RunFeedback(_dispatcher);
    }

    public NodeRegistry Registry { get; }
    public Graph Graph { get; }
    public GraphExecutor Executor { get; }

    public ObservableCollection<NodeViewModel> Nodes { get; } = [];

    /// <summary>The nodes that currently have visuals; the node layer is bound to this, not to <see cref="Nodes"/>.</summary>
    public ObservableCollection<NodeViewModel> RealizedNodes => Realizer.Realized;

    public NodeRealizer Realizer { get; }
    public ObservableCollection<LinkViewModel> Links { get; } = [];
    public ObservableCollection<NodeGroupViewModel> Groups { get; } = [];

    // ---- View transform -------------------------------------------------------------------

    [ObservableProperty]
    private double _scale = 1;

    [ObservableProperty]
    private double _offsetX;

    [ObservableProperty]
    private double _offsetY;

    public Point ScreenToGraph(Point screen) =>
        new((screen.X - OffsetX) / Scale, (screen.Y - OffsetY) / Scale);

    private Size _viewportSize;

    /// <summary>Size of the workspace on screen, reported by the view.</summary>
    public Size ViewportSize
    {
        get => _viewportSize;
        set
        {
            if (_viewportSize == value)
                return;

            _viewportSize = value;
            OnViewportChanged();
        }
    }

    /// <summary>The part of the graph currently on screen. Empty until the view has been laid out.</summary>
    public Rect VisibleGraphRect => ViewportSize is { Width: > 0, Height: > 0 } size
        ? new Rect(ScreenToGraph(new Point(0, 0)), ScreenToGraph(new Point(size.Width, size.Height)))
        : Rect.Empty;

    partial void OnScaleChanged(double value) => OnViewportChanged();

    partial void OnOffsetXChanged(double value) => OnViewportChanged();

    partial void OnOffsetYChanged(double value) => OnViewportChanged();

    private void OnViewportChanged()
    {
        OnPropertyChanged(nameof(VisibleGraphRect));
        Realizer?.Invalidate();
    }

    // ---- Status ---------------------------------------------------------------------------

    [ObservableProperty]
    private string _statusText = "Double-click the canvas to add a node.";

    // ---- Running --------------------------------------------------------------------------

    public bool IsRunningContinuously => Executor.IsRunningContinuously;

    public string ContinuousButtonText => IsRunningContinuously ? "Stop" : "Continuous";

    public long RunIterations => _runIterations;

    public TimeSpan RunElapsed => IsRunningContinuously ? DateTime.UtcNow - _runStartedUtc : TimeSpan.Zero;

    public string RunProgressText => IsRunningContinuously
        ? $"{_runIterations} iteration{(_runIterations == 1 ? string.Empty : "s")} in {RunElapsed.TotalSeconds:0.0}s"
        : string.Empty;

    /// <summary>
    /// Tick interval for continuous runs. Graph-level configuration rather than a node, so it saves
    /// with the document and there is nothing to wire up.
    /// </summary>
    public double IntervalMilliseconds
    {
        get => Graph.IntervalMilliseconds;
        set
        {
            if (Graph.IntervalMilliseconds == value)
                return;

            Graph.IntervalMilliseconds = value;
            OnPropertyChanged();
            NoteEdit("Change interval");
        }
    }

    /// <summary>Flashes nodes in evaluation order during a Run Once.</summary>
    private readonly RunFeedback _runFeedback;

    [RelayCommand]
    private async Task RunOnceAsync()
    {
        try
        {
            var result = await Executor.RunOnceAsync(_runFeedback.Begin(Nodes));
            StatusText = DescribeRun(result);
        }
        catch (Exception exception)
        {
            StatusText = $"Run failed: {exception.Message}";
        }
    }

    [RelayCommand]
    private async Task ToggleContinuousAsync()
    {
        if (Executor.IsRunningContinuously)
        {
            await Executor.StopContinuousAsync();
            _runClock.Stop();
            StatusText = $"Stopped after {_runIterations} iteration(s) in {RunElapsed.TotalSeconds:0.0}s.";
        }
        else
        {
            _runIterations = 0;
            _runStartedUtc = DateTime.UtcNow;
            Executor.StartContinuous();
            _runClock.Start();
            StatusText = "Running continuously.";
        }

        RaiseRunStateChanged();
    }

    private void OnEvaluated(object? sender, GraphRunResult result)
    {
        // Evaluation happens off the UI thread; everything the views bind to is refreshed here.
        _dispatcher.BeginInvoke(() =>
        {
            foreach (var node in Nodes)
                node.RefreshAfterEvaluation();

            if (Executor.IsRunningContinuously)
            {
                _runIterations++;
                OnPropertyChanged(nameof(RunIterations));
                OnPropertyChanged(nameof(RunProgressText));
            }

            StatusText = DescribeRun(result);
        });
    }

    private static string DescribeRun(GraphRunResult result)
    {
        var skipped = result.Skipped.Count > 0 ? $"; {result.Skipped.Count} skipped" : string.Empty;
        return result.HasErrors
            ? $"Evaluated {result.NodesEvaluated} node(s){skipped}; {result.Failed.Count} failed - {result.Failed[0].Title}: {result.Failed[0].LastError}"
            : $"Evaluated {result.NodesEvaluated} node(s){skipped}.";
    }

    private void RaiseRunStateChanged()
    {
        OnPropertyChanged(nameof(IsRunningContinuously));
        OnPropertyChanged(nameof(ContinuousButtonText));
        OnPropertyChanged(nameof(RunIterations));
        OnPropertyChanged(nameof(RunProgressText));
    }

    public Task StopAsync()
    {
        _runClock.Stop();
        return Executor.StopContinuousAsync();
    }

    // ---- Node lifecycle -------------------------------------------------------------------

    public NodeViewModel AddNode(NodeDefinition definition, Point graphPosition)
    {
        using var change = BeginChange($"Add {definition.Name}");

        var model = definition.CreateInstance();
        model.Position = new GraphPoint(graphPosition.X, graphPosition.Y);

        var viewModel = new NodeViewModel(model);
        Attach(viewModel);

        Graph.Add(model);
        Nodes.Add(viewModel);
        _linksByNode[model.Id] = [];

        SelectOnly(viewModel);
        RaiseRunStateChanged();
        StatusText = $"Added {definition.Path}.";
        return viewModel;
    }

    [RelayCommand]
    private void DeleteSelected()
    {
        var selected = Nodes.Where(n => n.IsSelected).ToList();
        if (selected.Count == 0)
            return;

        using var change = BeginChange(selected.Count == 1 ? $"Delete {selected[0].Title}" : $"Delete {selected.Count} nodes");

        foreach (var node in selected)
            RemoveNode(node);

        StatusText = $"Deleted {selected.Count} node(s).";
    }

    public void RemoveNode(NodeViewModel node)
    {
        using var change = BeginChange($"Delete {node.Title}");

        foreach (var link in Graph.Remove(node.Model))
            ForgetLink(link);

        Detach(node);
        Nodes.Remove(node);
        _linksByNode.Remove(node.Model.Id);

        // What ran per item because of this node's list may no longer.
        SettleLinks();

        // Graph.Remove already dropped it from its group and pruned groups left empty.
        SyncGroups();

        RaiseRunStateChanged();
        RaiseSelectionChanged();
    }

    public IReadOnlyList<NodeViewModel> SelectedNodes => [.. Nodes.Where(n => n.IsSelected)];

    public bool CanGroupSelection => Nodes.Count(n => n.IsSelected) >= 2;

    private void RaiseSelectionChanged() => OnPropertyChanged(nameof(CanGroupSelection));

    public void SelectOnly(NodeViewModel? node)
    {
        foreach (var candidate in Nodes)
            candidate.IsSelected = ReferenceEquals(candidate, node);

        RaiseSelectionChanged();
    }

    public void ToggleSelection(NodeViewModel node)
    {
        node.IsSelected = !node.IsSelected;
        RaiseSelectionChanged();
    }

    /// <summary>
    /// Used before a drag or a context menu: an unselected node becomes the selection, but a node
    /// that is already part of a multi-selection leaves that selection intact.
    /// </summary>
    public void EnsureSelected(NodeViewModel node)
    {
        if (!node.IsSelected)
            SelectOnly(node);
    }

    public void SelectNodes(IEnumerable<NodeViewModel> nodes)
    {
        var wanted = nodes.ToHashSet();

        foreach (var node in Nodes)
            node.IsSelected = wanted.Contains(node);

        RaiseSelectionChanged();
    }

    /// <summary>
    /// Marquee selection, recomputed from <paramref name="keep"/> (whatever was selected when the drag
    /// began) rather than accumulated, so it can be applied on every mouse-move to preview the result
    /// live. Hidden nodes, inside a collapsed group, are not selectable.
    /// </summary>
    public void SelectInRect(Rect rect, IReadOnlyCollection<NodeViewModel> keep)
    {
        foreach (var node in Nodes)
        {
            node.IsSelected = keep.Contains(node) || (node.IsVisible && rect.IntersectsWith(node.Bounds));
        }

        RaiseSelectionChanged();
    }

    private void OnNodeGeometryChanged(object? sender, EventArgs e)
    {
        // A restore refreshes everything once at the end, rather than per node as each one moves.
        if (sender is not NodeViewModel node || _restoring)
            return;

        Realizer.InvalidateLater();

        // The box must be up to date before wires are recomputed: a port inside a collapsed group
        // anchors to the box, so a stale box would leave those wires a frame behind.
        GroupContaining(node)?.Refresh();

        if (_linksByNode.TryGetValue(node.Model.Id, out var touching))
        {
            foreach (var link in touching)
                link.Refresh();
        }

        // Only a move or resize the user is making dirties the document, and shows as dirty while
        // the drag is still going. Geometry also changes when a node is first measured, which, with
        // virtualization, happens whenever panning brings one into view; that is not an edit.
        if (_changeDepth > 0)
            IsDirty = true;
    }

    private void Attach(NodeViewModel node)
    {
        node.GeometryChanged += OnNodeGeometryChanged;
        node.Model.PropertyChanged += OnNodeModelPropertyChanged;
    }

    private void Detach(NodeViewModel node)
    {
        node.GeometryChanged -= OnNodeGeometryChanged;
        node.Model.PropertyChanged -= OnNodeModelPropertyChanged;
    }

    private void OnNodeModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // RaiseBindingsChanged notifies with an empty name: that is evaluation output, not a user edit.
        if (!string.IsNullOrEmpty(e.PropertyName) && sender is NodeBase node)
            NoteEdit($"Edit {node.Title}");
    }

    // ---- Groups ---------------------------------------------------------------------------

    /// <summary>Default offered in the naming prompt when a group is created.</summary>
    public string SuggestedGroupName => $"Group {Graph.Groups.Count + 1}";

    /// <summary>Groups the current selection under <paramref name="name"/>.</summary>
    public void GroupSelected(string name)
    {
        var selected = SelectedNodes;
        if (selected.Count < 2)
        {
            StatusText = "Select two or more nodes to group them.";
            return;
        }

        using var change = BeginChange($"Group “{name}”");

        Graph.AddGroup(new NodeGroup
        {
            Name = name,
            NodeIds = [.. selected.Select(n => n.Model.Id)],
        });

        SyncGroups();
        StatusText = $"Grouped {selected.Count} nodes as “{name}”.";
    }

    public void RenameGroup(NodeGroupViewModel group, string name)
    {
        using var change = BeginChange("Rename group");

        group.Name = name;
        StatusText = $"Renamed group to “{name}”.";
    }

    [RelayCommand]
    private void Ungroup(NodeGroupViewModel? group)
    {
        if (group is null)
            return;

        using var change = BeginChange($"Ungroup “{group.Name}”");

        Graph.RemoveGroup(group.Model);
        SyncGroups();
        StatusText = "Ungrouped.";
    }

    public void ToggleCollapsed(NodeGroupViewModel group)
    {
        using var change = BeginChange(group.IsCollapsed ? $"Expand “{group.Name}”" : $"Collapse “{group.Name}”");

        group.IsCollapsed = !group.IsCollapsed;
    }

    public NodeGroupViewModel? GroupContaining(NodeViewModel node) =>
        Groups.FirstOrDefault(g => g.Members.Contains(node));

    /// <summary>The group whose box covers a graph point, topmost first. Used for highlighting.</summary>
    public NodeGroupViewModel? GroupAt(Point graphPoint) =>
        Groups.LastOrDefault(g => g.Bounds.Contains(graphPoint));

    /// <summary>
    /// Where a new node dropped at this point would land. Collapsed groups are excluded: a node put
    /// into one would vanish the moment it was created.
    /// </summary>
    public NodeGroupViewModel? DropGroupAt(Point graphPoint) =>
        Groups.LastOrDefault(g => !g.IsCollapsed && g.Bounds.Contains(graphPoint));

    /// <summary>Highlights whichever group the cursor is inside.</summary>
    public void UpdateHover(Point graphPoint)
    {
        var hit = GroupAt(graphPoint);

        foreach (var group in Groups)
            group.IsHovered = ReferenceEquals(group, hit);
    }

    public void ClearHover()
    {
        foreach (var group in Groups)
            group.IsHovered = false;

        HighlightPort(null, null);
    }

    /// <summary>Adds a freshly created node to an existing group.</summary>
    private void AddNodeToGroup(NodeGroupViewModel group, NodeViewModel node)
    {
        if (group.Model.NodeIds.Contains(node.Model.Id))
            return;

        group.Model.NodeIds.Add(node.Model.Id);
        SyncGroups();
    }

    /// <summary>Rebuilds the group view-models from <see cref="Graph"/>. Cheap; groups are few.</summary>
    private void SyncGroups()
    {
        foreach (var group in Groups)
        {
            group.VisibilityChanged -= OnGroupVisibilityChanged;
            group.AnchorsMoved -= OnGroupAnchorsMoved;
            group.Detach();
        }

        Groups.Clear();

        var byId = Nodes.ToDictionary(n => n.Model.Id);

        foreach (var model in Graph.Groups)
        {
            var members = model.NodeIds.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
            if (members.Count == 0)
                continue;

            var group = new NodeGroupViewModel(model, members);
            group.VisibilityChanged += OnGroupVisibilityChanged;
            group.AnchorsMoved += OnGroupAnchorsMoved;
            Groups.Add(group);
        }

        RefreshLinkVisibility();
    }

    private void OnGroupVisibilityChanged(object? sender, EventArgs e)
    {
        // Collapsing moves no node, but it does move every anchor in the group, so geometry has to be
        // recomputed as well as visibility.
        foreach (var link in Links)
        {
            link.Refresh();
            link.RaiseVisibilityChanged();
        }

        Realizer.Invalidate();
    }

    private void OnGroupAnchorsMoved(object? sender, EventArgs e)
    {
        if (sender is not NodeGroupViewModel group)
            return;

        // Only the links touching a member can anchor to this box.
        foreach (var member in group.Members)
        {
            if (_linksByNode.TryGetValue(member.Model.Id, out var touching))
            {
                foreach (var link in touching)
                    link.Refresh();
            }
        }
    }

    private void RefreshLinkVisibility()
    {
        foreach (var link in Links)
            link.RaiseVisibilityChanged();
    }

    // ---- Wiring ---------------------------------------------------------------------------

    public bool TryConnect(PortViewModel source, PortViewModel target)
    {
        if (source.AsOutput is not { } output || target.AsInput is not { } input)
            return false;

        using var change = BeginChange("Connect");

        if (!Graph.TryConnect(output, input, out var link, out var replaced, out var error))
        {
            StatusText = error ?? "Connection rejected.";
            return false;
        }

        if (replaced is not null)
            ForgetLink(replaced);

        var viewModel = new LinkViewModel(link!, source, target);
        Links.Add(viewModel);
        Track(viewModel, source.Node);
        Track(viewModel, target.Node);

        source.Node.RefreshPortConnections();
        target.Node.RefreshPortConnections();

        var how = link!.Iterate ? ", once per item" : string.Empty;
        StatusText = $"Connected {source.Node.Title}.{source.Name} to {target.Node.Title}.{target.Name}{how}."
                     + SettleLinks();
        return true;
    }

    /// <summary>Whether a wire from <paramref name="source"/> could be dropped on <paramref name="target"/>.</summary>
    public bool CanConnect(PortViewModel source, PortViewModel target) =>
        source.AsOutput is { } output
        && target.AsInput is { } input
        && Graph.Fit(output, input) != LinkFit.None;

    /// <summary>
    /// Whether the wire into <paramref name="input"/> could be switched to or from running per item,
    /// and if not, why. Null when there is no wire.
    /// </summary>
    public bool? CanToggleIterate(PortViewModel input, out string? reason)
    {
        reason = null;
        if (input.AsInput?.Link is not { } link)
            return null;

        return Graph.CanSetIterate(link, !link.Iterate, out reason);
    }

    /// <summary>
    /// Switches the wire into <paramref name="input"/> between passing its list whole and running the
    /// node once per item. The chain below follows: see <see cref="Graph.Settle"/>.
    /// </summary>
    public void ToggleIterate(PortViewModel input)
    {
        if (input.AsInput?.Link is not { } link)
            return;

        var iterate = !link.Iterate;
        using var change = BeginChange(iterate ? "For each item" : "Whole list");

        if (!Graph.SetIterate(link, iterate, out var reason))
        {
            StatusText = reason ?? "Cannot change that wire.";
            return;
        }

        StatusText = (iterate
            ? $"{input.Node.Title} runs once per item of {input.Name}."
            : $"{input.Node.Title} takes {input.Name} as a whole list.") + SettleLinks();
    }

    /// <summary>
    /// Brings every link in line with what its source now carries, after a change that may have made a
    /// node start or stop running per item, and refreshes port types and wire styles to match.
    /// Returns a note for the status bar about any link that had to go, or an empty string.
    /// </summary>
    private string SettleLinks()
    {
        var removed = Graph.Settle();
        foreach (var link in removed)
            ForgetLink(link);

        foreach (var node in Nodes)
            node.RefreshPortTypes();

        foreach (var link in Links)
            link.RefreshAppearance();

        return removed.Count switch
        {
            0 => string.Empty,
            1 => $" Removed {removed[0].Source.Owner.Title}.{removed[0].Source.Name} to "
                 + $"{removed[0].Target.Owner.Title}.{removed[0].Target.Name}, which no longer fits.",
            _ => $" Removed {removed.Count} links that no longer fit.",
        };
    }

    /// <summary>Clicking a connected input detaches it: the POC way to undo a wire.</summary>
    public void DisconnectInput(PortViewModel port)
    {
        if (port.AsInput?.Link is not { } link)
            return;

        using var change = BeginChange("Disconnect");

        Graph.Disconnect(link);
        ForgetLink(link);
        StatusText = "Disconnected." + SettleLinks();
    }

    private void Track(LinkViewModel link, NodeViewModel node)
    {
        if (!_linksByNode.TryGetValue(node.Model.Id, out var list))
            _linksByNode[node.Model.Id] = list = [];

        list.Add(link);
    }

    private void ForgetLink(Link link)
    {
        var viewModel = Links.FirstOrDefault(l => ReferenceEquals(l.Model, link));
        if (viewModel is null)
            return;

        Links.Remove(viewModel);

        foreach (var list in _linksByNode.Values)
            list.Remove(viewModel);

        viewModel.Source.Node.RefreshPortConnections();
        viewModel.Target.Node.RefreshPortConnections();
    }

    // ---- Port highlighting ------------------------------------------------------------------

    private PortViewModel? _highlightedPort;

    /// <summary>
    /// Highlights the port the cursor could act on: while dragging a wire that is a compatible input,
    /// and otherwise an output, since outputs are where a wire starts.
    /// </summary>
    public void HighlightPort(PortViewModel? hovered, PortViewModel? wiringSource)
    {
        var wanted = hovered switch
        {
            null => null,
            { IsInput: true } input when wiringSource is not null && CanConnect(wiringSource, input) => input,
            { IsInput: false } output when wiringSource is null => output,
            _ => null,
        };

        if (ReferenceEquals(_highlightedPort, wanted))
            return;

        _highlightedPort?.SetHighlighted(false);
        _highlightedPort = wanted;
        _highlightedPort?.SetHighlighted(true);
    }

    // ---- Clipboard ------------------------------------------------------------------------

    /// <summary>How far a pasted copy lands from the original, in graph units.</summary>
    private const double PasteOffset = 28;

    public void CopySelection()
    {
        var selected = SelectedNodes;
        if (selected.Count == 0)
        {
            StatusText = "Nothing selected to copy.";
            return;
        }

        // Only wiring internal to the selection travels; links to the outside are dropped.
        var document = GraphFile.CaptureSubset(Graph, [.. selected.Select(n => n.Model)]);

        try
        {
            Clipboard.SetText(GraphFile.Serialize(document));
        }
        catch (Exception exception)
        {
            // The clipboard is shared with every other process and can genuinely be unavailable.
            StatusText = $"Could not copy: {exception.Message}";
            return;
        }

        StatusText = $"Copied {document.Nodes.Count} node(s) and {document.Links.Count} link(s).";
    }

    public void Paste()
    {
        string json;
        try
        {
            json = Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty;
        }
        catch (Exception exception)
        {
            StatusText = $"Could not read the clipboard: {exception.Message}";
            return;
        }

        if (GraphFile.TryParse(json) is not { } document)
        {
            StatusText = "The clipboard does not hold nodes.";
            return;
        }

        using var change = BeginChange("Paste");

        var added = GraphRestore.Append(Graph, document, Registry, PasteOffset, PasteOffset);
        if (added.Count == 0)
        {
            StatusText = "Nothing in the clipboard could be pasted.";
            return;
        }

        RebuildFromGraph();
        SelectNodes(Nodes.Where(n => added.Contains(n.Model)));

        RaiseRunStateChanged();
        StatusText = $"Pasted {added.Count} node(s).";
    }

    // ---- Wire preview during a drag -------------------------------------------------------

    [ObservableProperty]
    private Geometry? _previewGeometry;

    public bool IsWiring => PreviewGeometry is not null;

    public void UpdatePreview(PortViewModel source, Point graphEnd)
    {
        PreviewGeometry = LinkViewModel.BuildCurve(source.Anchor, graphEnd);
        OnPropertyChanged(nameof(IsWiring));
    }

    public void ClearPreview()
    {
        PreviewGeometry = null;
        OnPropertyChanged(nameof(IsWiring));
    }

    // ---- Add-node search ------------------------------------------------------------------

    [ObservableProperty]
    private bool _isSearchOpen;

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private NodeDefinition? _selectedSearchResult;

    [ObservableProperty]
    private double _searchScreenX;

    [ObservableProperty]
    private double _searchScreenY;

    public ObservableCollection<NodeDefinition> SearchResults { get; }

    private Point _pendingNodePosition;

    /// <summary>
    /// Set when the catalog was opened by dropping a wire on empty canvas: whatever node the user
    /// picks gets wired to this output automatically.
    /// </summary>
    private PortViewModel? _pendingLinkSource;

    /// <summary>
    /// The group the catalog was opened inside, if any. Whatever node the user picks joins it, so
    /// double-clicking (or dropping a wire) inside a group box adds to that group.
    /// </summary>
    private NodeGroupViewModel? _pendingGroup;

    partial void OnSearchQueryChanged(string value) => RefreshSearchResults(value);

    /// <summary>
    /// Set while the catalog was opened by dropping a wire: the list then shows only node types with
    /// a port that could accept it, since picking anything else would produce an unconnected node.
    /// </summary>
    public string? SearchFilterLabel { get; private set; }

    public bool IsSearchFiltered => SearchFilterLabel is not null;

    private void RefreshSearchResults(string query)
    {
        var matches = Registry.Search(query);

        // Filtered to what can take the dropped wire, with Output first: looking at a value is the
        // commonest reason to drag a wire into empty space. OrderBy is stable, so the rest keep the
        // registry's order.
        if (_pendingLinkSource is { } source)
        {
            matches = matches
                .Where(d => d.AcceptsInput(source.ValueType))
                .OrderBy(d => d.NodeType == typeof(OutputNode) ? 0 : 1);
        }

        SearchResults.Clear();
        foreach (var definition in matches)
            SearchResults.Add(definition);

        SelectedSearchResult = SearchResults.FirstOrDefault();
    }

    /// <summary>
    /// Opens the node catalog at the cursor; the new node lands where the click was. When
    /// <paramref name="linkSource"/> is given, the chosen node is wired to it on commit.
    /// </summary>
    public void OpenSearch(Point screenPosition, PortViewModel? linkSource = null)
    {
        _pendingLinkSource = linkSource;
        _pendingNodePosition = ScreenToGraph(screenPosition);
        _pendingGroup = DropGroupAt(_pendingNodePosition);

        SearchFilterLabel = linkSource is null ? null : $"accepting {linkSource.TypeLabel}";
        OnPropertyChanged(nameof(SearchFilterLabel));
        OnPropertyChanged(nameof(IsSearchFiltered));
        SearchScreenX = screenPosition.X;
        SearchScreenY = screenPosition.Y;
        SearchQuery = string.Empty;
        RefreshSearchResults(string.Empty);
        IsSearchOpen = true;
    }

    public void CloseSearch()
    {
        _pendingLinkSource = null;
        _pendingGroup = null;
        ClearSearchFilter();
        IsSearchOpen = false;
    }

    private void ClearSearchFilter()
    {
        SearchFilterLabel = null;
        OnPropertyChanged(nameof(SearchFilterLabel));
        OnPropertyChanged(nameof(IsSearchFiltered));
    }

    public void CommitSearch()
    {
        var definition = SelectedSearchResult ?? SearchResults.FirstOrDefault();
        var linkSource = _pendingLinkSource;
        var group = _pendingGroup;

        _pendingLinkSource = null;
        _pendingGroup = null;
        ClearSearchFilter();
        IsSearchOpen = false;

        if (definition is null)
            return;

        // Adding, joining the group and auto-wiring are one step as far as undo is concerned.
        using var change = BeginChange($"Add {definition.Name}");

        var node = AddNode(definition, _pendingNodePosition);

        if (group is not null)
        {
            AddNodeToGroup(group, node);
            StatusText = $"Added {definition.Path} to “{group.Name}”.";
        }

        if (linkSource is null)
            return;

        // Wire the dropped output into the first input that accepts it. A Done wire means "after
        // that", so it goes to Run If rather than to the first boolean parameter it happens to fit.
        var target = (linkSource.Name == NodeBase.DonePortName
                         ? node.Inputs.FirstOrDefault(input => input.Name == NodeBase.RunIfPortName)
                         : null)
                     ?? node.Inputs.FirstOrDefault(input => CanConnect(linkSource, input));

        if (target is not null)
            TryConnect(linkSource, target);
        else
            StatusText = $"{node.Title} has no input that accepts {linkSource.TypeLabel}.";
    }

    public void MoveSearchSelection(int delta)
    {
        if (SearchResults.Count == 0)
            return;

        var index = SelectedSearchResult is null ? -1 : SearchResults.IndexOf(SelectedSearchResult);
        index = Math.Clamp(index + delta, 0, SearchResults.Count - 1);
        SelectedSearchResult = SearchResults[index];
    }

    // ---- Persistence ----------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    private bool _isDirty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    private string? _currentFilePath;

    public string WindowTitle =>
        $"vNoder - {(CurrentFilePath is null ? "untitled" : System.IO.Path.GetFileName(CurrentFilePath))}"
        + (IsDirty ? " *" : string.Empty);

    /// <summary>Discards the current graph. Callers are responsible for prompting about unsaved work.</summary>
    public void NewGraph()
    {
        Graph.Clear();
        RebuildFromGraph();
        SelectOnly(null);

        Scale = 1;
        OffsetX = 0;
        OffsetY = 0;
        CurrentFilePath = null;
        ResetHistory();
        RaiseRunStateChanged();
        OnPropertyChanged(nameof(IntervalMilliseconds));
        StatusText = "New graph.";
    }

    public async Task SaveAsync(string path)
    {
        var view = new GraphViewState { Scale = Scale, OffsetX = OffsetX, OffsetY = OffsetY };
        FlushPendingEdit();
        await GraphFile.SaveAsync(Graph, path, view);

        // What is marked clean is what was written, including anything never made an undo step.
        History.Amend();
        _savedState = History.Current;
        CurrentFilePath = path;
        RefreshHistoryState();
        StatusText = $"Saved {System.IO.Path.GetFileName(path)}.";
    }

    /// <summary>
    /// Loads a file over the current graph. Stops continuous mode first so nothing is mid-evaluation
    /// while the graph is being replaced.
    /// </summary>
    public async Task<GraphRestoreResult> LoadAsync(string path)
    {
        await Executor.StopContinuousAsync();
        FlushPendingEdit();

        var document = await GraphFile.LoadAsync(path);
        var result = GraphRestore.Into(Graph, document, Registry);
        RebuildFromGraph();

        if (document.View is { } view)
        {
            Scale = view.Scale is > 0 ? view.Scale : 1;
            OffsetX = view.OffsetX;
            OffsetY = view.OffsetY;
        }

        CurrentFilePath = path;
        ResetHistory();
        RaiseRunStateChanged();
        OnPropertyChanged(nameof(IntervalMilliseconds));

        var name = System.IO.Path.GetFileName(path);
        StatusText = result.IsComplete
            ? $"Opened {name}: {Nodes.Count} node(s), {Links.Count} link(s)."
            : $"Opened {name} with {result.Warnings.Count} warning(s).";

        return result;
    }

    /// <summary>
    /// Brings the view-models into line with <see cref="Graph"/> after the model was changed directly:
    /// a load, a paste or an undo. Incremental: a node or link that is still in the graph keeps its
    /// view-model (and so its visual, its selection and its measured height), and only what was added
    /// or removed is touched. A load, where nothing survives, degenerates to a plain rebuild.
    /// </summary>
    private void RebuildFromGraph()
    {
        ClearPreview();

        // ---- Nodes, in graph order, which is z-order.
        var previous = Nodes.ToDictionary(n => n.Model);
        var nodes = new List<NodeViewModel>(Graph.Nodes.Count);

        foreach (var model in Graph.Nodes)
        {
            if (previous.Remove(model, out var node))
            {
                node.SyncFromModel();
            }
            else
            {
                node = new NodeViewModel(model);
                Attach(node);
            }

            nodes.Add(node);
        }

        foreach (var stale in previous.Values)
            Detach(stale);

        SyncCollection(Nodes, nodes);

        // ---- Links, by model instance: a link that survived a restore is the same Link object.
        var portLookup = new Dictionary<Port, PortViewModel>();
        foreach (var node in Nodes)
        foreach (var port in node.Inputs.Concat(node.Outputs))
            portLookup[port.Model] = port;

        var previousLinks = Links.ToDictionary(l => l.Model);
        var links = new List<LinkViewModel>(Graph.Links.Count);

        foreach (var link in Graph.Links)
        {
            if (previousLinks.Remove(link, out var viewModel))
            {
                links.Add(viewModel);
            }
            else if (portLookup.TryGetValue(link.Source, out var source)
                     && portLookup.TryGetValue(link.Target, out var target))
            {
                links.Add(new LinkViewModel(link, source, target));
            }
        }

        SyncCollection(Links, links);

        _linksByNode.Clear();
        foreach (var node in Nodes)
            _linksByNode[node.Model.Id] = [];

        foreach (var link in Links)
        {
            Track(link, link.Source.Node);
            Track(link, link.Target.Node);
        }

        foreach (var node in Nodes)
            node.RefreshPortConnections();

        foreach (var link in Links)
            link.RefreshAppearance();

        // Groups first: a collapsed group moves the anchors of every wire touching it.
        SyncGroups();

        foreach (var link in Links)
            link.Refresh();

        RaiseSelectionChanged();
        Realizer.Invalidate();
    }

    /// <summary>
    /// Makes <paramref name="collection"/> equal <paramref name="wanted"/> in content and order, with
    /// few change notifications when most items survive, and a plain refill when none do.
    /// </summary>
    private static void SyncCollection<T>(ObservableCollection<T> collection, IReadOnlyList<T> wanted)
        where T : class
    {
        var wantedSet = wanted.ToHashSet();

        if (!collection.Any(wantedSet.Contains))
        {
            collection.Clear();
            foreach (var item in wanted)
                collection.Add(item);

            return;
        }

        for (var i = collection.Count - 1; i >= 0; i--)
        {
            if (!wantedSet.Contains(collection[i]))
                collection.RemoveAt(i);
        }

        for (var i = 0; i < wanted.Count; i++)
        {
            if (i < collection.Count && ReferenceEquals(collection[i], wanted[i]))
                continue;

            var at = collection.IndexOf(wanted[i]);
            if (at >= 0)
                collection.Move(at, i);
            else
                collection.Insert(i, wanted[i]);
        }
    }

    // ---- Undo / redo ----------------------------------------------------------------------

    /// <summary>
    /// How long inline typing must pause before it becomes an undo step. Without this, every
    /// keystroke in a node's text box would be a step of its own.
    /// </summary>
    private static readonly TimeSpan EditCommitDelay = TimeSpan.FromMilliseconds(700);

    public GraphHistory History { get; }

    /// <summary>The committed state when the document was last saved or opened; clean means equal to it.</summary>
    private string _savedState;

    private readonly DispatcherTimer _editCommitTimer;
    private string? _pendingEditLabel;

    private int _changeDepth;
    private string _changeLabel = string.Empty;

    /// <summary>
    /// True while an undo or redo is writing the graph. Property and geometry notifications it causes
    /// are its own doing, not edits to record.
    /// </summary>
    private bool _restoring;

    public bool CanUndo => History.CanUndo;
    public bool CanRedo => History.CanRedo;

    public string UndoToolTip => History.UndoLabel is { } label ? $"Undo {label} (Ctrl+Z)" : "Nothing to undo";
    public string RedoToolTip => History.RedoLabel is { } label ? $"Redo {label} (Ctrl+Y)" : "Nothing to redo";

    /// <summary>
    /// Opens a change that becomes one undo step when disposed. Nested changes fold into the outermost
    /// one, so an operation built from others (add a node, put it in a group, wire it) undoes as a
    /// whole, under the outer label. A change that turns out to alter nothing records nothing.
    /// </summary>
    /// <remarks>
    /// Pending inline edits are committed first, as a step of their own: they happened before this
    /// change and should not be undone along with it.
    /// </remarks>
    public IDisposable BeginChange(string label)
    {
        if (_changeDepth++ == 0)
        {
            FlushPendingEdit();
            _changeLabel = label;
        }

        return new ChangeScope(this);
    }

    private void EndChange()
    {
        if (--_changeDepth > 0)
            return;

        if (!_restoring)
            History.Commit(_changeLabel);

        RefreshHistoryState();
    }

    private sealed class ChangeScope(WorkspaceViewModel owner) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            owner.EndChange();
        }
    }

    /// <summary>
    /// Records an edit that arrives as a stream: typing into a node, or into the interval box. The
    /// step is committed once the stream pauses, when focus moves, or before any other change.
    /// </summary>
    private void NoteEdit(string label)
    {
        // Inside a change the edit is part of it; during a restore it is the restore's own doing.
        if (_changeDepth > 0 || _restoring)
            return;

        _pendingEditLabel ??= label;
        IsDirty = true;

        _editCommitTimer.Stop();
        _editCommitTimer.Start();
    }

    /// <summary>Commits a pending inline edit now. Called by the view when keyboard focus moves.</summary>
    public void FlushPendingEdit()
    {
        _editCommitTimer.Stop();

        if (_pendingEditLabel is not { } label)
            return;

        _pendingEditLabel = null;
        History.Commit(label);
        RefreshHistoryState();
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private Task UndoAsync() => StepAsync(undo: true);

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private Task RedoAsync() => StepAsync(undo: false);

    private async Task StepAsync(bool undo)
    {
        // Mid-gesture (a drag in progress) the graph is between states; there is nothing sound to undo to.
        if (_changeDepth > 0)
            return;

        FlushPendingEdit();

        // A continuous run must not evaluate a graph that is being rewritten underneath it.
        using (await Executor.ExclusiveAsync())
        {
            if (_changeDepth > 0)
                return;

            string? label;
            _restoring = true;
            try
            {
                label = undo ? History.Undo() : History.Redo();
                if (label is not null)
                    RebuildFromGraph();
            }
            finally
            {
                _restoring = false;
            }

            if (label is null)
                return;

            StatusText = undo ? $"Undid {label}." : $"Redid {label}.";
        }

        OnPropertyChanged(nameof(IntervalMilliseconds));
        RaiseRunStateChanged();
        RefreshHistoryState();
    }

    private void ResetHistory()
    {
        _editCommitTimer.Stop();
        _pendingEditLabel = null;
        History.Reset();
        _savedState = History.Current;
        RefreshHistoryState();
    }

    private void RefreshHistoryState()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(UndoToolTip));
        OnPropertyChanged(nameof(RedoToolTip));
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();

        // Clean exactly when the committed state is the saved one, so undoing back to it clears the
        // title's asterisk. An edit still waiting to be committed keeps the document dirty.
        IsDirty = _pendingEditLabel is not null || History.Current != _savedState;
    }
}
