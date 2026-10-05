using System.Diagnostics;
using vNoder.Core;

namespace vNoder.Nodes;

/// <summary>
/// Opens a URL, file or folder the way double-clicking it would: a web page in the browser, a report
/// in its viewer, a folder in Explorer. It is the natural last step of a graph that writes a file.
/// </summary>
/// <remarks>
/// A path that does not exist throws, rather than letting the shell show its own error dialog. Like
/// every node with side effects it fires on every tick of a continuous run, which here means a new
/// window each time: gate it with Changed or Every.
/// </remarks>
[NodeDefinition("System/Open", Description = "Opens a URL, file or folder with its default program (browser, viewer, Explorer); gate it in continuous runs.")]
public sealed class ShellOpenNode : NodeBase
{
    private string _target = string.Empty;

    private readonly InputPort _targetIn;

    public ShellOpenNode()
    {
        _targetIn = AddInput<string>("Target");

        AddRunGate();
    }

    /// <summary>A URL (http, https, mailto …) or a file or folder path.</summary>
    public string Target { get => _target; set => Set(ref _target, value ?? string.Empty); }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var target = (_targetIn.IsConnected ? _targetIn.ValueOr(string.Empty) : Target).Trim().Trim('"');
        if (target.Length == 0)
            throw new InvalidOperationException("Nothing to open.");

        // A drive-letter path parses as a URI with a one-letter scheme, so only longer schemes count.
        var isUrl = Uri.TryCreate(target, UriKind.Absolute, out var uri) && !uri.IsFile && uri.Scheme.Length > 1;
        if (!isUrl)
        {
            target = Path.GetFullPath(Environment.ExpandEnvironmentVariables(target));
            if (!File.Exists(target) && !Directory.Exists(target))
                throw new FileNotFoundException($"'{target}' does not exist.");
        }

        using var process = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        return Task.CompletedTask;
    }
}
