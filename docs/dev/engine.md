# Engine: execution, persistence, undo

## Execution model

`GraphExecutor` orders nodes with Kahn's algorithm, pulls each input from the output it is wired to,
then awaits `EvaluateAsync`. Cycles are refused at connection time by `Graph.TryConnect`, so the
executor never has to deal with feedback loops.

Two things follow from this that are easy to get wrong:

- **Order is data dependency, and nothing else.** Two nodes with no wire between them may evaluate in
  either order. Side effects are sequenced with **Run If / Done** (`NodeBase.AddRunGate`), which is
  just another wire, so Kahn's algorithm honours it with no special case: File Write's Done into File
  Read's Run If makes the read run second.
- **Skipping is the one thing the executor adds.** A node is skipped when its Run If is wired and not
  `true`, or when any wired input comes from a node that failed or was skipped this pass (unless the
  node `Merges`). Skipped means not evaluated, outputs cleared, `IsSkipped` set,
  `NodeRunPhase.Skipped` reported without a `Started`, and listed in `GraphRunResult.Skipped`. A failed
  node's outputs are cleared too. Before this, a failed node's dependents ran on whatever it had left
  behind: null if it had cleared its outputs, **the previous pass's values** if not. Inputs from a
  blocked node are pulled as null even into a merge node, never as a stale value.
- Run If and Done are a plain `bool` input and output, not a dedicated signal type, so a Success,
  Compare or Is Empty output gates a node directly with no Gate node, and the four places a new port
  type touches are untouched. Done is set by the executor after a successful evaluation; it means "ran",
  not "succeeded". The ports are declared last, so they sit at the bottom of the node, and since links
  are saved by port name, files saved before the ports existed load with them unwired.
