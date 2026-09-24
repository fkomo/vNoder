# TODO

Two engine changes the node catalogue is now outgrowing. Neither is a node: both change what the
executor does, so they are worth designing before building. Order matters — the second is easier once
the first exists.

## 1. Ordering side effects (and skipping work)

### The problem

`GraphExecutor` orders nodes by data dependency and nothing else (Kahn's algorithm over the links). Two
nodes with no wire between them run in either order, which is fine for pure nodes and wrong for
side effects:

- **File Write → File Read** of the same path: `Success` (bool) cannot feed `Path` (string), so there is
  no wire to express "after", and the read may see the old file.
- **Create Directory → File Search** in it, **File Move → Image Load** from the new place, **Run
  Process** (a build) → **File Read** of its output. The same shape each time.
- **Select does not skip work.** Both branches feeding it are evaluated on every pass, side effects
  included. "Only upload when the hash changed" cannot be built today.
- **A failed node does not stop what depends on it.** Downstream nodes still run and see its outputs,
  which are null if the node cleared them before throwing and the *previous pass's values* if it did
  not. Nodes are inconsistent about clearing, so what downstream sees after a failure is effectively
  undefined.

### Option A — a signal port (recommended first step)

Add a port type that carries no data, only "this happened", e.g. `Signal` (a small record, or `bool`
in a new wrapper type so it cannot be confused with a real boolean).

- Side-effect nodes opt in to a `Done` output and an `After` input, declared on a shared base or with a
  helper (`AddSequencePorts()`), so it stays one class per node. Pure nodes do not get them.
- **Ordering is free**: a wire from `Done` to `After` is an ordinary link, so Kahn's algorithm already
  honours it. No executor change is needed for sequencing alone.
- **Skipping** is the real executor change: when a node's `After` input is wired and the signal says
  "did not happen" (upstream failed, was skipped, or reported `Success = false`), the node is *skipped*.
  It is not evaluated, its outputs are cleared, and the skip propagates to everything downstream of it.
  That gives both "stop on failure" and conditional execution: a **Gate** node (bool in → signal out)
  turns a condition into a signal.
- A node without `After` wired behaves exactly as today, so existing graphs are unchanged.

Touch points: the four places a port type needs (`Graph.FriendlyTypeName`, `PortTypeNames.For`,
`PortBrushConverter`, `OutputNode.Format` / `LengthNode`); a skipped state in `GraphExecutor` and
`GraphRunResult`; `NodeRunPhase.Skipped` so Run Once feedback can show it (a grey flash rather than
none, so the reason a node did nothing is visible); a distinct port glyph (a triangle rather than a dot)
so signal wires read as control, not data.

### Option B — execution-flow wires (Blueprint style)

Separate exec pins and wires. Impure nodes run only when their exec input fires; pure nodes are pulled
on demand; control nodes (Branch, Sequence, For Each, While) direct the flow.

- Gives real branching and loops, and is the model people know from Unreal.
- It is a different executor: an interpreter walking exec edges, with pull evaluation and caching for
  pure nodes, instead of one topological pass. The view needs exec pins, and every existing
  side-effect node needs to become impure.

### Decisions to make

- A or B? A reuses the executor and can grow toward B: the signal type is B's exec wire in all but name.
- Should a failed node **always** skip its data dependents, even without signal wiring? It would make
  failures well-defined, but it changes the behaviour of existing graphs.
- Should `Done` fire when a node ran but reported `Success = false` (File Copy with Skip, HTTP 404)?
  Probably not by default, perhaps as a per-node option.
- Should nodes clear their outputs on failure? Either way, the executor should enforce it rather than
  leaving it to each node.

## 2. Doing something per item in a list

### The problem

Nodes produce lists (File Search, Lines, Split, Regex Extract All, JSON arrays), and the List nodes
reshape them, but there is no way to run a *chain* of nodes once per item: resize every image File
Search found, hash each file, call an API per line. Today the only way out is Run Process with a
script.

### Option A — a sub-graph node (recommended)

A **For Each** node that contains its own graph. Inside, an **Item** source (with the item and its
index) feeds the chain, and a **Collect** sink gathers one result per item. The node's outputs are
the collected lists.

- The executor runs the inner graph once per item, reusing `GraphExecutor` as it is: the inner graph
  is an ordinary `Graph`.
- The same mechanism gives **user-defined nodes** (macros) almost for free: a sub-graph with named
  inputs and outputs, saved and reused.
- Costs: nested graphs in `GraphDocument` and `GraphRestore` (persistence, and undo, which snapshots
  the whole document, so it follows automatically); a UI for editing the inner graph (open it on its
  own canvas with a breadcrumb back); copy and paste across levels.

### Option B — a loop region

Mark a group box as **For Each**. Nodes inside run per item, a list wired in from outside becomes the
item, and wires leaving the box carry lists.

- Probably the fastest to use, and it reuses the group UI.
- It breaks the rule that groups are purely presentational (`CLAUDE.md`: the executor never looks at
  groups). Dragging a node in or out would change what the graph computes, which is easy to do by
  accident.

### Option C — implicit per-item evaluation

Wiring a list into a single-value input runs the node once per item and turns its outputs into lists,
as Grasshopper does.

- No new UI at all.
- It fights the static port types: a `number` output would sometimes carry a list of numbers, which
  there is no type for. Every type would need a list variant, or every such output would degrade to
  `any`.

### Decisions to make

- Which list types exist? Today there is only `string[]`. Collecting numbers, booleans, dates or images
  (`byte[]`) per item needs either a list type per element type or a generic list. That touches the
  four port-type places, plus `EqualsNode`, `CompareNode` and the List nodes.
- Errors per item: fail the whole For Each, skip the item, or collect an error list next to the
  results? The signal work in part 1 answers much of this.
- Parallelism: run items concurrently with a limit? That is safe for pure chains, but not for side
  effects writing the same file.
- Run Once feedback: flash per iteration, or once for the whole node with a progress count?
- Continuous mode: a long loop holds the pass open, and ticks are already skipped rather than queued,
  so that is probably fine, but cancellation must reach inside each iteration.
