using System.Text.Json;
using vNoder.Cli;
using vNoder.Core;
using vNoder.Nodes;

// Runs a saved graph once and exits: vnoder-cli <graph.vnoder> [--name value ...]
//
// Standard output belongs to the graph (CLI Write); everything vnoder-cli says itself (load warnings,
// failed nodes) goes to standard error, so a script can capture one without the other. The one
// exception is the parameter list, which is asked for and so is the output.
//
// Parameters are the graph's CLI Param nodes; --help after the file lists them without running.
//
// Exit codes: whatever an Exit Code node set; otherwise 1 if a node failed, 0 if none did. 2 means the
// graph could not be run at all (bad arguments, unreadable file), 130 that it was cancelled (Ctrl+C).

const int UsageError = 2;
const int Cancelled = 130;

if (args is [] || args[0] is "-h" or "--help" or "/?")
{
    var help = args is [_, ..];
    (help ? Console.Out : Console.Error).WriteLine(
        """
        Usage: vnoder-cli <graph.vnoder> [--name value ...]

        Runs the graph once, as Run Once does in the editor, then exits.

        The graph's CLI Param nodes are its parameters, given as --name value or --name=value; a
        name given with no value is "true". Put --help after the file to list a
        graph's parameters without running it.

        CLI Write nodes write to standard output (or error); vnoder-cli's own messages go to
        standard error. The exit code is the one set by an Exit Code node; without one it is 0,
        or 1 if a node failed. 2 means the graph could not be run (unreadable file, missing or
        unknown parameters), 130 that it was cancelled.
        """);
    return help ? 0 : UsageError;
}

var path = args[0];
var fileName = Path.GetFileName(path);

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    // Cancel the run rather than kill the process, so a node mid-write can stop cleanly.
    e.Cancel = true;
    cancellation.Cancel();
};

GraphDocument document;
try
{
    document = await GraphFile.LoadAsync(path, cancellation.Token);
}
catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
{
    Console.Error.WriteLine($"vnoder-cli: cannot read {path}: {exception.Message}");
    return UsageError;
}

var registry = new NodeRegistry().ScanAssemblyOf<OutputNode>();
var graph = new Graph();
var restore = GraphRestore.Into(graph, document, registry);
foreach (var warning in restore.Warnings)
    Console.Error.WriteLine($"vnoder-cli: warning: {warning}");

var declared = Parameter.FromGraph(graph);

if (args is [_, "-h" or "--help" or "/?"])
{
    Parameter.Print(Console.Out, fileName, declared);
    return 0;
}

if (!Parameter.TryParse(args[1..], out var given, out var parseError))
{
    Console.Error.WriteLine($"vnoder-cli: {parseError}");
    return UsageError;
}

var unknown = given.Keys.Where(name => !declared.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))).ToList();
var missing = declared.Where(p => p.Required && !given.ContainsKey(p.Name)).ToList();
if (unknown.Count > 0 || missing.Count > 0)
{
    foreach (var name in unknown)
        Console.Error.WriteLine($"vnoder-cli: unknown parameter --{name}");
    foreach (var parameter in missing)
        Console.Error.WriteLine($"vnoder-cli: missing required parameter --{parameter.Name}");

    Console.Error.WriteLine();
    Parameter.Print(Console.Error, fileName, declared);
    return UsageError;
}

var host = new CliNodeHost(given);
var executor = new GraphExecutor(graph) { Host = host };

GraphRunResult result;
try
{
    result = await executor.RunOnceAsync(cancellation.Token);
}
catch (OperationCanceledException exception)
{
    // The executor lets cancellation end the pass rather than fail one node; a timeout inside a node
    // (an HTTP request) looks the same, so tell the two apart.
    if (cancellation.IsCancellationRequested)
    {
        Console.Error.WriteLine("vnoder-cli: cancelled.");
        return Cancelled;
    }

    Console.Error.WriteLine($"vnoder-cli: the run was aborted: {exception.Message}");
    return 1;
}

foreach (var node in result.Failed)
    Console.Error.WriteLine($"vnoder-cli: {node.Title} failed: {node.LastError}");

if (cancellation.IsCancellationRequested)
{
    Console.Error.WriteLine("vnoder-cli: cancelled.");
    return Cancelled;
}

return host.ExitCode ?? (result.HasErrors ? 1 : 0);