- Dropping a wire from a `Done` output on empty canvas connects the new node's Run If, not the first
  boolean input it fits (`WorkspaceViewModel`, where the catalog's auto-connect picks the target).
- **Continuous mode is graph-level configuration, not a node.** `Graph.IntervalMilliseconds` drives
  it (edited in the toolbar box beside the Continuous button, `Ctrl+Shift+R` to toggle), so every graph can
  run continuously with nothing to wire up. The loop is `PeriodicTimer` on a background task, and a
  tick is *skipped* rather than queued when the previous pass is still running. There was a
  `ClockNode`/`IClockNode`/`Pulse` trio here; it is gone, so old files mentioning `Control/Clock`
  load with an "unknown node type" warning, which is the intended degradation.

**Run Once shows execution** (`RunFeedback`): the executor's optional per-node observer
(`RunOnceAsync(observer)`, `NodeRunPhase`) reports each node starting and finishing, on the
evaluation thread; the view marks a node `IsRunning` while it evaluates and queues a flash when it
finishes (a skipped node reports `Skipped` and gets no flash: it did not run, and its dimmed
style already says so). Flashes are **paced**, never less than a spacing apart (30 ms, shrinking so the whole cascade
fits 0.75 s; each flash plays for 0.3 s), because a pass over ordinary nodes takes microseconds and
would otherwise light up in a single frame with no visible order. Keep `RunFeedback.FlashDuration`
and the `NodeFlash` storyboard in step. When the spacing is under a frame the schedule catches up
(several flashes per tick, which is what holds a big graph to its budget); above it, the next flash is
timed from the last one instead, since at ~2 timer ticks apart a late tick would otherwise fire two
flashes together. A slow node holds the queue back naturally, so the playback never
shows an order that did not happen. `IsFlashing` is cleared once the flash has played, or a node
realized later (scrolled into view) would replay it. Continuous runs pass no observer: they would
flicker constantly, and pay nothing for the feature. The running highlight changes only the border
colour — a thicker border would move the port dots off their anchors (`NodeMetrics` assumes 1px).
`Ctrl+R` is safe inside a node's text box: a plain `TextBox` has no binding for it, unlike
`RichTextBox`, where it means align-right.

Evaluation runs off the UI thread. Nodes must not raise `PropertyChanged` from inside
`EvaluateAsync`: assign quietly, and let `WorkspaceViewModel` call `NodeBase.RaiseBindingsChanged()`
on the dispatcher once the pass completes. `OutputNode` is the worked example.

- **Nodes with memory derive from `StatefulNode`** (Changed, Counter, Previous Value, History, Plot,
  Log): private fields, never saved, cleared by `Reset()`. `Remember(context)` resets them when
  `NodeEvaluationContext.Run` changes, which it does each time a continuous run starts; one-shot
  passes carry the latest, so Run Once steps through. A run number rather than a first-pass flag,
  because a node skipped on the first pass would miss the flag. Undo keeps node instances
  (`Reconcile`), so it keeps their memory too.
- **`INodeHost`** (`NodeEvaluationContext.Host`, set on `GraphExecutor.Host`) is how a node reaches
  what only the app has: the clipboard and desktop notifications, implemented by `DesktopNodeHost` on
  the dispatcher, and the process exit code and command-line parameters, implemented by `CliNodeHost`
  in `vnoder-cli` (the editor ignores the exit code and has no parameters, so a CLI graph still runs
  there). It is null in a harness, and those nodes then fail with a clear message, except CLI Param,
  which treats a missing host like a missing parameter. Add to the interface rather than giving Nodes
  a UI reference, and implement the new member in both hosts. CLI Write is deliberately not on it: it
  writes to `Console` directly, which works everywhere and simply goes nowhere in the editor.

### Per-item evaluation

Wiring a list into an input that takes one item runs the node once per item (the Grasshopper model,
Option C in `TODO.md`). It is all in Core; **node code never knows** — `EvaluateAsync` sees one item on
each per-item input, so every existing node supports it for free. Keep it that way.

- The state is one flag, **`Link.Iterate`**, saved as `"each": true` on the link (omitted when false).
  `NodeBase.RunsPerItem` is "any input link iterates", and `OutputPort.EffectiveType` is then the list
  of the declared type (Done excepted: it stays one flag). `ValueType` stays the declared type; the
  view, `Graph.Fit` and wire colours use the effective one.
- **`Graph.Settle()` keeps links consistent** and must run after anything that can change what a node
  carries: connect, disconnect, remove, toggle. It walks in topological order: a link that no longer
  fits whole but fits per item starts iterating, an iterating link whose source is no longer a list
  goes back to whole, and one that fits neither way is removed (and returned, so the view can forget
  it; the workspace says so in the status bar). That walk is what makes iteration propagate down a
  chain. The workspace does it in `SettleLinks()`.
- **Whole wins when both fit.** A list into an `any` input (Hash, Output, Length, Equals) stays whole
  until the user ticks "For each item" on the input's right-click menu (`Graph.SetIterate`). Only a
  list into an input that cannot take it whole iterates on its own. Run If never iterates.
- **Restore connects with `Graph.Attach`, then settles once.** Whether a link fits can depend on links
  upstream of it that are not restored yet (a document lists links in creation order, not
  topologically), so `TryConnect`'s type check would reject valid links. `Into`, `Append` and
  `Reconcile` all attach unchecked and warn about whatever `Settle` removes.
- The executor (`EvaluatePerItemAsync`) pairs several per-item inputs **by index** and fails on a
  length mismatch; non-list values count as one item, null as none. An item that throws fails the
  whole node, prefixed "Item i of n". Results are **typed arrays** (`string[]` for text, so Join and
  the List nodes take them), an `any` output narrowed to its items' common type; a value-type list
  with a missing item stays `object[]` rather than inventing a zero. `NodeBase.ItemCount` feeds the
  header's `×N` badge.
- A per-item node whose output is already a list gives a list of lists (`string[][]`), and a list
  input (Join) fed with one iterates again, peeling one level. That is all the nesting there is.
- Per-item wires are dashed (`WireLayer` pens are keyed by brush *and* dash), and a per-item input
  shows a teal ×. `LinkViewModel.RefreshAppearance()` raises `"Appearance"` only when colour or dash
  changed, so a settle redraws only the wires that did.

## Persistence

Graphs save as JSON (`.vnoder`) through `GraphFile` / `GraphRestore` in Core. Toolbar New / Open /
Save plus the built-in `Ctrl+N` / `Ctrl+O` / `Ctrl+S` gestures and `Ctrl+Shift+S`; the window title
shows the file name and a `*` when dirty.

- **Nodes are keyed by definition path** (`"type": "Math/Add"`), not CLR type name, so a file stays
  readable and survives class renames. To rename a node's `NodeDefinition` path, list the old one in
  `FormerPaths = ["Old/Path"]`: saved files still load and are written back under the new path, and
  the old path never appears in the catalogue. Without it, every saved instance becomes an unknown node
  type. Renaming a *port* drops just that link, with a warning.
- **Inline state is captured by reflection** (`NodeStateProperties`): the public read/write
  properties a node declares itself, of a JSON-friendly type. This is deliberate — it is what keeps
  adding a node to one class, with no serializer to touch. Do not replace it with per-node
  serialization code.
- **A partly unreadable file still opens.** `GraphRestore.Into` collects warnings (unknown node type,
  missing port, newer format version) rather than throwing, and the UI shows them. Malformed JSON is
  the one case that throws.
- **Loading replaces the graph in place** via `Graph.Clear()`, keeping one `Graph`/`GraphExecutor`
  instance for the session, and `WorkspaceViewModel.RebuildFromGraph()` mirrors the result. Continuous
  mode is stopped first so nothing is mid-evaluation while the graph is swapped.
- Saved ids are preserved on load, so save → load → save is byte-identical. **Paste is the
  exception**: `GraphRestore.Append` always mints fresh ids and never clears the graph, because one
  fragment may be pasted repeatedly into the same document.
- **Import** (toolbar, `Ctrl+I`, `MainWindow.ImportCommand` since WPF has no built-in command for it)
  adds a whole file to the open graph through `GraphRestore.Import`: the paste path (`Append` and
  `Import` share `AppendCore`) plus the file's groups and the notes on them, all under fresh ids, with
  what did not survive reported as warnings rather than dropped silently. The file's interval and
  view are ignored, the current file path is kept, and the block keeps its layout, centred in the view
  (`CenteringOffset`) and selected so it can be dragged into place. It is one `BeginChange`, so one
  undo step, and it is not stopped by or a reason to stop a continuous run, any more than a paste is.
- The clipboard holds the same JSON, via `GraphFile.CaptureSubset` — the selected nodes plus only
  the links whose *both* ends are selected. `GraphFile.TryParse` returns null rather than throwing, so
  unrelated clipboard text is simply ignored.
- Node size is written as `w`/`h` only once the node has been resized, and groups as a `groups`
  block; both are omitted when absent, so files stay minimal.

Dirty tracking listens to each node model's `PropertyChanged` and **ignores an empty property name**,
because that is what `RaiseBindingsChanged()` sends — without that filter a continuous run would mark the
document dirty several times a second. Beyond that, **clean means "the committed undo state equals the
saved one"** (`History.Current == _savedState`), so undoing back to the last save clears the `*`.
Geometry changes dirty the document only inside an open change (a drag or resize): a node reporting
its measured height is not an edit, and with virtualization that happens on every pan.

## Undo / redo

`GraphHistory` (Core) is a stack of **whole-document snapshots**, the same JSON a file saves, not a
command per operation. That is deliberate and for the same reason persistence is reflective: a new
node's inline state is undoable with no undo code, and a new operation cannot forget to be undoable.
Do not replace it with per-operation commands.

- **Commit after, never before.** `Commit(label)` snapshots the graph and records a step only if it
  differs from the current one, so a click that moves nothing or a rejected connection leaves no
  empty step.
- In the workspace every mutating operation is wrapped in `using var change = BeginChange("label")`.
  Scopes nest and only the outermost commits, which is how add-node + join-group + auto-wire from the
  catalog undo as one step. A drag or resize opens a scope at gesture start (`_gestureChange` in
  `MainWindow`) and closes it on mouse-up or `LostMouseCapture`, so a drag is one step. **A new
  mutating operation needs a `BeginChange`**, or it will be folded into whatever is committed next.
- Streams of edits (typing into a node, the interval box) go through `NoteEdit`, which debounces
  them into one step (`EditCommitDelay`). A pending edit is flushed before any other change, on
  save, and whenever keyboard focus moves, so it never merges with an unrelated step.
- **Undo restores in place** via `GraphRestore.Reconcile`, not `Into`: surviving nodes and links keep
  their instances (so evaluated outputs are not wiped), and node and link order is restored so a
  capture afterwards reproduces the snapshot byte for byte. `RebuildFromGraph` is incremental for the
  same reason: it keeps each surviving view-model and calls `NodeViewModel.SyncFromModel()`, which
  must not write back to the model (an unresized node's model has no size, and writing the view's
  default into it would make the next snapshot differ).
- `_restoring` suppresses the property and geometry notifications a restore causes, and undo holds
  `GraphExecutor.ExclusiveAsync()` while it rewrites the graph so a continuous run cannot evaluate
  it mid-restore.
- `Ctrl+Z` is the routed `ApplicationCommands.Undo`, so a focused `TextBox` undoes its own typing
  first; the toolbar buttons bind to the workspace commands directly and always mean the graph.

