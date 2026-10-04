# Verifying changes


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

The `.vnoder` files in `samples/` (each explained by the notes inside it) are real saved graphs: 01–04
cover Run If / Done, skipping and merges, and 05–11 are self-contained showcases that together use
every node type. The checks apply to the numbered samples only; other files in `samples/` are
scratch graphs and may be stale. Three cheap checks after changing ports, node paths, the file format
or adding a node: load every numbered sample through `GraphFile` + `GraphRestore.Into` and expect zero
warnings; collect the node types they use and expect every registry definition among them; and run
05–10 with `GraphExecutor.RunOnceAsync`, expecting no failure but Notify (it needs the app's host) and
no skip but the nodes switched off on purpose (Send Email, Process Kill, Clipboard Set and Get).
Sample 11 needs a host for its Exit Code, so run it with `vnoder-cli` instead: with `--help` it
lists its one parameter and exits 0 without running; with or without
`--name Ada`, expect two lines on standard output and exit code 0 (3 with `TEMP` pointing nowhere).

`vnoder-cli` is also the quickest way to run any saved graph end to end without the editor: its exit
code is 1 when a node failed, and the failures are listed on standard error. If a change
renames something the samples use, update them too; editing a sample by hand in the app and saving is
fine, since node positions and inline values are all a file holds.

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

**Catch binding errors instead of eyeballing templates.** Add a `TraceListener` to
`PresentationTraceSources.DataBindingSource` (level `Warning`) before constructing the window, add
every registry definition to the workspace a batch at a time in view (so each editor is realized),
and assert nothing was logged. A misspelt property in an editor template only ever shows up there;
it neither throws nor fails the build. The editors for the batch of nodes added with Flow and Data
were generated from a spec by a scratch script and checked this way.

The clipboard is unusable in this session (`CLIPBRD_E_CANT_OPEN` even for a direct call), so the
clipboard nodes can only be checked for failing cleanly here; check the round trip by hand.

Two traps when doing this: a rendered check can pass for the wrong reason (thin strokes antialias
away at low zoom — compare against a baseline), and `mv`/`cp` that preserves an old timestamp will
leave MSBuild convinced the XAML is unchanged, so you measure a stale build. `touch` the file.

The README screenshots in `docs/screenshots/` were made the same way, not by hand: load a sample (or
build a small graph through the workspace), Run Once and wait out the flash cascade (~1.8 s of
pumping), clear selection, focus and hover, fit the graph to the workspace at no more than 100%, and
render the window's **content** (not the window, whose frame renders white) at 144 DPI. Re-shoot them
after a visible change to nodes, wires or chrome.

Anything about feel rather than geometry — drag inertia, focus order, whether a gesture is
comfortable — still has to be checked by hand with `dotnet run --project src/vNoder.App`.
