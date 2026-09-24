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

NuGet versions are managed centrally in `src/Directory.Packages.props`: a project's
`<PackageReference>` has no `Version`, and adding or upgrading a package means editing that one file.

Settings shared by every project live in `src/Directory.Build.props`. Today that is only
`DebugType=embedded`: symbols are compiled into each DLL rather than written as `.pdb` files, so any
copy of a build — including Release — gives stack traces with file and line numbers. Do not set
`DebugType` in an individual project file.

## Projects

- `src/vNoder.Core` — plain `net10.0`: graph model, port/type system, node registry, execution
  engine. **No WPF reference**, so it can be exercised without a UI host.
- `src/vNoder.Nodes` — plain `net10.0`, references Core only: the node implementations. Its only
  NuGet dependencies are `BCrypt.Net-Next`, `QRCoder` and `SixLabors.ImageSharp`, all pure managed
  code; prefer the BCL, and add a package only for something the BCL genuinely lacks.
- **ImageSharp is pinned to 3.1.x — do not upgrade to 4.x casually.** 4.x adds a build-time license-key
  check (`SixLabors_ValidateLicense`) that only warns in Debug but **fails Release builds** without a
  key. It is also under the Six Labors Split License (free for open source, small for-profits and
  non-profits; commercial otherwise), which is the project owner's call, not a technical one.
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
- An **enum property** renders as a combo box with
  `ItemsSource="{Binding X, Converter={StaticResource EnumValues}}"` — no `ObjectDataProvider`
  resource per enum — and persists through the generic state capture like any other property.
  **Enums are saved by number**, not name (`GraphFile` has no string-enum converter), so a new member
  goes at the end and existing ones are never reordered or removed — `HashAlgorithmKind.CRC32` was
  appended for exactly this reason. The first member is the default, so choose it deliberately or set
  the field's initial value.
- A **family of nodes** can share inline state on an abstract base (`TrigNode.Degrees`) and one
  template declared for that base: `NodeEditorTemplateSelector` walks base types. The same goes for
  port boilerplate: `UnaryMathNode` / `BinaryMathNode` declare the ports, and each operator is still
  its own attributed class. The registry only lists attributed types, so bases never appear in the
  catalogue.
- A text-or-bytes `any` input goes through `NodeValues.AsBytes` (text as UTF-8). Any value shown or
  spliced as text goes through `NodeValues.ToText`, the one formatter shared by Output, To Text,
  Template and Pad, so a number or date reads the same everywhere.
- **A value that does not parse is a result, not an error.** To Number, To Boolean, Date Parse, JSON
  Get (missing path), List Item At (out of range), Path Exists and File Info report it on a `Success` /
  `Found` / `Exists` output and fall back, so a continuous run is not stuck in an error state and the
  flag can drive a `Select`. Throw for input that has no sane reading (malformed JSON, an invalid
  regex, a missing file to copy). JSON Format is the exception that proves it: validity is its answer,
  so it reports invalid JSON on `Valid` / `Error` instead of throwing.
- CPU-heavy work (password hashing) and blocking I/O with no async API (directory walks) run in
  `Task.Run` with the context's cancellation token, so a slow node neither blocks the UI nor outlives
  a stopped continuous run.
- Port names appear in XAML as `ConverterParameter`; quote one that contains a space
  (`ConverterParameter='Work Factor'`).
- User-editable state is a plain property using the `Set(ref _field, value)` helper. To give it an
  inline editor, add a `DataTemplate` with `DataType="{x:Type n:YourNode}"` to `MainWindow.xaml`; a
  node without one simply renders no editor (`NodeEditorTemplateSelector` handles the fallback).
- Throw from `EvaluateAsync` to report a failure. The executor catches it, records it on
  `NodeBase.LastError`, shows it on the node, **clears the node's outputs**, skips the nodes that take
  data from it, and carries on with the rest of the pass. A node need not clear its own outputs
  before throwing.
- A node that **touches the outside world** (reads or writes files, the network, processes) calls
  `AddRunGate()` as the last line of its constructor. That declares the Run If input and Done output;
  the executor does the rest, and the node's own code never looks at them. A node that brings branches
  together or displays whatever arrives sets `Merges = true` on its attribute instead.
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
  is where sizes come from. A vertical scrollbar when the value is taller than the node (nothing horizontal), anchored top-left,
  clipped to the node, with an inline Wrap
  toggle. It also passes its input straight through an `any`
  output, so a display can sit mid-chain rather than only at the end. `Resizable = true`: enlarging is how
  you read a long value.
- `LengthNode` is the same kind of terminal display but reports a size, and also exposes the count as
  a number so it can feed arithmetic.
