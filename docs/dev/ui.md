# UI: canvas, chrome, selection, groups, notes, dialogs

## Canvas mechanics

- One wrapper (`SceneRoot` in `MainWindow.xaml`) carries the zoom/pan `TransformGroup`; the
  `WireLayer` and the node `ItemsControl` both sit inside it, wires declared first so they render
  underneath. Both are on a `Canvas`, which measures to zero — children still render and hit-test,
  which is what makes the unbounded canvas work.
- **The canvas is virtualized.** The node `ItemsControl` binds to `RealizedNodes`, not `Nodes`:
  `NodeRealizer` keeps only nodes near the viewport (enter at 200 screen px, leave at 600, so an edge
  node is not churned on every pixel of a pan). Every node still has its view-model; only visuals are
  virtual, and since anchors come from `NodeMetrics` rather than visuals, wires, groups and selection
  do not care. On-screen nodes are realized at once; margin nodes at most `MarginBudget` per update,
  the rest at background priority, because template instantiation is the expensive part and a
  grid-aligned column crossing the margin together used to hitch the pan. **Geometry-driven
  invalidation must use `InvalidateLater`**: a realized node's first measurement raises
  `GeometryChanged`, and a Render-priority update from there re-runs within the same frame with a
  fresh budget — that cascade silently realized whole columns at once. The node holding keyboard
  focus is pinned so its text box is not destroyed mid-typing. A never-realized node's
  `MeasuredHeight` is an estimate (`NodeMetrics.EstimatedEditorHeight`) until it is first drawn.
- **Wires are `DrawingVisual`s in `WireLayer`, not elements.** One visual per link, no template or
  bindings; a moved wire re-renders only itself, and wires outside the viewport (by the Bezier's
  control-point hull, `LinkViewModel.Bounds`) render nothing. The layer is not hit-testable, so a
  click on a wire falls through to the canvas.
- A port row with a port on one side only draws nothing on the other: the `PortSlot` style collapses
  a port `ContentControl` whose content is null. Without it the port template is still instantiated
  for the null port and paints an unbound dark dot on the node's edge.
- Wire endpoints come from `NodeMetrics` plus the node's own width, not from measuring the visual
  tree, so an anchor is exact the instant a node moves or is resized. **The header height and port
  row height in `NodeMetrics` and in the node template in `MainWindow.xaml` must be changed
  together**; node width is bound to `NodeViewModel.Width`, so only its default lives in metrics.
- `NodeViewModel.MeasuredHeight` is the one piece of geometry the view reports back (via
  `SizeChanged`), because a node body's height depends on its inline editor. Groups size their box
  from it; wires never need it.
- A node drag refreshes only the wires touching that node, via `_linksByNode` in
  `WorkspaceViewModel` — do not replace that with a sweep over all links.
- **Everything drawn in scene space must sit on a `Canvas`, never loose in the scene `Grid`.** A Grid
  measures its children against the viewport, so a wire longer than the window gets its `DesiredSize`
  clamped, and WPF then applies a **layout clip** at that size — silently cutting the shape at a hard
  edge in *scene* coordinates, which panning moves to an arbitrary place on screen. A `Canvas`
  measures children with infinite space, so no clip. This bit the live preview wire, which was the one
  element not on a Canvas. `LayoutInformation.GetLayoutClip` is how to spot it.
- **Wire thickness compensates for zoom** (`ScaleToStrokeThicknessConverter.For`, used by
  `WireLayer` and the preview wire). The scene carries the
  zoom transform, so a fixed thickness of 2 renders at half a pixel at 25% zoom and antialiases away
  to nothing, which reads as wires simply not being drawn. Anything else drawn in scene space and
  meant to stay legible needs the same treatment.
- Text areas inside nodes hide their scrollbars but stay scrollable: the **middle button drags to
  scroll** whichever text area is under the cursor.
- Gestures live in `MainWindow.xaml.cs` because they are hit-testing over visuals, not bindings:
  wheel zooms about the cursor, right/middle-drag pans, header-drag moves, output-port drag wires up,
  clicking a connected input detaches it, double-click on empty canvas opens the search popup.
- Delete is a `Window.KeyDown` handler, not a `KeyBinding`, so that it deletes the selected node only
  when focus is not in an inline editor.
