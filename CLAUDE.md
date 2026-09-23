# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

`vNoder` is a node-based visual scripting editor (Blueprint / Shader-Graph style): a zoomable,
pannable canvas where nodes are added by double-clicking and searching, wired through typed ports,
edited inline in the node body, and run either once or continuously.

`README.md` is the user-facing description: what it does, the controls, and the node catalogue.
This file is the part that is not obvious from reading the code — the invariants, and the traps that
have already cost time.

## Build and run

The solution is one level down, in `src/`, in the newer XML solution format (`.slnx`, not `.sln`) —
pass the path explicitly, since some tooling still looks for `*.sln`:

```bash
dotnet build src/vNoder.slnx
```

```bash
dotnet run --project src/vNoder.App
```

Requires the .NET 10 SDK, and builds/runs on Windows only (WPF).

## Projects

- `src/vNoder.Core` — plain `net10.0`: graph model, port/type system, node registry, execution
  engine. **No WPF reference**, so it can be exercised without a UI host.
- `src/vNoder.Nodes` — plain `net10.0`, references Core only: the node implementations.
- `src/vNoder.App` — WPF (`net10.0-windows`), references Core and Nodes. Only new NuGet dependency
  is `CommunityToolkit.Mvvm`.

Keep graph and node logic out of `vNoder.App`. That split is what keeps it testable, and a WPF
assembly cannot be referenced from a cross-platform test project.

## Adding a node type

This is the path that must stay cheap — nodes get added constantly. A new node is **one class** in
`vNoder.Nodes`:

```csharp
[NodeDefinition("Math/Double", Description = "Doubles the input.")]
public sealed class DoubleNode : NodeBase
{
    private readonly InputPort _in;
    private readonly OutputPort _out;

    public DoubleNode()
    {
        _in = AddInput<double>("Value");
        _out = AddOutput<double>("Result");
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        _out.Value = _in.ValueOr(0d) * 2;
        return Task.CompletedTask;
    }
}
```

`NodeRegistry` finds it by reflection and it appears in the search popup. Do not add a registration
list, factory switch, or any other second place to edit — that is the whole point of the attribute.

Conventions that go with it:
- Declare ports in the constructor and keep the returned `InputPort`/`OutputPort` in readonly fields.
- Read inputs with `input.ValueOr(fallback)`; an unwired input is `null`, which is how a node falls
  back to its inline value. That one call is the whole idiom for a **wirable parameter**: declare the
  input, keep the inline property, and read `_minIn.ValueOr(Min)`. Prefer this to a fixed inline-only
  value wherever a parameter could plausibly be computed — the generators do it for min/max, length
  and charset.
- User-editable state is a plain property using the `Set(ref _field, value)` helper. To give it an
  inline editor, add a `DataTemplate` with `DataType="{x:Type n:YourNode}"` to `MainWindow.xaml`; a
  node without one simply renders no editor (`NodeEditorTemplateSelector` handles the fallback).
- Throw from `EvaluateAsync` to report a failure. The executor catches it, records it on
  `NodeBase.LastError`, shows it on the node, and carries on with the rest of the pass.
- `EvaluateAsync` is genuinely async — do real async I/O rather than blocking.
- An inline field that mirrors an input port should be gated with
  `IsEnabled="{Binding Inputs, Converter={StaticResource PortNotWired}, ConverterParameter=<PortName>}"`,
  so it greys out once a wire drives it. It binds to the node's `Inputs` collection with the port name
  rather than to a per-field property, because adding one such property to every node is exactly the
  boilerplate the one-class rule exists to avoid. `NodeBase.RaiseBindingsChanged()` is what refreshes
  it — the workspace calls it both after a run and whenever wiring changes.
- Inline state is saved and loaded automatically (see **Persistence**). Nothing to register, but it
  does mean a public settable property of a simple type *is* persisted state — give anything that
  evaluation computes a non-public setter, as `OutputNode.Value` and `LengthNode.Count` do.

## Node catalogue notes

- Display nodes (`OutputNode`, `LengthNode`) render into a **read-only `TextBox`**, not a `TextBlock`,
  so the value can be selected and copied. WPF `TextBlock` has no text selection.
- `OutputNode` shows its value **in full and nothing but the value** — no truncation, no byte counts,
  no glyphs, no placeholder when empty; it is meant to be read and copied verbatim, and `LengthNode`
  is where sizes come from. No scrollbars, anchored top-left, clipped to the node, with an inline Wrap
  toggle. It also passes its input straight through an `any`
  output, so a display can sit mid-chain rather than only at the end. `Resizable = true` (as are the
  file nodes): enlarging is how you read a long value.