- **"Any" (`object`) is permissive in both directions.** An `any` input accepts every output, and an
  `any` output feeds every input — its real type is only known at run time, so the check is deferred
  to evaluation, where `ValueOr` falls back instead of failing. Without the second half, `OutputNode`'s
  pass-through port could only ever feed another `any` input, which made it useless.
- **Port types** are `double`, `string`, `bool`, `byte[]`, `string[]` (a text list, produced by
  Split, Lines, File Search, Regex Extract All and JSON Get on an array, consumed by Join and the List
  nodes), `DateTime` ("date/time", from
  `DateTimeNode` to `FormatDateTimeNode`) and `object` for "any". Adding a type means touching four
  places: `Graph.FriendlyTypeName`, `PortTypeNames.For`, `PortBrushConverter` for its wire colour, and
  usually `OutputNode.Format` / `LengthNode` so it can be displayed and measured — plus
  `EqualsNode.AreEqual` / `Text` and `CompareNode` if the type has a natural order (dates compare as
  dates, and display as ISO 8601).
- A new **category** gets its header colour in `CategoryBrushConverter`; one without an entry falls back
  to grey.
- `HttpRequestNode` is one node with a method selector, not one per verb, so switching verb needs no
  rewiring; the verbs are in its description so catalogue search still finds "post". An error status
  is a *result* (Status, Success, Body), not a throw — an API's error body is what you want to read.
  Only no response at all throws. It uses one static `HttpClient` with per-request timeouts. Like
  file writes and deletes, it fires every tick in a continuous run.
- **Date arithmetic goes through `DateMath.AddTicks`**, which saturates at DateTime's range. Only the
  offset is a `double`, rounded to whole ticks and added as a `long`: a date's own tick count (~6e17)
  is past double's exact range, so summing as doubles silently lost milliseconds (From Unix read
  `.123` as `.122`), and `DateTime.MaxValue.Ticks` rounds up past itself as a `double`, which is how
  `AddSeconds` with a large offset once threw. Cultures resolve through `NodeValues.Culture` with
  `predefinedOnly: true`, since under ICU any well-formed name is otherwise accepted as an
  invariant-formatting culture.
- `FormatDateTimeNode` moved from `Text/Format Date Time` to `Date/Format`, with the old path in
  `FormerPaths`. The Date nodes read an unwired date input as "now", as Format always has.
- **Select picks a value; it does not skip work by itself.** Skipping a branch is Run If's job (see
  **Execution model**); Select, Coalesce, Is Empty and Output are declared `Merges = true`, so they
  still run when one side is skipped and read it as empty.
- Regex nodes (and the Regex mode of Contains / List Filter) use a 2 s match timeout, so a
  catastrophically backtracking pattern fails the node instead of hanging a continuous run. An empty
  pattern is a no-op, like an empty Find in Replace All.
- `Template` fills `{A}`–`{D}` only and leaves every other brace alone, so JSON needs no escaping.
  `{{` is the one escape; there is deliberately no `}}`, since JSON closes nested objects with `}}`.
- JSON travels as **text**; there is no JSON port type. JSON Get turns an array into a `string[]` (so
  the List nodes apply) and an object into its JSON text. JSON Set writes non-ASCII as-is
  (`UnsafeRelaxedJsonEscaping`), not as `\u` escapes.
