# TODO

## Doing something per item in a list

### The problem

Nodes produce lists (File Search, Lines, Split, Regex Extract All, JSON Get on an array), and the List
nodes reshape them (Filter, Sort, Slice, Item At, ...). But there is no way to run a *chain* of nodes
once per item: resize every image File Search found, hash each file, call an API per line. Today the
only way out is Run Process with a script, or Item At with a hand-set index.

### What already exists to build on

- **Run If / Done and skipping** (`NodeBase.AddRunGate`, `GraphExecutor`). Inside an iteration, the
  outside-world nodes can already be ordered (Done → Run If) and gated, and a failure already skips
  just what depends on it. A failed or gated-off iteration can therefore simply be a *skipped
  iteration*, with no new error model.
- **Merge nodes** (`Merges = true`: Select, Coalesce, Is Empty, Output) already give defined
  behaviour when part of a chain is skipped, so a per-item result can fall back rather than
  disappear.
- **`GraphExecutor` is self-contained over one `Graph`**, with an optional per-node observer
  (`NodeRunPhase`, including `Skipped`), so an inner graph can be run by the same class.
- **Undo snapshots the whole document** and **persistence is reflective**, so anything that nests
  inside `GraphDocument` is undoable and saved without per-feature code.

### Option A — a sub-graph node (recommended)

A **For Each** node that contains its own graph. Inside, an **Item** source (the item and its index)
feeds the chain, and a **Collect** sink gathers one result per item. The node's outputs are the
collected list, plus a count of the items that were skipped or failed.

- The executor runs the inner graph once per item with the existing `GraphExecutor`, since the inner
  graph is an ordinary `Graph`.
- The same mechanism gives **user-defined nodes** (macros) almost for free: a sub-graph with named
  inputs and outputs, saved and reused.
- Costs: nested graphs in `GraphDocument`, `GraphFile` and `GraphRestore` (including `Reconcile`,
  which undo uses to restore in place); a UI for editing the inner graph (its own canvas with a
  breadcrumb back, which also has to work with the `NodeRealizer` virtualization); and copy and paste
  across levels.

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

- **List types.** Today there is only `string[]` (text list). Collecting numbers, booleans, dates or
  images (`byte[]`) per item needs either a list type per element type or a generic list. A new port
  type touches `Graph.FriendlyTypeName`, `PortTypeNames.For`, `PortBrushConverter`,
  `NodeValues.ToText` / `LengthNode`, plus `EqualsNode`, `CompareNode` and the List nodes. The cheapest
  first step is to collect text only, via `NodeValues.ToText`.
- **Skipped and failed items.** Leave them out of the collected list, or keep a placeholder so indexes
  still line up with the input? Report them on outputs (Skipped count, Errors list), and don't fail the
  For Each node itself, which matches "a value that does not parse is a result".
- **Parallelism.** Run items concurrently with a limit? That is safe for pure chains, but not for side
  effects writing the same file. Possibly sequential by default, parallel as an option.
- **Run Once feedback.** Flash per iteration (a long list would flash for a long time), or once for
  the whole node with a progress count?
- **Continuous mode.** A long loop holds the pass open, and ticks are already skipped rather than
  queued, so that is probably fine, but cancellation must reach inside each iteration. Every
  outside-world node already honours the context's token.
- **Run If on For Each itself.** Should the For Each node have its own Run If / Done
  (`AddRunGate`), so a whole loop can be sequenced after, say, a Create Directory? Probably yes, since
  its body may touch the outside world.
