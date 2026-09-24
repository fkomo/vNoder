using System.ComponentModel;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using vNoder.App.ViewModels;
using vNoder.App.Views;
using vNoder.Core;

namespace vNoder.App
{
    /// <summary>
    /// Canvas interaction for the workspace: zoom, pan, node dragging and wire dragging. These are
    /// direct-manipulation gestures over hit-tested visuals, so they live in code-behind and talk to
    /// <see cref="WorkspaceViewModel"/> rather than being expressed as bindings.
    /// </summary>
    public partial class MainWindow : Window
    {
        private const double MinScale = 0.2;
        private const double MaxScale = 3.0;
        private const double ZoomStep = 1.12;

        private enum Gesture
        {
            None,
            Panning,
            ScrollingText,
            DraggingNodes,
            Wiring,
            Marquee,
            Resizing,
        }

        private Gesture _gesture;

        private Point _panStartScreen;
        private double _panStartOffsetX;
        private double _panStartOffsetY;

        /// <summary>Where each dragged node started, so a multi-node drag stays rigid.</summary>
        private IReadOnlyList<(NodeViewModel Node, double X, double Y)> _dragOrigins = [];
        private Point _dragStartGraph;

        private NodeViewModel? _resizeNode;
        private double _resizeStartWidth;
        private double _resizeStartContentHeight;

        private ScrollViewer? _textScroller;
        private Point _textScrollStartScreen;
        private double _textScrollStartVertical;
        private double _textScrollStartHorizontal;

        private Point _marqueeStartScreen;

        /// <summary>
        /// What was selected when the marquee began. The rectangle is applied against this on every
        /// move rather than accumulated, so the highlight previews the result as the user drags.
        /// </summary>
        private IReadOnlyCollection<NodeViewModel> _marqueeBaseSelection = [];

        private PortViewModel? _wireSource;

        /// <summary>
        /// The undo step a drag or resize is building. Opened when the gesture starts and closed when
        /// it ends, so a drag across a hundred mouse-moves undoes as one step.
        /// </summary>
        private IDisposable? _gestureChange;

        /// <summary>
        /// Where a double-click asked for the node catalog. The popup is opened from the matching
        /// mouse-up rather than here: it is <c>StaysOpen="False"</c>, so a button release outside its
        /// content dismisses it, and opening on the second mouse-down means its own double-click's
        /// release would close it again immediately.
        /// </summary>
        private Point? _pendingSearchAt;

        public MainWindow()
        {
            InitializeComponent();
            DataContext = new WorkspaceViewModel();
        }

        private WorkspaceViewModel Workspace => (WorkspaceViewModel)DataContext;

        protected override void OnClosed(EventArgs e)
        {
            // Let the continuous loop unwind rather than tearing down mid-evaluation.
            _ = Workspace.StopAsync();
            base.OnClosed(e);
        }

        // ---- Zoom ---------------------------------------------------------------------------

        private void WorkspaceHost_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            // Only over empty canvas: over a node the wheel is left alone, so it scrolls a text area
            // there instead of zooming out from under the cursor.
            if (!IsBackdrop(e.OriginalSource))
                return;

            var cursor = e.GetPosition(WorkspaceHost);
            var anchor = Workspace.ScreenToGraph(cursor);

            var scale = Math.Clamp(
                e.Delta > 0 ? Workspace.Scale * ZoomStep : Workspace.Scale / ZoomStep,
                MinScale,
                MaxScale);

            // Zoom about the cursor: the graph point under it must not move.
            Workspace.Scale = scale;
            Workspace.OffsetX = cursor.X - (anchor.X * scale);
            Workspace.OffsetY = cursor.Y - (anchor.Y * scale);
            e.Handled = true;
        }

        // ---- Background clicks --------------------------------------------------------------