- **Pan and zoom start only from empty canvas** (`IsBackdrop`: the grid backdrop or the host itself).
  Over a node or group header the right button opens a context menu, the middle button does nothing
  (or scrolls a text area), and the wheel is left unhandled so a scrollable editor under the cursor
  gets it. Wires and a group box's body are not hit-testable, so they count as empty canvas.
- Context menus are built in code-behind rather than in XAML. A menu is outside the visual tree, so
  binding it to workspace commands from inside a node's `DataTemplate` needs `PlacementTarget`
  gymnastics; constructing it where both the node and the workspace are in scope is plainer.
- The search popup is opened from the **mouse-up**, not from the double-click's mouse-down. It is
  `StaysOpen="False"`, and such a popup takes the mouse capture when it opens, so opening it on the
  second mouse-down means the release that follows is a button event outside the captured popup —
  which dismisses it instantly. `_pendingSearchAt` carries the position from the down to the up, and
  the open is posted at `DispatcherPriority.Input` so it happens after that release has been routed.
- **The catalog tree is a flattened list, not a `TreeView`.** `SearchResults` holds `CatalogRow`s
  (`CatalogRows.cs`): a `CatalogCategoryRow` per category and the `CatalogNodeRow`s under it, told apart
  by implicit templates in the `SearchList` resources. That keeps the search box in charge of the
  keyboard (Up, Down and Enter move over headers and nodes alike; Enter or a click on a header toggles
  it instead of committing), and keeps one selection to commit. A `TreeView` would take focus and its
  own keyboard handling. Left and Right work the tree only while the box is empty, since otherwise they
  move the caret.
- Which categories are open is session state: all start closed. A search or a wire filter (`IsCatalogSearching`) opens every category with a match, because a
  hit inside a closed one reads as no hit; closing one during a search lasts until the query changes
  (`_collapsedWhileSearching`). Opening a category scrolls its last node and then its header into view
  (`RevealSearchSelection`), in two passes because the list is virtualized. The one button at the
  foot (`ToggleAllCategories`: Expand all while any is closed, else Collapse all) acts on the
  categories on show, so during a search it changes only `_collapsedWhileSearching` and leaves the
  browsing state alone.

## Chrome and theming

The framework's default control chrome is light, so `App.xaml` carries **implicit** dark styles
(`CheckBox`, `ComboBox`, `ComboBoxItem`, `ListBoxItem`, `ScrollBar`, and `DataGrid` with its header and
cell for the Table node) rather than keyed ones — anything
dropped into a node body is themed without extra wiring. Add a style here when a new control type
first appears in a node, or it will render white.

An implicit style only supplies the properties it sets; the framework's **theme style still fills in
the rest**, and a minimum beats an explicit size. The `ScrollBar` style set `Width` but not
`MinWidth`, so every scrollbar silently stayed at the 17px system metric until `MinWidth`/`MinHeight`
were cleared. If a size setter appears to do nothing, look for a theme-supplied minimum.

A continuous run is easy to leave on by accident, so it is loud: the toggle switches to a bright red
border (`RunToggleButton`), and the status bar shows a live iteration count and elapsed time. The
elapsed time is driven by a `DispatcherTimer` rather than by evaluations, because the interval can be
seconds long and the clock would otherwise appear frozen.

Ports highlight the one the cursor can act on: mid-drag that is a **type-compatible input**, and
otherwise an **output**, since outputs are where a wire starts.

## Selection, resizing and groups

- **Selection**: click selects one, Ctrl+click toggles, and a left-drag on empty canvas is a marquee.
  A click that never moves ends as an empty marquee rectangle, which is what clears the selection.
  Dragging any selected node's header moves the whole selection, rigidly: `_dragOrigins` captures
  every node's start position so the delta is applied against a fixed origin rather than accumulated.
- The marquee selects **live** on every mouse-move, so the catch is visible before releasing. It
  recomputes from `_marqueeBaseSelection` (what was selected when the drag began) rather than
  accumulating, which is what makes repeated application safe.
