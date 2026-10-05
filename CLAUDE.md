# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

`vNoder` is a node-based visual scripting editor (Blueprint / Shader-Graph style): a zoomable,
pannable canvas where nodes are added by double-clicking and searching, wired through typed ports,
edited inline in the node body, and run either once or continuously.

`README.md` is the user-facing description: what it does, the controls, and the node catalogue.
This file and the guides in `docs/dev/` are the part that is not obvious from reading the code — the
invariants, and the traps that have already cost time.

## Build and run

The solution is one level down, in `src/`, in the newer XML solution format (`.slnx`, not `.sln`) —
pass the path explicitly, since some tooling still looks for `*.sln`:

```bash
dotnet build src/vNoder.slnx
```

```bash
dotnet run --project src/vNoder.App
```

```bash
dotnet run --project src/vNoder.Cli -- samples/11-command-line.vnoder --name Ada
```

Requires the .NET 10 SDK. The editor builds and runs on Windows only (WPF); the command-line runner
is plain `net10.0`.

NuGet versions are managed centrally in `src/Directory.Packages.props`: a project's
`<PackageReference>` has no `Version`, and adding or upgrading a package means editing that one file.

Settings shared by every project live in `src/Directory.Build.props`. Today that is only
`DebugType=embedded`: symbols are compiled into each DLL rather than written as `.pdb` files, so any
copy of a build — including Release — gives stack traces with file and line numbers. Do not set
`DebugType` in an individual project file.

## Projects

- `src/vNoder.Core` — plain `net10.0`: graph model, port/type system, node registry, execution
  engine. **No WPF reference**, so it can be exercised without a UI host.
- `src/vNoder.Nodes` — plain `net10.0`, references Core only: the node implementations. Its NuGet
  dependencies, all pure managed code, are `BCrypt.Net-Next`, `QRCoder`, `SixLabors.ImageSharp` and
  `.Drawing`, `ZXing.Net` (barcodes), `Cronos` (cron), `MailKit` (SMTP), `Markdig`, `YamlDotNet`,
  `AngleSharp` (HTML parsing, for HTML Select) and `System.Security.Cryptography.ProtectedData`
  (Windows DPAPI, for Secret; it throws elsewhere, and the node says so), each for one thing the BCL
  genuinely lacks; prefer the BCL, and add a package only on the same
  grounds. The README lists them with their licenses — keep that table in step.
- **ImageSharp is pinned to 3.1.x — do not upgrade to 4.x casually.** 4.x adds a build-time license-key
  check (`SixLabors_ValidateLicense`) that only warns in Debug but **fails Release builds** without a
  key. It is also under the Six Labors Split License (free for open source, small for-profits and
  non-profits; commercial otherwise), which is the project owner's call, not a technical one.
  `SixLabors.ImageSharp.Drawing` is pinned to 2.1.x for the same reason: 3.x requires ImageSharp 4.
  Barcode Read hands ZXing raw RGB24 pixels, so no ZXing-to-ImageSharp bindings package is needed.
- `src/vNoder.App` — WPF (`net10.0-windows`), references Core and Nodes. Only new NuGet dependency
  is `CommunityToolkit.Mvvm`.
- `src/vNoder.Cli` — plain `net10.0` exe, `vnoder-cli`, references Core and Nodes: loads a `.vnoder`
  file, runs it once, and exits. Standard output belongs to the graph (CLI Write); the runner's own
  messages (load warnings, failed nodes) go to standard error. Parameters are the graph's CLI Param
  nodes (`Parameter.cs`), given as `--name value`; `--help` after the file lists them without
  running. Exit code: whatever an Exit Code
  node set, else 1 if a node failed, else 0; 2 when the file cannot be read or a parameter is unknown
  or missing, 130 on Ctrl+C. Its `CliNodeHost` carries the exit code and the parameters, and refuses
  the clipboard and notifications. Named `vnoder-cli`, not `vNoder`, because that is the editor's
  executable.

Keep graph and node logic out of `vNoder.App` and `vNoder.Cli`. That split is what keeps it
testable, and a WPF assembly cannot be referenced from a cross-platform test project.

## Guides by kind of work

The detail is split by the kind of change, so read the guide for the work at hand **before** starting
it; a change that spans several kinds needs each of them.

| Work | Read |
|---|---|
| Adding a node, or changing a node's ports, inline state, editor template or behaviour; port types; categories | [docs/dev/nodes.md](docs/dev/nodes.md) |
| Execution order, Run If / Done and skipping, continuous runs, per-item evaluation, the file format and save/load, copy/paste, undo/redo | [docs/dev/engine.md](docs/dev/engine.md) |
| The canvas (pan/zoom, wires, virtualization), node and group visuals, selection, resizing, groups, notes, theming, dialogs, window lifetime | [docs/dev/ui.md](docs/dev/ui.md) |
| Checking any change: the console harness, the sample checks, headless rendering, binding-error checks, screenshots | [docs/dev/verifying.md](docs/dev/verifying.md) |

Adding a node usually touches the UI guide too: its editor is a `DataTemplate` in `MainWindow.xaml`, and
a control type that has not appeared in a node before needs an implicit style in `App.xaml`.

There is no test project; changes are verified with a throwaway console harness and the samples in
`samples/`, as described in the verifying guide.

Commit history starts from the first working POC; `src/`, `docs/`, `samples/`, `README.md` and this
file are tracked.
