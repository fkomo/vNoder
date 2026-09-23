# vNoder

A node-based visual scripting editor for Windows, in the spirit of Unreal Blueprints or Shader Graph.
Drop nodes on a canvas, wire them together through typed ports, edit their values in place, and run
the graph once or continuously.

## Requirements

- Windows (the UI is WPF)
- [.NET 10 SDK](https://dotnet.microsoft.com/download)

## Build and run

The solution lives in `src/` and uses the newer XML solution format, so pass the path explicitly:

```bash
dotnet run --project src/vNoder.App
```

```bash
dotnet build src/vNoder.slnx
```

## Using it

Double-click empty canvas to open the node catalogue, type to filter, press Enter to place. Drag from
an output port to an input port to wire them up — incompatible ports are refused, and so are
connections that would form a cycle. Drop a wire on empty canvas and the catalogue opens filtered to
nodes that can accept it, then wires the one you pick automatically.

Press **Run Once** to evaluate the graph, or **Continuous** to re-run it on a timer (the interval is
the box beside the button). While a continuous run is going the button turns red and the status bar
counts iterations and elapsed time.

### Controls

| | |
|---|---|
| Add a node | Double-click empty canvas |
| Pan | Right-drag empty canvas, or middle-drag anywhere |
| Zoom | Mouse wheel (zooms about the cursor) |
| Select | Click, Ctrl+click to toggle, or drag a box on empty canvas |
| Move | Drag a node header — moves the whole selection |
| Resize | Drag the corner grip (on nodes that have one) |
| Disconnect | Click a connected input port |
| Delete | `Delete` |
| Group selection | `G`, or right-click a node |
| Collapse a group | Double-click its header |
| Copy / paste | `Ctrl+C` / `Ctrl+V` |
| Run once | `F5` |
| Run continuously | `Ctrl+F5` |
| New / open / save | `Ctrl+N` / `Ctrl+O` / `Ctrl+S`, `Ctrl+Shift+S` to save as |

Groups are a purely visual convenience: a named box you can drag, collapse and rename. Wires crossing
a collapsed group stay drawn, stubbed to its edge, so you can still see it is connected.

## Nodes

| Category | Nodes |
|---|---|
| Input | Static Number, Static Text |
| Generators | Random Number, Random String, Random Bytes, Random Boolean |
| Text | Substring, Replace All, Split, Join, Concat, Count Occurrences, To Upper, To Lower |
| Math | Add, Sqrt |
| Logic | And, Or, Xor, Nand, Boolean, Equals, Compare |
| Crypto | Hash (MD5, SHA-1, SHA-256, SHA-384, SHA-512) |
| IO | File Read, File Write, File Delete |
| Output | Output, Length |

Ports carry one of `number`, `text`, `boolean`, `bytes`, `text list`, or `any`, and wire colour
follows the type. Most node parameters can be either typed in place or driven by a wire — connect one
and the inline field greys out.

Evaluation order comes from the wiring: nodes are sorted by data dependency, and a node with no path
between it and another has no guaranteed order relative to it. A node that fails is reported on the
node itself and does not stop the rest of the pass.

## Files

Graphs save as JSON with a `.vnoder` extension, holding the nodes and their inline values, the wiring,
any groups, and the current zoom and pan. Node types are stored by name (`"type": "Math/Add"`), so the
file stays readable. A file that references something this build does not have still opens — the parts
that could not be restored are reported and the rest loads.

## Project layout

- `src/vNoder.Core` — graph model, port types, node registry, execution engine. No UI dependency.
- `src/vNoder.Nodes` — the node implementations.
- `src/vNoder.App` — the WPF application.

Adding a node type is one class with one attribute; the registry finds it by reflection and it
appears in the catalogue, with its editable properties saved and loaded automatically. See
[CLAUDE.md](CLAUDE.md) for the conventions that go with that.

## Status

A working proof of concept. It has no undo, and the canvas is drawn with plain WPF shapes rather than
a virtualised surface, which is comfortable into the hundreds of nodes rather than thousands.
