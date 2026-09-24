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
nodes that can accept it, with Output at the top, then wires the one you pick automatically.

Press **Run Once** (`Ctrl+R`) to evaluate the graph. Each node flashes as it finishes, in the order
the graph evaluated it — green, or red if it failed — and a node that takes a while, such as an HTTP
request, is outlined in amber while it runs. **Continuous** (`Ctrl+Shift+R`) re-runs it on a timer
(the interval is the box beside the button), without the flashes. While a continuous run is going the button turns red and the status bar
counts iterations and elapsed time.

### Controls

| | |
|---|---|
| Add a node | Double-click empty canvas |
| Pan | Right- or middle-drag empty canvas |
| Zoom | Mouse wheel over empty canvas (zooms about the cursor) |
| Select | Click, Ctrl+click to toggle, or drag a box on empty canvas |
| Move | Drag a node header — moves the whole selection |
| Resize | Drag the corner grip (on nodes that have one) |
| Disconnect | Click a connected input port |
| Delete | `Delete` |
| Group selection | `G`, or right-click a node |
| Collapse a group | Double-click its header |
| Copy / paste | `Ctrl+C` / `Ctrl+V` |
| Undo / redo | `Ctrl+Z` / `Ctrl+Y` (or `Ctrl+Shift+Z`), or the toolbar buttons |
| Run once | `Ctrl+R` |
| Run continuously | `Ctrl+Shift+R` |
| New / open / save | `Ctrl+N` / `Ctrl+O` / `Ctrl+S`, `Ctrl+Shift+S` to save as |

Everything that is saved can be undone: adding, deleting, wiring, moving, resizing, grouping, pasting,
inline values and the run interval. A drag is one step however far it goes, and typing into a node
becomes one step once you pause or move focus. While a text box has focus, `Ctrl+Z` undoes its typing
first. Undoing back to the last save clears the unsaved-changes marker.

Groups are a purely visual convenience: a named box you can drag, collapse and rename. Wires crossing
a collapsed group stay drawn, stubbed to its edge, so you can still see it is connected.

## Nodes

| Category | Nodes |
|---|---|
| Input | Static Number, Static Text |
| Generators | Random Number, Random String, Random Bytes, Random Boolean, GUID (v4 / v7), Date Time |
| Text | Substring, Replace All, Split, Join, Concat, Count Occurrences, To Upper, To Lower, Lines, Trim, Pad, Contains (starts / ends with, equals, regex), Template, Regex Match, Regex Replace, Regex Extract All |
| List | Count, Item At, First, Last, Sort (text, natural, numeric), Distinct, Reverse, Filter, Slice |
| Convert | To Number, To Text, To Boolean |
| JSON | Get (by path, e.g. `data.items[0].name`), Set, Format (indent / minify / validate) |
| Date | Parse, Format, Add, Difference, To Unix, From Unix |
| Math | Add, Subtract, Multiply, Divide, Modulo, Power, Min, Max, Abs, Negate, Sign, Floor, Ceiling, Round, Clamp, Sqrt, Exp, Ln, Log10, Sin, Cos, Tan, Asin, Acos, Atan, Constant (π, τ, e) |
| Logic | And, Or, Xor, Nand, Not, Boolean, Equals, Compare, Select (if / else), Is Empty, Coalesce |
| Encoding | Base64 Encode / Decode, URL Encode / Decode, Hex Encode / Decode, HTML Escape / Unescape, Compress / Decompress (GZip, Deflate, ZLib, Brotli), JWT Decode, QR Code |
| Crypto | Hash (MD5, SHA-1, SHA-256, SHA-384, SHA-512, CRC32), HMAC, AES Encrypt / Decrypt, BCrypt Hash, BCrypt Verify, PBKDF2 |
| IO | File Read, File Write, File Copy, File Move, Path Delete (files and directories), Path Exists, File Info, Create Directory, File Search, Directory Size, Image Load, Image Save |
| Path | Path Parts, Combine, Change Extension |
| System | Environment Variable, Run Process |
| Network | HTTP Request (GET, POST, PUT, PATCH, DELETE) |
| Image | Convert (to PNG, JPEG, WebP, GIF, BMP, TIFF, TGA, QOI, PBM), Resize (fit, fill, stretch, pad; thumbnails), Crop, Rotate Flip (and EXIF auto-orient) |
| Output | Output, Length, Image (PNG, JPEG, GIF, BMP, TIFF, ICO, WebP, TGA, QOI, PBM, or Base64 text) |