        private void WorkspaceHost_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!IsBackdrop(e.OriginalSource))
                return;

            WorkspaceHost.Focus();

            if (e.ClickCount == 2)
            {
                _pendingSearchAt = e.GetPosition(WorkspaceHost);
                e.Handled = true;
                return;
            }

            // A press on empty canvas begins a marquee. A click that never moves ends as an empty
            // rectangle, which clears the selection, so "click nothing to deselect" comes for free.
            _gesture = Gesture.Marquee;
            _marqueeStartScreen = e.GetPosition(WorkspaceHost);
            _marqueeBaseSelection = Keyboard.Modifiers.HasFlag(ModifierKeys.Control)
                ? Workspace.SelectedNodes.ToHashSet()
                : [];
            WorkspaceHost.CaptureMouse();
            e.Handled = true;
        }

        private void WorkspaceHost_MouseDown(object sender, MouseButtonEventArgs e)
        {
            // Over a text area the middle button scrolls that text, not the workspace behind it.
            if (e.ChangedButton == MouseButton.Middle
                && FindAncestor<TextBoxBase>(e.OriginalSource as DependencyObject) is { } textBox
                && FindDescendant<ScrollViewer>(textBox, _ => true) is { } scroller)
            {
                _gesture = Gesture.ScrollingText;
                _textScroller = scroller;
                _textScrollStartScreen = e.GetPosition(WorkspaceHost);
                _textScrollStartVertical = scroller.VerticalOffset;
                _textScrollStartHorizontal = scroller.HorizontalOffset;
                WorkspaceHost.CaptureMouse();
                WorkspaceHost.Cursor = Cursors.ScrollNS;
                e.Handled = true;
                return;
            }

            // Panning starts only from empty canvas: over a node the right button opens its menu, and
            // the middle button does nothing (beyond scrolling a text area, above).
            var pans = e.ChangedButton is MouseButton.Middle or MouseButton.Right
                       && IsBackdrop(e.OriginalSource);

            if (!pans)
                return;

            _gesture = Gesture.Panning;
            _panStartScreen = e.GetPosition(WorkspaceHost);
            _panStartOffsetX = Workspace.OffsetX;
            _panStartOffsetY = Workspace.OffsetY;
            WorkspaceHost.CaptureMouse();
            WorkspaceHost.Cursor = Cursors.SizeAll;
            e.Handled = true;
        }

        private bool IsBackdrop(object source) =>
            ReferenceEquals(source, GridBackdrop) || ReferenceEquals(source, WorkspaceHost);

        // ---- Dragging nodes, groups and wires -------------------------------------------------

        private void NodeHeader_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: NodeViewModel node })
                return;

            e.Handled = true;

            // Ctrl+click edits the selection instead of starting a drag.
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                Workspace.ToggleSelection(node);
                return;
            }

            Workspace.EnsureSelected(node);
            BeginNodeDrag([.. Workspace.SelectedNodes.Select(n => (n, n.X, n.Y))], e);
        }

        private void GroupHeader_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: NodeGroupViewModel group })
                return;

            e.Handled = true;

            if (e.ClickCount == 2)
            {
                ToggleCollapsed(group);
                return;
            }

            // Dragging the box moves its members; the box is derived from where they end up.
            BeginNodeDrag(group.CaptureMemberOrigins(), e);
        }

        private void ToggleCollapsed(NodeGroupViewModel group) => Workspace.ToggleCollapsed(group);

        private void BeginNodeDrag(
            IReadOnlyList<(NodeViewModel Node, double X, double Y)> origins,
            MouseButtonEventArgs e)
        {
            if (origins.Count == 0)
                return;

            _gesture = Gesture.DraggingNodes;
            _dragOrigins = origins;
            _gestureChange = Workspace.BeginChange(origins.Count == 1 ? "Move node" : $"Move {origins.Count} nodes");
            _dragStartGraph = Workspace.ScreenToGraph(e.GetPosition(WorkspaceHost));
            WorkspaceHost.CaptureMouse();
        }

        private void ResizeGrip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: NodeViewModel node } grip)
                return;

            _gesture = Gesture.Resizing;
            _resizeNode = node;
            _gestureChange = Workspace.BeginChange($"Resize {node.Title}");
            _resizeStartWidth = node.Width;

            // An unresized node has an Auto body height; start from what it currently renders as, so
            // the first drag does not make it jump.
            _resizeStartContentHeight = double.IsNaN(node.ContentHeight)
                ? CurrentEditorHeight(grip)
                : node.ContentHeight;

            // In case the node has not been laid out since its template changed.
            if (!node.HasMeasuredMinContentHeight
                && VisualTreeHelper.GetParent(grip) is { } shell)
            {
                node.SetMinContentHeight(EditorMinHeight(shell));
            }

            _dragStartGraph = Workspace.ScreenToGraph(e.GetPosition(WorkspaceHost));
            WorkspaceHost.CaptureMouse();
            e.Handled = true;
        }

        private static double CurrentEditorHeight(DependencyObject grip)
        {
            var root = VisualTreeHelper.GetParent(grip);
            var editor = root is null
                ? null
                : FindDescendant<ContentControl>(root, c => (c.Tag as string) == "Editor");

            return editor is { ActualHeight: > NodeMetrics.MinContentHeight }
                ? editor.ActualHeight
                : NodeMetrics.MinContentHeight;
        }

        private static T? FindDescendant<T>(DependencyObject root, Func<T, bool> match)
            where T : DependencyObject
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);

                if (child is T typed && match(typed))
                    return typed;

                if (FindDescendant(child, match) is { } found)
                    return found;
            }

            return null;
        }

        private void NodeShell_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // A node body's height depends on its inline editor, so the view is the only thing that
            // knows it; groups size their box from it.
            if (sender is not FrameworkElement { DataContext: NodeViewModel node } root)
                return;

            node.SetMeasuredHeight(e.NewSize.Height);

            // Pick up the editor template's declared minimum once, so a resize cannot cut it in half.
            if (node.IsResizable && !node.HasMeasuredMinContentHeight)
                node.SetMinContentHeight(EditorMinHeight(root));
        }

        /// <summary>The <c>MinHeight</c> declared on the root of a node's inline editor template.</summary>
        /// <summary>
        /// The <c>MinHeight</c> declared on the root of a node's inline editor template.
        /// </summary>
        /// <remarks>
        /// The template root is a grandchild, not a child: a ContentControl's own visual child is its
        /// ContentPresenter, and the presenter's child is what the DataTemplate produced. Reading the
        /// child directly gets the presenter, whose MinHeight is always zero.
        /// </remarks>
        private static double EditorMinHeight(DependencyObject root)
        {
            if (FindDescendant<ContentControl>(root, c => (c.Tag as string) == "Editor") is not { } editor)
                return 0;

            if (FindDescendant<ContentPresenter>(editor, _ => true) is not { } presenter
                || VisualTreeHelper.GetChildrenCount(presenter) == 0)
            {
                return 0;
            }

            return VisualTreeHelper.GetChild(presenter, 0) is FrameworkElement { MinHeight: > 0 } content
                ? content.MinHeight
                : 0;
        }

        private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
        {
            while (node is not null)
            {
                if (node is T found)
                    return found;

                node = VisualTreeHelper.GetParent(node) ?? (node as FrameworkElement)?.Parent;
            }

            return null;
        }

        private void OutputPortDot_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: PortViewModel port } || port.IsInput)
                return;

            _gesture = Gesture.Wiring;
            _wireSource = port;
            Workspace.UpdatePreview(port, Workspace.ScreenToGraph(e.GetPosition(WorkspaceHost)));
            WorkspaceHost.CaptureMouse();
            e.Handled = true;
        }

        /// <summary>Clicking a connected input detaches its wire.</summary>
        private void InputPortDot_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: PortViewModel port } || !port.IsInput)
                return;

            Workspace.DisconnectInput(port);
            e.Handled = true;
        }

        private void WorkspaceHost_MouseLeave(object sender, MouseEventArgs e) => Workspace.ClearHover();

        private void WorkspaceHost_MouseMove(object sender, MouseEventArgs e)
        {
            if (_gesture == Gesture.None)
            {
                // Still track what the cursor is over, so highlights follow a plain move.
                var idle = e.GetPosition(WorkspaceHost);
                Workspace.UpdateHover(Workspace.ScreenToGraph(idle));
                Workspace.HighlightPort(FindPortAt(idle), null);
                return;
            }

            var screen = e.GetPosition(WorkspaceHost);
            var graph = Workspace.ScreenToGraph(screen);
            Workspace.UpdateHover(graph);

            // Mid-wire it is the compatible inputs that matter; otherwise the outputs a drag starts from.
            Workspace.HighlightPort(FindPortAt(screen), _wireSource);

            switch (_gesture)
            {
                case Gesture.Panning:
                    Workspace.OffsetX = _panStartOffsetX + (screen.X - _panStartScreen.X);
                    Workspace.OffsetY = _panStartOffsetY + (screen.Y - _panStartScreen.Y);
                    break;

                case Gesture.ScrollingText when _textScroller is not null:
                    _textScroller.ScrollToVerticalOffset(
                        _textScrollStartVertical - (screen.Y - _textScrollStartScreen.Y));
                    _textScroller.ScrollToHorizontalOffset(
                        _textScrollStartHorizontal - (screen.X - _textScrollStartScreen.X));
                    break;

                case Gesture.DraggingNodes:
                {
                    // Every node moves by the same graph-space delta, so the arrangement holds.
                    var dx = graph.X - _dragStartGraph.X;
                    var dy = graph.Y - _dragStartGraph.Y;

                    foreach (var (node, x, y) in _dragOrigins)
                        node.MoveTo(x + dx, y + dy);

                    break;
                }

                case Gesture.Resizing when _resizeNode is not null:
                    _resizeNode.Width = Math.Max(
                        NodeMetrics.MinWidth,
                        _resizeStartWidth + (graph.X - _dragStartGraph.X));
                    _resizeNode.ContentHeight = Math.Max(
                        _resizeNode.MinContentHeight,
                        _resizeStartContentHeight + (graph.Y - _dragStartGraph.Y));
                    break;

                case Gesture.Wiring when _wireSource is not null:
                    Workspace.UpdatePreview(_wireSource, graph);
                    break;

                case Gesture.Marquee:
                    UpdateMarquee(screen);
                    break;
            }
        }

        private void UpdateMarquee(Point screen)
        {
            var rect = new Rect(_marqueeStartScreen, screen);

            Canvas.SetLeft(Marquee, rect.X);
            Canvas.SetTop(Marquee, rect.Y);
            Marquee.Width = rect.Width;
            Marquee.Height = rect.Height;
            Marquee.Visibility = Visibility.Visible;

            // Select as the rectangle moves, so what is caught is visible before releasing.
            Workspace.SelectInRect(GraphRect(screen), _marqueeBaseSelection);
        }

        private Rect GraphRect(Point screen) =>
            new(Workspace.ScreenToGraph(_marqueeStartScreen), Workspace.ScreenToGraph(screen));

        private void WorkspaceHost_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                if (_gesture == Gesture.Wiring && _wireSource is not null)
                    FinishWiring(e.GetPosition(WorkspaceHost));
                else if (_gesture == Gesture.Marquee)
                    FinishMarquee(e.GetPosition(WorkspaceHost));
            }

            _gesture = Gesture.None;
            EndGestureChange();
            _dragOrigins = [];
            _textScroller = null;
            _resizeNode = null;
            _wireSource = null;
            WorkspaceHost.Cursor = Cursors.Arrow;
            WorkspaceHost.ReleaseMouseCapture();
            Workspace.HighlightPort(FindPortAt(e.GetPosition(WorkspaceHost)), null);

            if (_pendingSearchAt is not { } at)
                return;

            _pendingSearchAt = null;

            // Queued behind this input event so the popup is not opened while the release that would
            // dismiss it is still being routed.
            Dispatcher.BeginInvoke(() => Workspace.OpenSearch(at), DispatcherPriority.Input);
        }

        private void EndGestureChange()
        {
            _gestureChange?.Dispose();
            _gestureChange = null;
        }

        /// <summary>
        /// Capture can be lost without a button release (another window takes focus mid-drag). The
        /// gesture is over either way, so its undo step must be closed, or every later change would
        /// fold into it.
        /// </summary>
        private void WorkspaceHost_LostMouseCapture(object sender, MouseEventArgs e)
        {
            // The event bubbles: a text box inside a node releasing its own capture is not this.
            if (!ReferenceEquals(e.OriginalSource, WorkspaceHost))
                return;

            if (_gesture is Gesture.DraggingNodes or Gesture.Resizing)
                _gesture = Gesture.None;

            EndGestureChange();
        }

        private void WorkspaceHost_SizeChanged(object sender, SizeChangedEventArgs e) =>
            Workspace.ViewportSize = e.NewSize;

        /// <summary>
        /// Any move of keyboard focus ends a burst of inline typing, so it becomes its own undo step
        /// rather than merging with whatever is typed next. The node that now holds focus is kept
        /// realized even if it is panned out of view, so its text box is not destroyed under the caret.
        /// </summary>
        private void Window_PreviewGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            Workspace.FlushPendingEdit();

            Workspace.Realizer.Pinned = NodeOf(e.NewFocus as DependencyObject);
        }

        private static NodeViewModel? NodeOf(DependencyObject? element)
        {
            while (element is not null)
            {
                if (element is FrameworkElement { DataContext: NodeViewModel node })
                    return node;

                element = VisualTreeHelper.GetParent(element) ?? (element as FrameworkElement)?.Parent;
            }

            return null;
        }

        // ---- Undo / redo ----------------------------------------------------------------------

        /// <summary>
        /// Routed through <see cref="ApplicationCommands.Undo"/> so that a focused text box, which
        /// handles that command itself, undoes its own typing first; only when focus is elsewhere does
        /// Ctrl+Z reach the graph. Ignored mid-gesture, when the graph is between two states.
        /// </summary>
        private void UndoCommand_CanExecute(object sender, CanExecuteRoutedEventArgs e) =>
            e.CanExecute = _gesture == Gesture.None && Workspace.CanUndo;

        private void UndoCommand_Executed(object sender, ExecutedRoutedEventArgs e) =>
            Workspace.UndoCommand.Execute(null);

        private void RedoCommand_CanExecute(object sender, CanExecuteRoutedEventArgs e) =>
            e.CanExecute = _gesture == Gesture.None && Workspace.CanRedo;

        private void RedoCommand_Executed(object sender, ExecutedRoutedEventArgs e) =>
            Workspace.RedoCommand.Execute(null);

        private void FinishWiring(Point screen)
        {
            var source = _wireSource!;
            Workspace.ClearPreview();

            // Dropping anywhere on an input port (dot or label) connects.
            if (FindInputPortAt(screen) is { } target)
            {
                Workspace.TryConnect(source, target);
                return;
            }

            // Dropped on empty canvas: offer the catalog, and wire up whatever gets picked.
            if (IsBackdrop(WorkspaceHost.InputHitTest(screen)!))
                Dispatcher.BeginInvoke(() => Workspace.OpenSearch(screen, source), DispatcherPriority.Input);
        }

        private void FinishMarquee(Point screen)
        {
            Marquee.Visibility = Visibility.Collapsed;
            Workspace.SelectInRect(GraphRect(screen), _marqueeBaseSelection);
            _marqueeBaseSelection = [];
        }

        /// <summary>
        /// Walks up from whatever is under the cursor to the nearest element bound to a port. The dot
        /// and its label both carry the port as their DataContext, so either will do.
        /// </summary>
        private PortViewModel? FindPortAt(Point screen)
        {
            var hit = WorkspaceHost.InputHitTest(screen) as DependencyObject;

            while (hit is not null)
            {
                if (hit is FrameworkElement { DataContext: PortViewModel port })
                    return port;

                hit = VisualTreeHelper.GetParent(hit) ?? (hit as FrameworkElement)?.Parent;
            }

            return null;
        }

        private PortViewModel? FindInputPortAt(Point screen) =>
            FindPortAt(screen) is { IsInput: true } port ? port : null;

        // ---- Grouping -------------------------------------------------------------------------

        private void NodeShell_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: NodeViewModel node } element)
                return;

            // Right-clicking an unselected node selects it, but leaves a multi-selection alone.
            Workspace.EnsureSelected(node);

            var selected = Workspace.SelectedNodes.Count;
            var menu = new ContextMenu();

            var groupItem = new MenuItem
            {
                Header = selected >= 2 ? $"Group {selected} selected nodes" : "Group selected nodes",
                IsEnabled = Workspace.CanGroupSelection,
            };
            groupItem.Click += (_, _) => PromptAndGroupSelection();
            menu.Items.Add(groupItem);

            if (Workspace.GroupContaining(node) is { } group)
            {
                var ungroupItem = new MenuItem { Header = $"Ungroup \u201c{group.Name}\u201d" };
                ungroupItem.Click += (_, _) => Workspace.UngroupCommand.Execute(group);
                menu.Items.Add(ungroupItem);
            }

            menu.Items.Add(new Separator());

            var copyItem = new MenuItem
            {
                Header = selected >= 2 ? $"Copy {selected} nodes" : "Copy node",
            };
            copyItem.Click += (_, _) => Workspace.CopySelection();
            menu.Items.Add(copyItem);

            menu.Items.Add(new Separator());

            var deleteItem = new MenuItem
            {
                Header = selected >= 2 ? $"Delete {selected} nodes" : "Delete node",
            };
            deleteItem.Click += (_, _) => Workspace.DeleteSelectedCommand.Execute(null);
            menu.Items.Add(deleteItem);

            menu.PlacementTarget = element;
            menu.IsOpen = true;
            e.Handled = true;
        }

        private void PromptAndGroupSelection()
        {
            var name = AppDialog.Prompt(this, "Group nodes", "Name for the new group:", Workspace.SuggestedGroupName);

            if (name is not null)
                Workspace.GroupSelected(name);
        }

        private void PromptAndRenameGroup(NodeGroupViewModel group)
        {
            var name = AppDialog.Prompt(this, "Rename group", "Group name:", group.Name);

            if (name is not null)
                Workspace.RenameGroup(group, name);
        }

        private void GroupHeader_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: NodeGroupViewModel group } element)
                return;

            var menu = new ContextMenu();

            var toggleItem = new MenuItem { Header = group.IsCollapsed ? "Expand" : "Collapse" };
            toggleItem.Click += (_, _) => ToggleCollapsed(group);
            menu.Items.Add(toggleItem);

            var renameItem = new MenuItem { Header = "Rename…" };
            renameItem.Click += (_, _) => PromptAndRenameGroup(group);
            menu.Items.Add(renameItem);

            var selectItem = new MenuItem { Header = "Select members" };
            selectItem.Click += (_, _) => Workspace.SelectNodes(group.Members);
            menu.Items.Add(selectItem);

            menu.Items.Add(new Separator());

            var ungroupItem = new MenuItem { Header = "Ungroup" };
            ungroupItem.Click += (_, _) => Workspace.UngroupCommand.Execute(group);
            menu.Items.Add(ungroupItem);

            menu.PlacementTarget = element;
            menu.IsOpen = true;
            e.Handled = true;
        }

        // ---- Add-node search ----------------------------------------------------------------

        private void SearchPopup_Opened(object sender, EventArgs e)
        {
            // The popup's own visual tree is not ready until after this event, hence the dispatch.
            Dispatcher.BeginInvoke(
                () =>
                {
                    SearchBox.Focus();
                    SearchBox.SelectAll();
                },
                DispatcherPriority.Input);
        }

        private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Enter:
                    Workspace.CommitSearch();
                    e.Handled = true;
                    break;

                case Key.Escape:
                    Workspace.CloseSearch();
                    e.Handled = true;
                    break;

                case Key.Down:
                    Workspace.MoveSearchSelection(1);
                    SearchList.ScrollIntoView(SearchList.SelectedItem);
                    e.Handled = true;
                    break;

                case Key.Up:
                    Workspace.MoveSearchSelection(-1);
                    SearchList.ScrollIntoView(SearchList.SelectedItem);
                    e.Handled = true;
                    break;
            }
        }

        private void SearchList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            // Commit on a single click, but only when the click actually landed on a result row.
            if (ItemsControl.ContainerFromElement(SearchList, (DependencyObject)e.OriginalSource) is not ListBoxItem item)
                return;

            SearchList.SelectedItem = item.DataContext;
            Workspace.CommitSearch();
            e.Handled = true;
        }

        // ---- Save / load --------------------------------------------------------------------

        private const string FileFilter =
            "vNoder graph (*.vnoder)|*.vnoder|JSON (*.json)|*.json|All files (*.*)|*.*";

        private bool _closeConfirmed;

        /// <summary>
        /// Delete is handled here rather than as a <c>KeyBinding</c> so that it deletes the selected
        /// node only when the user is not typing in an inline editor.
        /// </summary>
        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            // Bare-letter shortcuts must never fire while the user is typing into a node or the
            // search box, which is why these are here rather than in Window.InputBindings.
            if (Keyboard.FocusedElement is TextBoxBase)
                return;

            switch (e.Key)
            {
                case Key.Delete:
                    Workspace.DeleteSelectedCommand.Execute(null);
                    e.Handled = true;
                    break;

                case Key.G when Workspace.CanGroupSelection:
                    PromptAndGroupSelection();
                    e.Handled = true;
                    break;
            }
        }

        private void CopyCommand_Executed(object sender, ExecutedRoutedEventArgs e) =>
            Workspace.CopySelection();

        private void PasteCommand_Executed(object sender, ExecutedRoutedEventArgs e) =>
            Workspace.Paste();

        private async void NewCommand_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            if (await ConfirmDiscardAsync())
                Workspace.NewGraph();
        }

        private async void OpenCommand_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            if (!await ConfirmDiscardAsync())
                return;

            var dialog = new OpenFileDialog
            {
                Title = "Open graph",
                Filter = FileFilter,
                DefaultExt = GraphFile.Extension,
            };

            if (dialog.ShowDialog(this) != true)
                return;

            try
            {
                var result = await Workspace.LoadAsync(dialog.FileName);

                // A partly unreadable file still opens; say what did not survive.
                if (!result.IsComplete)
                    AppDialog.Message(this, "Opened with warnings", string.Join(Environment.NewLine, result.Warnings));
            }
            catch (Exception exception)
            {
                AppDialog.Message(this, "Could not open", exception.Message);
            }
        }

        private async void SaveCommand_Executed(object sender, ExecutedRoutedEventArgs e) =>
            await SaveAsync(Workspace.CurrentFilePath);

        private async void SaveAsCommand_Executed(object sender, ExecutedRoutedEventArgs e) =>
            await SaveAsync(null);

        /// <summary>Saves to <paramref name="path"/>, or asks for one when it is null.</summary>
        private async Task<bool> SaveAsync(string? path)
        {
            if (path is null)
            {
                var dialog = new SaveFileDialog
                {
                    Title = "Save graph",
                    Filter = FileFilter,
                    DefaultExt = GraphFile.Extension,
                    AddExtension = true,
                    FileName = Workspace.CurrentFilePath is { } current
                        ? System.IO.Path.GetFileName(current)
                        : "graph" + GraphFile.Extension,
                };

                if (dialog.ShowDialog(this) != true)
                    return false;

                path = dialog.FileName;
            }

            try
            {
                await Workspace.SaveAsync(path);
                return true;
            }
            catch (Exception exception)
            {
                AppDialog.Message(this, "Could not save", exception.Message);
                return false;
            }
        }

        /// <summary>Returns true when it is safe to discard the current graph.</summary>
        private async Task<bool> ConfirmDiscardAsync()
        {
            if (!Workspace.IsDirty)
                return true;

            var name = Workspace.CurrentFilePath is { } path
                ? System.IO.Path.GetFileName(path)
                : "this graph";

            var answer = AppDialog.Confirm(
                this,
                "Unsaved changes",
                $"Save changes to {name} before continuing?",
                yes: "Save",
                no: "Discard");

            return answer switch
            {
                DialogChoice.Yes => await SaveAsync(Workspace.CurrentFilePath),
                DialogChoice.No => true,
                _ => false,
            };
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            base.OnClosing(e);

            if (_closeConfirmed || e.Cancel || !Workspace.IsDirty)
                return;

            e.Cancel = true;

            // The prompt and the re-issued Close must not run inside this call. ConfirmDiscardAsync
            // can complete synchronously (the user answers a modal dialog, or picks "No"), and
            // calling Close() while the window is still closing throws. Posting it lets this close
            // unwind first.
            Dispatcher.BeginInvoke(() => _ = ConfirmThenCloseAsync(), DispatcherPriority.Normal);
        }

        private async Task ConfirmThenCloseAsync()
        {
            if (!await ConfirmDiscardAsync())
                return;

            _closeConfirmed = true;
            Close();
        }
    }
}