- `Ctrl+C` / `Ctrl+V` copy and paste the selection; pasted nodes land offset and become the selection.
- **Resizing is opt-in**, declared on the node's own attribute: `[NodeDefinition("...",
  Resizable = true)]` — today `Output`, `Static Text`, `Template`, `Image`, `QR Code` and `HTTP Request`. Most
  nodes are a fixed list of ports and gain nothing from a grip; turn it on for nodes with a body worth
  enlarging. A node that is **not** resizable drops any saved size when its view-model is created
  (`NodeViewModel` resets `Model.Size`), so a file saved while a type was still resizable — the file
  nodes were — does not pin its editor to a stale height with no grip to fix it. A
  resizable node starts at `NodeMetrics.DefaultContentHeight` rather than Auto, deliberately: a
  display node must not resize itself when a large value arrives. `Width` is the node's own width
  (wires follow it, because output anchors are `Node.X + Node.Width`) and `ContentHeight` sizes only
  the inline editor area; `ContentHeight` stays `double.NaN` (WPF's "Auto") until a node is resized,
  and the grip reads the editor's current `ActualHeight` on the first drag so the node does not jump.
- A resizable node cannot be shrunk past its editor. The **editor template's root declares its own
  `MinHeight`** in `MainWindow.xaml`; the view reads it and reports it as
  `NodeViewModel.MinContentHeight`. The clamp lives in the `ContentHeight` **property**, not in the
  resize gesture, so no caller can bypass it, and a too-small height loaded from an older file is
  corrected on measure. Only the template knows what it contains, so when you add a resizable node,
  set `MinHeight` on its editor root or it will fall back to `NodeMetrics.MinContentHeight`.
- Reading that `MinHeight` means descending **two** levels from the editor `ContentControl`: its own
  visual child is a `ContentPresenter`, and the presenter's child is what the `DataTemplate` produced.
  Reading the child directly gets the presenter, whose `MinHeight` is always zero — which silently
  disabled every declared minimum once already.
- **Groups** are purely presentational. `NodeGroup` lives in Core only so it travels with the saved
  document, exactly as node positions do; `GraphExecutor` never looks at groups, and grouping changes
  nothing about ordering or connectivity. A node belongs to at most one group (`Graph.AddGroup`
  enforces this), and a group that loses all its members is removed.
- Collapsing a group hides its members and points them at the box via `NodeViewModel.CollapsedHost`.
  A hidden port then anchors to the edge of the group box instead of to its node, so **wires crossing
  a collapsed boundary stay drawn**, stubbed to the box — that is how you can tell a collapsed group
  is still wired in. Only links with both ends in the *same* collapsed group are hidden.
- Collapsing moves no node but moves every anchor in the group, so the toggle refreshes link geometry
  as well as visibility. For the same reason `OnNodeGeometryChanged` refreshes the node's group box
  *before* its wires: a stale box would leave stubbed wires a frame behind.
- The group box computes its own bounds from member geometry, so it has no stored rectangle to keep
  in sync.
- A **collapsed** box is as wide as its header text (name plus node count, measured with
  `FormattedText` in `NodeGroupViewModel.CollapsedWidth`), never narrower than
  `NodeMetrics.GroupCollapsedWidth`. The width is computed in the view-model, not left to the view,
  because wires crossing into a collapsed group anchor to its edge. Keep the measurement in step with
  the header in `GroupTemplate` (font sizes, weight, margins). A rename while collapsed changes the width
  with no member moving, so it raises `AnchorsMoved` and the workspace refreshes the wires touching
  the group.
- The group header is a `TextBlock` with `IsHitTestVisible="False"`, not a `TextBox`, and has no
  buttons, so the **whole bar** is a drag handle. Double-clicking it collapses or expands. Groups are
  named in a prompt when created and renamed from the header's context menu (`AppDialog.Prompt` —
  WPF has no input box). `G` groups the selection.
- Opening the catalog inside a group box adds the new node to that group (`_pendingGroup`, captured in
  `OpenSearch`), which covers both double-clicking inside a group and dropping a wire there. Collapsed
  groups are excluded from `GroupAt`, since a node dropped into one would vanish on arrival.
- Hover highlighting: nodes use a plain `IsMouseOver` style trigger, but the group box is deliberately
  not hit-testable, so its `IsHovered` is set from `WorkspaceHost_MouseMove` via `Workspace.UpdateHover`.
- Bare-letter shortcuts (`G`, `Delete`) live in `Window_KeyDown` behind a `TextBoxBase` focus check,
  not in `Window.InputBindings`, so they cannot fire while the user is typing in a node or the search
  box.
- Dropping a dragged wire on empty canvas opens the catalog and auto-connects the new node's first
  compatible input (`_pendingLinkSource` in `WorkspaceViewModel`). The catalog is then **filtered to
  node types that could accept that wire**, via `NodeDefinition.AcceptsInput`. Port types are declared
  in constructors, so a definition discovers them by instantiating its node once and caching the
  result (`NodeDefinition.InputTypes`).

## Notes

- **Notes are presentational, like groups.** `GraphNote` lives in Core only so it travels with the
  document (a `notes` block, omitted when empty); the executor never looks at it. Each note is attached
  to exactly one node or group by id (`TargetId`), and its position is an **offset from the target's
  top-left corner**, not a canvas position, so a note follows its target with nothing to keep in sync.
- `Graph` prunes a note whose target is gone (`PruneNotes`, from `Remove`, `RemoveGroup` and the
  empty-group prune in `AddGroup`), so deleting a node or ungrouping takes its notes with it and undo
  brings them back. `AddNote` refuses a note with no target. `CaptureSubset` carries notes on copied
  nodes, and `Append` re-targets them to the pasted nodes with fresh ids; group notes are not copied,
  since groups are not.
- Restore attaches notes **after** groups (`RestoreNotes`), since a note may target a group, and
  `Reconcile` rebuilds them wholesale the way it does groups. In the workspace `SyncNotes` runs at the
  end of `SyncGroups` for the same reason: it must see the group view-models that were just recreated.
- The leader is a straight dotted amber line between the nearest edges of the two boxes
  (`NoteViewModel.BuildLeader`), deliberately unlike a wire (port-coloured Bezier, solid or dashed). The
  leaders sit under the node layer and the note bodies above it; notes are few, so not virtualized.
  The note reports its measured height (`Note_SizeChanged`), since the leader meets its edge.
- **Visibility is a session view setting** (`WorkspaceViewModel.NotesVisible`, toggled by bare `N` in
  `Window_KeyDown` and a toolbar button), not document state: hiding notes is not an edit and is not
  saved. A note on a node hidden inside a collapsed group is hidden with it.
- Typing in a note goes through `NoteEdit` like a node's inline field, and a note drag or resize is one
  change scope (`Gesture.DraggingNote` / `ResizingNote`), so each undoes as a single step. A linked note
  saves `w`/`h` only once resized; until then its height is Auto and follows the text.
- **A note and its target light each other up.** Hovering or selecting a node, or hovering a group,
  sets the note's `IsHighlighted` (brighter box and leader); hovering the note sets the target's
  `IsNoteHovered` (amber border). Hover reaches the view-models through `MouseEnter`/`MouseLeave`
  (`Hoverable_MouseEnter`), since `IsMouseOver` is read-only and cannot be bound back. `Detach` clears
  a note's hover, so a note replaced while under the cursor does not leave its target lit. The
  `IsHighlighted` trigger lives on `LinkedNoteShell`, not `NoteShell`, because `NoteNodeShell` is based
  on `NoteShell` and its DataContext (a node) has no such property.
- **A free-standing note is a node, `NoteNode` (`Output/Note`)**, not a target-less `GraphNote`: as a
  node it gets selection, moving, grouping, copy/paste, undo and saving with no new code. It has no
  ports and evaluates to nothing. `NodeTemplateSelector` gives it `NoteNodeTemplate` (no header, the
  note look) instead of `NodeTemplate`; the template keeps `Tag="Editor"` and a `MinHeight` on the
  editor root, which the node resize grip reads. `N` hides Note nodes too (their template binds
  `NotesVisible`), and hiding deselects them and keeps them out of the marquee, so `Delete` cannot
  remove a note the user cannot see. Linked notes cannot be added to a Note node.

## Dialogs

Every dialog goes through `Views/AppDialog.xaml` (`Message`, `Confirm`, `Prompt`). **Do not use
`MessageBox`**: it is the system dialog, always renders in light chrome, and looks like a different
program next to this window. `AppDialog` is `WindowStyle="None"` with its own draggable title bar, so
no system chrome appears at all.

## Window closing

`OnClosing` must **not** call `Close()` on itself, even after an `await`. `ConfirmDiscardAsync` can
complete synchronously (a modal prompt is answered inline, or the user picks "No"), so the
continuation runs on the same stack while the window is still closing, and WPF throws
`InvalidOperationException: Cannot ... call ... Close ... while a Window is closing`. The handler is
synchronous, sets `e.Cancel`, and posts `ConfirmThenCloseAsync` with `Dispatcher.BeginInvoke` so the
first close unwinds before the second is issued.