- `LengthNode` is the same kind of terminal display but reports a size, and also exposes the count as
  a number so it can feed arithmetic.
- **"Any" (`object`) is permissive in both directions.** An `any` input accepts every output, and an
  `any` output feeds every input — its real type is only known at run time, so the check is deferred
  to evaluation, where `ValueOr` falls back instead of failing. Without the second half, `OutputNode`'s
  pass-through port could only ever feed another `any` input, which made it useless.
- **Port types** are `double`, `string`, `bool`, `byte[]`, `string[]` (a text list, produced by
  `SplitNode` and consumed by `JoinNode`) and `object` for "any". Adding a type means touching four
  places: `Graph.FriendlyTypeName`, `PortTypeNames.For`, `PortBrushConverter` for its wire colour, and
  usually `OutputNode.Format` / `LengthNode` so it can be displayed and measured.
- `FileDeleteNode` is destructive and unconditional — in a continuous run it fires every tick. Its
  `Deleted` output reports whether *this* pass removed something, since `File.Delete` is silent about
  a file that was not there and "nothing to do" should not read as success.
- Text nodes **clamp rather than throw** on out-of-range arguments (`SubstringNode`) and treat a
  degenerate argument as a no-op (`ReplaceAllNode` with an empty Find, `SplitNode` with an empty
  separator). A graph running continuously would otherwise sit in an error state for as long as a
  bound is mid-edit.
- `EqualsNode` and `CompareNode` take `any` on both sides and compare by kind: numbers numerically,
  buffers by content, everything else as the text it prints as. That matters — `10` sorts before `2`
  as text but not as a number.
- `HashNode` replaced `Sha256Node`; the algorithm is an enum property, so it renders as a combo box
  and persists through the generic state capture with no serializer work. Its definition path changed
  from `Crypto/SHA-256` to `Crypto/Hash`, so files saved before the change report an unknown node
  type for it.

## Execution model

`GraphExecutor` orders nodes with Kahn's algorithm, pulls each input from the output it is wired to,
then awaits `EvaluateAsync`. Cycles are refused at connection time by `Graph.TryConnect`, so the
executor never has to deal with feedback loops.

Two things follow from this that are easy to get wrong:

- **Order is data dependency, and nothing else.** Two nodes with no wire between them may evaluate in
  either order. In particular `FileWriteNode.Success` (bool) cannot feed `FileReadNode.Path`
  (string), so a write and a read in one graph are unordered — read may run first. Sequencing side
  effects needs a new mechanism (an execution-flow port type, or a pulse chain), not a bug fix.
- **Continuous mode is graph-level configuration, not a node.** `Graph.IntervalMilliseconds` drives
  it (edited in the toolbar box beside the Continuous button, `Ctrl+F5` to toggle), so every graph can
  run continuously with nothing to wire up. The loop is `PeriodicTimer` on a background task, and a
  tick is *skipped* rather than queued when the previous pass is still running. There was a
  `ClockNode`/`IClockNode`/`Pulse` trio here; it is gone, so old files mentioning `Control/Clock`
  load with an "unknown node type" warning, which is the intended degradation.

Evaluation runs off the UI thread. Nodes must not raise `PropertyChanged` from inside
`EvaluateAsync`: assign quietly, and let `WorkspaceViewModel` call `NodeBase.RaiseBindingsChanged()`
on the dispatcher once the pass completes. `OutputNode` is the worked example.

## Persistence

Graphs save as JSON (`.vnoder`) through `GraphFile` / `GraphRestore` in Core. Toolbar New / Open /
Save plus the built-in `Ctrl+N` / `Ctrl+O` / `Ctrl+S` gestures and `Ctrl+Shift+S`; the window title
shows the file name and a `*` when dirty.

- **Nodes are keyed by definition path** (`"type": "Math/Add"`), not CLR type name, so a file stays
  readable and survives class renames. Renaming a node's `NodeDefinition` path breaks existing files;
  renaming a *port* drops just that link, with a warning.
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
- The clipboard holds the same JSON, via `GraphFile.CaptureSubset` — the selected nodes plus only
  the links whose *both* ends are selected. `GraphFile.TryParse` returns null rather than throwing, so
  unrelated clipboard text is simply ignored.
- Node size is written as `w`/`h` only once the node has been resized, and groups as a `groups`
  block; both are omitted when absent, so files stay minimal.

Dirty tracking listens to each node model's `PropertyChanged` and **ignores an empty property name**,
because that is what `RaiseBindingsChanged()` sends — without that filter a continuous run would mark the
document dirty several times a second.

## Canvas mechanics