- `RunProcessNode` reads stdout and stderr concurrently (reading one to the end first deadlocks once
  the other's pipe fills), treats a program that exits without reading its stdin as normal, and kills
  the whole process tree on timeout or cancellation. It fires every tick in a continuous run.
- Inline secrets (HMAC `Key`, AES `Password`) are ordinary public string properties, so they **are**
  saved in the `.vnoder` file; the editor labels say so, and wiring the Key input keeps them out.
  `AesEnvelope` is this app's own layout (version, salt, nonce, tag, ciphertext) over standard
  AES-256-GCM and PBKDF2-SHA256 (600k iterations); text is a password, 16/24/32 bytes is a raw key.
- Images travel as **encoded bytes** (`byte[]`), never pixels: Nodes cannot reference WPF, so
  `ImageData` (in `vNoder.Nodes/Imaging`) is everything the nodes know about an image without decoding
  it — accepting bytes or Base64 / `data:` text (`FromValue`), the format from the signature
  (`Sniff`), the size from the headers (`Dimensions`, for all seven formats), and the file extensions.
  Decoding to pixels happens only in the view (`BytesToImageConverter`). `ImageNode` (Output/Image) is
  the universal viewer; `ImageLoadNode` / `ImageSaveNode` read and write files.
- Resize, Crop and Rotate Flip derive from `TransformImageNode` and go through
  `ImageConversion.Transform`, which re-encodes in the **source** format with the encoder ImageSharp
  picks for it, so a JPEG keeps its quality and a lossless WebP stays lossless. `Apply` returning
  false (Only Shrink on a small image, a crop covering everything) passes the original bytes through
  untouched rather than re-encoding. Parameters are read in `Capture()` on the evaluation thread;
  `Apply` runs on the thread pool.
- **Conversion is `ImageConversion` (ImageSharp)**, the one place Nodes decodes pixels; `ImageData`
  stays header-only and falls back to ImageSharp only to *detect* and *size* formats it has no
  signature for (TGA, QOI, PBM). TGA is detected heuristically, so ~0.06% of random byte buffers pass
  as one; decoding then fails cleanly. ImageSharp 3.1 cannot read or write **ICO**, so an ICO passes
  through the nodes but cannot be converted.
- Converting to a format without alpha (JPEG, BMP, PBM) **composites onto white** first; otherwise
  transparent pixels come out black. WebP at quality 100 is lossless.
- **Image Save writes the format its extension names**: a matching extension writes the bytes as they
  arrive (no re-encode), a different known one converts, no extension adds the right one, an unknown
  one (or `.ico` from another format) is refused. The target is decided before the if-exists check,
  but conversion runs after it, so `Skip` never pays for an encode. `AddNumber` makes a numbered
  series, which is what a continuous run wants; `Skip` reports `Success = false`.
- `ImageNode.Preview` is what the view shows: the image itself, or a PNG rendering for formats WPF
  cannot be relied on to decode (TGA, QOI, PBM, and WebP, whose WIC codec is absent on N editions and
  Server).
- `ImageNode`'s computed `Bytes` / `ImageFormat` have private setters, so they are never persisted.
- Dropping a wire on empty canvas lists **Output first**, then the other compatible nodes in registry
  order (`RefreshSearchResults`), and pre-selects it, so drag-drop-Enter shows a value.
- `QrCodeNode` keeps its last PNG in a computed `Png` property (non-public setter, so not persisted)
  for the inline preview, shown through `BytesToImageConverter` with nearest-neighbour scaling.
- `PathDeleteNode` (formerly `FileDeleteNode`, "IO/File Delete") is destructive and unconditional — in
  a continuous run it fires every tick. Its `Deleted` output reports whether *this* pass removed
  something, since `File.Delete` is silent about a file that was not there and "nothing to do" should
  not read as success. A non-empty directory needs `Recursive`; a drive root is always refused.
- **Path Delete walks the tree itself** rather than calling `Directory.Delete(path, true)`. Neither
  follows a junction into its target, but .NET's recursive delete fails with "access denied" on
  reaching one and leaves the tree half-deleted; `Directory.Delete(link, false)` removes a junction
  cleanly. So links inside the tree are unlinked, never entered, and a path that is itself a link has
  just the link removed, without needing `Recursive`. Test link handling with a real junction
  (`mklink /J`, no admin needed) and a sentinel file in its target.
- File Write, File Copy, File Move and Image Save create missing parent folders
  (`FilePaths.EnsureDirectory`), and share `ExistingFileMode` and `FilePaths.NextFreeName`. Copy and
  Move are one `FileTransferNode` base differing only in the final call, with one editor template
  declared for the base. Both refuse Overwrite onto the source itself, which would otherwise truncate
  it.
- Path fields are **single-line** `NodeFieldTextBox`es, as in Image Load; a path does not need a
  multi-line, wrapping, resizable box.
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

`RenderTargetBitmap.Render(WorkspaceHost)` keeps the host's own offset in the window (the 40px
toolbar), so sampled pixels sit that far down the bitmap; read `VisualTreeHelper.GetOffset(host)`
rather than assuming the origin. With virtualization on, remember that only realized nodes are in the
visual tree — pan there (and pump the dispatcher) before looking for one. Call async workspace methods
with a `DispatcherSynchronizationContext` installed, or their continuations resume off the UI thread.

Two traps when doing this: a rendered check can pass for the wrong reason (thin strokes antialias
away at low zoom — compare against a baseline), and `mv`/`cp` that preserves an old timestamp will
leave MSBuild convinced the XAML is unchanged, so you measure a stale build. `touch` the file.

Anything about feel rather than geometry — drag inertia, focus order, whether a gesture is
comfortable — still has to be checked by hand with `dotnet run --project src/vNoder.App`.

Commit history starts from the first working POC; `src/`, `README.md`, `TODO.md` and this file are tracked.
