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
    }

    public NodeRegistry Registry { get; }
    public Graph Graph { get; }
    public GraphExecutor Executor { get; }

    public ObservableCollection<NodeViewModel> Nodes { get; } = [];
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
            IsDirty = true;
        }
    }

    [RelayCommand]
    private async Task RunOnceAsync()
    {
        try
        {
            var result = await Executor.RunOnceAsync();
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

    private static string DescribeRun(GraphRunResult result) => result.HasErrors
        ? $"Evaluated {result.NodesEvaluated} node(s); {result.Failed.Count} failed - {result.Failed[0].Title}: {result.Failed[0].LastError}"
        : $"Evaluated {result.NodesEvaluated} node(s).";

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
        var model = definition.CreateInstance();
        model.Position = new GraphPoint(graphPosition.X, graphPosition.Y);

        var viewModel = new NodeViewModel(model);
        Attach(viewModel);

        Graph.Add(model);
        Nodes.Add(viewModel);
        _linksByNode[model.Id] = [];

        SelectOnly(viewModel);
        RaiseRunStateChanged();
        IsDirty = true;
        StatusText = $"Added {definition.Path}.";
        return viewModel;
    }

    [RelayCommand]
    private void DeleteSelected()
    {
        var selected = Nodes.Where(n => n.IsSelected).ToList();
        if (selected.Count == 0)
            return;

        foreach (var node in selected)
            RemoveNode(node);

        StatusText = $"Deleted {selected.Count} node(s).";
    }

    public void RemoveNode(NodeViewModel node)
    {
        foreach (var link in Graph.Remove(node.Model))
            ForgetLink(link);

        Detach(node);
        Nodes.Remove(node);
        _linksByNode.Remove(node.Model.Id);

        // Graph.Remove already dropped it from its group and pruned groups left empty.
        SyncGroups();

        RaiseRunStateChanged();
        RaiseSelectionChanged();
        IsDirty = true;
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
            node.IsSelected = keep.Contains(node)
                              || (node.IsVisible
                                  && rect.IntersectsWith(
                                      new Rect(node.X, node.Y, node.Width, node.MeasuredHeight)));
        }

        RaiseSelectionChanged();
    }

    private void OnNodeGeometryChanged(object? sender, EventArgs e)
    {
        if (sender is not NodeViewModel node)
            return;

        // The box must be up to date before wires are recomputed: a port inside a collapsed group
        // anchors to the box, so a stale box would leave those wires a frame behind.
        GroupContaining(node)?.Refresh();

        if (_linksByNode.TryGetValue(node.Model.Id, out var touching))
        {
            foreach (var link in touching)
                link.Refresh();
        }

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
        if (!string.IsNullOrEmpty(e.PropertyName))
            IsDirty = true;
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

        Graph.AddGroup(new NodeGroup
        {
            Name = name,
            NodeIds = [.. selected.Select(n => n.Model.Id)],
        });

        SyncGroups();
        IsDirty = true;
        StatusText = $"Grouped {selected.Count} nodes as “{name}”.";
    }

    public void RenameGroup(NodeGroupViewModel group, string name)
    {
        group.Name = name;
        IsDirty = true;
        StatusText = $"Renamed group to “{name}”.";
    }

    [RelayCommand]
    private void Ungroup(NodeGroupViewModel? group)
    {
        if (group is null)
            return;

        Graph.RemoveGroup(group.Model);
        SyncGroups();
        IsDirty = true;
        StatusText = "Ungrouped.";
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
        IsDirty = true;
        StatusText = $"Connected {source.Node.Title}.{source.Name} to {target.Node.Title}.{target.Name}.";
        return true;
    }

    /// <summary>Clicking a connected input detaches it: the POC way to undo a wire.</summary>
    public void DisconnectInput(PortViewModel port)
    {
        if (port.AsInput?.Link is not { } link)
            return;

        Graph.Disconnect(link);
        ForgetLink(link);
        IsDirty = true;
        StatusText = "Disconnected.";
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
            { IsInput: true } input when wiringSource is not null
                && PortTypeCompatibility.CanConnect(wiringSource.ValueType, input.ValueType) => input,
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

        var added = GraphRestore.Append(Graph, document, Registry, PasteOffset, PasteOffset);
        if (added.Count == 0)
        {
            StatusText = "Nothing in the clipboard could be pasted.";
            return;
        }

        RebuildFromGraph();
        SelectNodes(Nodes.Where(n => added.Contains(n.Model)));

        IsDirty = true;
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

        if (_pendingLinkSource is { } source)
            matches = matches.Where(d => d.AcceptsInput(source.ValueType));

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

        var node = AddNode(definition, _pendingNodePosition);

        if (group is not null)
        {
            AddNodeToGroup(group, node);
            StatusText = $"Added {definition.Path} to “{group.Name}”.";
        }

        if (linkSource is null)
            return;

        // Wire the dropped output into the first input that accepts it.
        var target = node.Inputs.FirstOrDefault(
            input => PortTypeCompatibility.CanConnect(linkSource.ValueType, input.ValueType));

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
        IsDirty = false;
        RaiseRunStateChanged();
        OnPropertyChanged(nameof(IntervalMilliseconds));
        StatusText = "New graph.";
    }

    public async Task SaveAsync(string path)
    {
        var view = new GraphViewState { Scale = Scale, OffsetX = OffsetX, OffsetY = OffsetY };
        await GraphFile.SaveAsync(Graph, path, view);

        CurrentFilePath = path;
        IsDirty = false;
        StatusText = $"Saved {System.IO.Path.GetFileName(path)}.";
    }

    /// <summary>
    /// Loads a file over the current graph. Stops continuous mode first so nothing is mid-evaluation
    /// while the graph is being replaced.
    /// </summary>
    public async Task<GraphRestoreResult> LoadAsync(string path)
    {
        await Executor.StopContinuousAsync();

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
        IsDirty = false;
        RaiseRunStateChanged();
        OnPropertyChanged(nameof(IntervalMilliseconds));

        var name = System.IO.Path.GetFileName(path);
        StatusText = result.IsComplete
            ? $"Opened {name}: {Nodes.Count} node(s), {Links.Count} link(s)."
            : $"Opened {name} with {result.Warnings.Count} warning(s).";

        return result;
    }

    /// <summary>
    /// Rebuilds every view-model from the current <see cref="Graph"/>. Used after a load, so the views
    /// mirror the model rather than the load having to drive the view-model layer itself.
    /// </summary>
    private void RebuildFromGraph()
    {
        foreach (var node in Nodes)
            Detach(node);

        Nodes.Clear();
        Links.Clear();
        _linksByNode.Clear();
        ClearPreview();

        var portLookup = new Dictionary<Port, PortViewModel>();

        foreach (var model in Graph.Nodes)
        {
            var node = new NodeViewModel(model);
            Attach(node);
            Nodes.Add(node);
            _linksByNode[model.Id] = [];

            foreach (var port in node.Inputs.Concat(node.Outputs))
                portLookup[port.Model] = port;
        }

        foreach (var link in Graph.Links)
        {
            if (!portLookup.TryGetValue(link.Source, out var source)
                || !portLookup.TryGetValue(link.Target, out var target))
            {
                continue;
            }

            var viewModel = new LinkViewModel(link, source, target);
            Links.Add(viewModel);
            Track(viewModel, source.Node);
            Track(viewModel, target.Node);
        }

        foreach (var node in Nodes)
            node.RefreshPortConnections();

        SyncGroups();
        RaiseSelectionChanged();
    }
}