- One wrapper (`SceneRoot` in `MainWindow.xaml`) carries the zoom/pan `TransformGroup`; the wire
  `ItemsControl` and the node `ItemsControl` both sit inside it, wires declared first so they render
  underneath. Both use a `Canvas` items panel, which measures to zero — children still render and
  hit-test, which is what makes the unbounded canvas work.
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
- **Wire thickness compensates for zoom** (`ScaleToStrokeThicknessConverter`). The scene carries the
  zoom transform, so a fixed thickness of 2 renders at half a pixel at 25% zoom and antialiases away
  to nothing, which reads as wires simply not being drawn. Anything else drawn in scene space and
  meant to stay legible needs the same treatment.
- Text areas inside nodes hide their scrollbars but stay scrollable: the **middle button drags to
  scroll** whichever text area is under the cursor, and only pans the workspace when it is not.
- Gestures live in `MainWindow.xaml.cs` because they are hit-testing over visuals, not bindings:
  wheel zooms about the cursor, right/middle-drag pans, header-drag moves, output-port drag wires up,
  clicking a connected input detaches it, double-click on empty canvas opens the search popup.
- Delete is a `Window.KeyDown` handler, not a `KeyBinding`, so that it deletes the selected node only
  when focus is not in an inline editor.
- Right-drag pans **only from empty canvas**; over a node or group header the right button opens a
  context menu instead. Middle-drag always pans.
- Context menus are built in code-behind rather than in XAML. A menu is outside the visual tree, so
  binding it to workspace commands from inside a node's `DataTemplate` needs `PlacementTarget`
  gymnastics; constructing it where both the node and the workspace are in scope is plainer.
- The search popup is opened from the **mouse-up**, not from the double-click's mouse-down. It is
  `StaysOpen="False"`, and such a popup takes the mouse capture when it opens, so opening it on the
  second mouse-down means the release that follows is a button event outside the captured popup —
  which dismisses it instantly. `_pendingSearchAt` carries the position from the down to the up, and
  the open is posted at `DispatcherPriority.Input` so it happens after that release has been routed.

## Chrome and theming

The framework's default control chrome is light, so `App.xaml` carries **implicit** dark styles
(`CheckBox`, `ComboBox`, `ComboBoxItem`, `ListBoxItem`, `ScrollBar`) rather than keyed ones — anything
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
  Resizable = true)]` — today `Output`, `Static Text` and the three file nodes. Most nodes are a fixed
  list of ports and gain nothing from a grip; turn it on for nodes with a body worth enlarging. A
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

## Verifying changes

There is no test project, so `dotnet test` does nothing today. Adding one needs no restructuring,
because Core and Nodes are WPF-free:

```bash
dotnet new xunit -o src/vNoder.Tests && dotnet sln src/vNoder.slnx add src/vNoder.Tests
```

```bash
dotnet test src/vNoder.slnx --filter FullyQualifiedName~MyTestName
```

Until then, engine behaviour is checked with a **throwaway console harness** in the scratchpad: a
`net10.0` exe with `ProjectReference`s to Core and Nodes that builds graphs, runs
`GraphExecutor.RunOnceAsync`, and asserts on outputs. It is fast to write and exercises the real
model. Prefer it to reasoning about whether a graph change is correct.

The canvas cannot be driven by synthetic input from here — the session has run inside a VM console
where clicks never reach the window, and Windows blocks foreground activation. Two techniques work
instead, and both have caught real bugs:

- **Render the real window headlessly.** Reference `vNoder.App` from a scratch exe, construct
  `App`/`InitializeComponent`, `new MainWindow()`, drive its `WorkspaceViewModel` directly, then
  `RenderTargetBitmap.Render(window)` and assert on pixels. Measure the *extent* of what was drawn,
  not merely that something was drawn — a truncated wire still paints pixels, which is how a clipping
  bug survived one round of testing.
- **Inspect layout instead of guessing.** `LayoutInformation.GetLayoutClip` / `GetLayoutSlot`, and
  `DesiredSize` vs `RenderSize`, name the culprit directly when something is cut off or mis-sized.

Two traps when doing this: a rendered check can pass for the wrong reason (thin strokes antialias
away at low zoom — compare against a baseline), and `mv`/`cp` that preserves an old timestamp will
leave MSBuild convinced the XAML is unchanged, so you measure a stale build. `touch` the file.

Anything about feel rather than geometry — drag inertia, focus order, whether a gesture is
comfortable — still has to be checked by hand with `dotnet run --project src/vNoder.App`.

Commit history starts from the first working POC; `src/`, `README.md` and this file are tracked.
