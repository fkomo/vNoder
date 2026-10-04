# Nodes: adding and changing node types

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

**Every new node comes with a sample.** Use it in a graph in `samples/`, doing a small real job, not
sitting alone on the canvas: add it to the existing sample it fits (05 site monitor, 06 data, 07 text,
08 security, 09 numbers and dates, 10 files, images and the system, 11 command line), or add a new numbered sample
when it opens a new area. Describe what it shows **in the sample itself**, with notes: each sample
opens with a Note node explaining it, and linked notes on the nodes or groups worth pointing at (there
is no samples README any more; the explanation lives next to what it explains). Between them the samples use
every node type, and that is worth keeping: they are how a user sees what a node is for, and loading
and running them is the cheapest regression check there is (see [verifying.md](verifying.md)).

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
- Inline state is saved and loaded automatically (see **Persistence** in [engine.md](engine.md)). Nothing to register, but it
  does mean a public settable property of a simple type *is* persisted state — give anything that
  evaluation computes a non-public setter, as `OutputNode.Value` and `LengthNode.Count` do.
- **Never name an inline property after a `NodeBase` member**: `Size`, `Title`, `Position`, `Id`,
  `Category`, `Inputs`, `Outputs`. It compiles with only a CS0108 warning, but `Size` is the node's
  saved geometry and `Title` its header text, so hiding them breaks the canvas or the file. Use
  `ChunkSize`, `FontSize`, `Caption` (the port can still be called "Size" or "Title"). Class names are
  global too: `Math/Sign` is `SignNode` and `Math/Log` is `LogarithmNode`, so the crypto and output
  ones are `SignDataNode` and `LogNode`.
- A node whose outputs describe a shared thing declares them through a small ports class built with
  its own `AddOutput` (`CertificateInfoPorts`, shared by Certificate Load and TLS Certificate), so the
  two read the same without a base class.

## Node catalogue notes

What an individual node does and why is documented on its class. What follows applies across nodes.

- **"Any" (`object`) is permissive in both directions.** An `any` input accepts every output, and an
  `any` output feeds every input — its real type is only known at run time, so the check is deferred
  to evaluation, where `ValueOr` falls back instead of failing. Without the second half, `OutputNode`'s
  pass-through port could only ever feed another `any` input, which made it useless.
- **Port types** are `double`, `string`, `bool`, `byte[]`, `string[]` (a text list, produced by
  Split, Lines, File Search, Regex Extract All and JSON Get on an array, consumed by Join and the List
  nodes), `DateTime` ("date/time", from
  `DateTimeNode` to `FormatDateTimeNode`) and `object` for "any". A node running per item also
  carries **any array but `byte[]`** as a list (`double[]`, `bool[]`, `string[][]`, `object[]`; see
  **Per-item evaluation** in [engine.md](engine.md)): `PortTypeCompatibility.IsList` is the rule, and `byte[]` is one value, never
  a list of numbers. Names come from `PortTypeCompatibility.Describe` (`Graph.FriendlyTypeName` and
  `PortTypeNames.For` both delegate to it; lists read "number list"), and every list is teal. Adding a
  type means touching: `PortTypeCompatibility.Describe`, `PortBrushConverter` for its wire colour, and
  usually `NodeValues.ToText` / `LengthNode` so it can be displayed and measured — plus
  `EqualsNode.AreEqual` / `Text` and `CompareNode` if the type has a natural order (dates compare as
  dates, and display as ISO 8601).
- There is **no JSON or image port type**. JSON travels as text; images travel as encoded bytes
  (`byte[]`), never pixels, because Nodes cannot reference WPF. `ImageData` reads format and size
  from headers, `ImageConversion` (ImageSharp) is the one place Nodes decodes pixels, and the view
  decodes for display (`BytesToImageConverter`).
- A new **category** gets its header colour in `CategoryBrushConverter`; one without an entry falls back
  to grey.
- **Select picks a value; it does not skip work by itself.** Skipping a branch is Run If's job (see
  **Execution model** in [engine.md](engine.md)); Select, Coalesce, Is Empty and Output are declared `Merges = true`, so they
  still run when one side is skipped and read it as empty.
- Nodes with side effects (file writes and deletes, zips, HTTP Request, Download To File, Send Email,
  Run Process, Process Kill, Image Save, Clipboard Set, Notify, CLI Write) **fire on every tick** of a continuous
  run. Changed into Run If is the answer to "only when something happens"; say so in the node's comment
  when it is one a user would typically want gated.
- Text nodes **clamp rather than throw** on out-of-range arguments (`SubstringNode`) and treat a
  degenerate argument as a no-op (`ReplaceAllNode` with an empty Find, `SplitNode` with an empty
  separator, an empty regex pattern). A graph running continuously would otherwise sit in an error
  state for as long as a bound is mid-edit.
- Display nodes (`OutputNode`, `LengthNode`) render into a **read-only `TextBox`**, not a `TextBlock`,
  so the value can be selected and copied. WPF `TextBlock` has no text selection.
- Path fields are **single-line** `NodeFieldTextBox`es, as in Image Load; a path does not need a
  multi-line, wrapping, resizable box.
- Dropping a wire on empty canvas lists **Output first**, then the other compatible nodes in registry
  order (`RefreshSearchResults`), and pre-selects it, so drag-drop-Enter shows a value.