Ports carry one of `number`, `text`, `boolean`, `bytes`, `text list`, `date/time`, or `any`, and wire
colour follows the type. Most node parameters can be either typed in place or driven by a wire — connect one
and the inline field greys out.

Evaluation order comes from the wiring: nodes are sorted by data dependency, and a node with no path
between it and another has no guaranteed order relative to it. A node that fails is reported on the
node itself and does not stop the rest of the pass.

Nodes with side effects — File Write, File Copy, File Move, Path Delete, Create Directory, Image Save,
HTTP Request and Run Process — act on every pass, so in a continuous run they fire on every tick. An
HTTP error status (404, 500) is a result, reported on the node's Status and Success outputs along with
the body, not a failure. The same goes for a value that does not parse (To Number, Parse, JSON Get on a
missing path): the node reports it on a Success or Found output rather than failing, so it can drive a
Select.

Select picks a value; it does not skip work. Every node runs on every pass, so both branches feeding a
Select are evaluated, side effects included.

JSON travels as text, so HTTP Request and File Read wire straight into JSON Get, and a JSON array comes
out as a text list for the List nodes. Run Process is killed, with anything it started, when its timeout
passes or a continuous run is stopped.

Inline values are saved in the graph file, including the HMAC key and AES password fields. To keep a
secret out of the file, wire it in instead, from Environment Variable for example.

## Files

Graphs save as JSON with a `.vnoder` extension, holding the nodes and their inline values, the wiring,
any groups, and the current zoom and pan. Node types are stored by name (`"type": "Math/Add"`), so the
file stays readable. A file that references something this build does not have still opens — the parts
that could not be restored are reported and the rest loads.

File Write, File Copy, File Move and Image Save create any missing folders in the path. The
destination of File Copy and File Move may be a file path or an existing folder to put the file in, and
like Image Save they can overwrite, skip or number an existing file. Path Delete removes a file or an
empty directory; a directory with contents is deleted only with Recursive ticked, links (junctions,
symbolic links) are removed without touching what they point at, and a drive root is always refused.

Image Save writes in the format its file extension names, converting when the image is in a
different one (a PNG saved as `photo.jpg` becomes a JPEG); leave the extension off to keep the format.
Converting a transparent image to JPEG, BMP or PBM, which have no transparency, puts it on white.
Resize, Crop and Rotate Flip keep the image's format (and a JPEG's quality); follow them with Convert
to change it.

## Project layout

- `src/vNoder.Core` — graph model, port types, node registry, execution engine. No UI dependency.
- `src/vNoder.Nodes` — the node implementations.

Image conversion uses [ImageSharp](https://github.com/SixLabors/ImageSharp), which is under the
Six Labors Split License: Apache 2.0 for open-source use, for-profits under 1M USD annual revenue and
non-profits, and a commercial license otherwise. It is pinned to 3.1 (see `src/Directory.Packages.props`).
- `src/vNoder.App` — the WPF application.

Adding a node type is one class with one attribute; the registry finds it by reflection and it
appears in the catalogue, with its editable properties saved and loaded automatically. See
[CLAUDE.md](CLAUDE.md) for the conventions that go with that.

## Status

A working proof of concept. The canvas is virtualized: only nodes near the viewport have visuals, and
wires are drawn as lightweight visuals culled to the viewport, so graphs in the thousands of nodes
open in a fraction of a second and stay responsive to pan and zoom.
