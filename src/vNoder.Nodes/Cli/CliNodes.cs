using vNoder.Core;

namespace vNoder.Nodes;

/// <summary>Which of the process's output streams <see cref="CliWriteNode"/> writes to.</summary>
public enum ConsoleStream
{
    StandardOutput,
    StandardError,
}

/// <summary>
/// Writes text to the process's standard output (or error), which is how a graph run by vnoder-cli
/// reports back to the shell or script that started it. It writes to the console directly, not through
/// the host: in the editor there is usually no console attached and the text goes nowhere, so the text
/// written is also passed on, for an Output node to show while the graph is being built.
/// </summary>
/// <remarks>
/// Writes on every pass, like any node with side effects; under vnoder-cli a graph runs once, so that
/// is once. A list is written whole, formatted as <see cref="NodeValues.ToText"/> formats it; tick
/// "For each item" on the input to make one write per item instead.
/// <para>
/// With Log ticked, every line written starts with the local date and time to the millisecond
/// (<c>2026-10-08 14:03:05.123</c>), as a log file's lines do; a multi-line value gets one per line.
/// Text passes on what was written, timestamps included.
/// </para>
/// </remarks>
[NodeDefinition("CLI/CLI Write", Description = "Writes text (or any value, as text) to standard output or standard error, for graphs run by vnoder-cli.")]
public sealed class CliWriteNode : NodeBase
{
    private string _text = string.Empty;
    private bool _newLine = true;
    private ConsoleStream _stream;
    private bool _log;

    private readonly InputPort _in;
    private readonly OutputPort _out;

    public CliWriteNode()
    {
        _in = AddInput("Value", PortTypeCompatibility.Any);
        _out = AddOutput<string>("Text");

        AddRunGate();
    }

    public string Text { get => _text; set => Set(ref _text, value ?? string.Empty); }

    public bool NewLine { get => _newLine; set => Set(ref _newLine, value); }

    public ConsoleStream Stream { get => _stream; set => Set(ref _stream, value); }

    /// <summary>Whether each line written starts with a timestamp, log style.</summary>
    public bool Log { get => _log; set => Set(ref _log, value); }

    public override async Task EvaluateAsync(NodeEvaluationContext context)
    {
        var text = _in.IsConnected ? NodeValues.ToText(_in.Value) : Text;
        if (Log)
        {
            var stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff ", System.Globalization.CultureInfo.InvariantCulture);
            text = string.Join('\n', text.Split('\n').Select(line => stamp + line));
        }

        var writer = Stream == ConsoleStream.StandardError ? Console.Error : Console.Out;

        var written = NewLine ? text + Environment.NewLine : text;
        await writer.WriteAsync(written.AsMemory(), context.CancellationToken).ConfigureAwait(false);
        await writer.FlushAsync(context.CancellationToken).ConfigureAwait(false);

        // Recorded too, so the Standard Output and Standard Error nodes can show it in the editor.
        context.Console?.Write(written, error: Stream == ConsoleStream.StandardError);

        _out.Value = text;
    }
}

/// <summary>
/// A named value from the command line: <c>vnoder-cli graph.vnoder --name value</c>. The same graph can
/// then be run against different inputs without editing it. A parameter that was not given is a
/// result, not an error: Value falls back to Default and Found is false, which is also what happens in
/// the editor, where there is no command line. Required is enforced by vnoder-cli before the run
/// starts, so the node itself never fails for a missing value.
/// </summary>
/// <remarks>
/// vnoder-cli finds every CLI Param in the graph to list them (with --help) and to check the arguments,
/// before the graph runs, so Name, Required and Description are read from the node's inline state and
/// are not wirable: a wired value only exists once the graph has run. Default is the exception, because
/// it is only needed during the run: wired, it is computed (the current user, today's date, a value read
/// from a file), and --help lists it as such. Several nodes may share a name; they read the same value.
/// </remarks>
[NodeDefinition("CLI/CLI Param", Description = "A named parameter given to vnoder-cli as --name value; falls back to a default when absent.")]
public sealed class CliParamNode : NodeBase
{
    private string _name = string.Empty;
    private bool _required;
    private string _default = string.Empty;
    private string _description = string.Empty;

    private readonly InputPort _defaultIn;
    private readonly OutputPort _valueOut;
    private readonly OutputPort _foundOut;

    public CliParamNode()
    {
        _defaultIn = AddInput("Default", PortTypeCompatibility.Any);
        _valueOut = AddOutput<string>("Value");
        _foundOut = AddOutput<bool>("Found");
    }

    /// <summary>The name given on the command line, without the leading <c>--</c>; case does not matter.</summary>
    public string Name { get => _name; set => Set(ref _name, NormalizeName(value)); }

    public bool Required { get => _required; set => Set(ref _required, value); }

    public string Default { get => _default; set => Set(ref _default, value ?? string.Empty); }

    /// <summary>Whether Default comes from a wire, so vnoder-cli lists it as computed rather than showing the inline value.</summary>
    public bool DefaultIsWired => _defaultIn.IsConnected;

    /// <summary>One line for the parameter list vnoder-cli prints.</summary>
    public string Description { get => _description; set => Set(ref _description, value ?? string.Empty); }

    /// <summary>A name as typed, minus surrounding space and any leading dashes the user added out of habit.</summary>
    public static string NormalizeName(string? name) => (name ?? string.Empty).Trim().TrimStart('-').Trim();

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var value = Name.Length > 0 ? context.Host?.GetParameter(Name) : null;

        _valueOut.Value = value ?? (_defaultIn.IsConnected ? NodeValues.ToText(_defaultIn.Value) : Default);
        _foundOut.Value = value is not null;
        return Task.CompletedTask;
    }
}

/// <summary>
/// Sets the code vnoder-cli exits with, so a script can branch on what the graph found: a Compare or
/// Select into Code, or a Success flag into Run If. Without one, vnoder-cli exits 0, or 1 if a node
/// failed. In the editor it does nothing, so the graph can still be built and run there.
/// </summary>
[NodeDefinition("CLI/Exit Code", Description = "Sets the exit code vnoder-cli returns when the run is over.")]
public sealed class CliExitCodeNode : NodeBase
{
    private double _code;

    private readonly InputPort _codeIn;

    public CliExitCodeNode()
    {
        _codeIn = AddInput<double>("Code");

        AddRunGate();
    }

    public double Code { get => _code; set => Set(ref _code, value); }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var host = context.Host ?? throw new InvalidOperationException("An exit code can be set only when the graph runs in vNoder or vnoder-cli.");

        var code = _codeIn.ValueOr(Code);
        if (double.IsNaN(code))
            throw new ArgumentException("The exit code is not a number.");

        host.SetExitCode((int)Math.Clamp(Math.Round(code), int.MinValue, int.MaxValue));
        return Task.CompletedTask;
    }
}

/// <summary>
/// What was piped into vnoder-cli (<c>type log.txt | vnoder-cli filter.vnoder</c>), as text and as
/// lines. It comes through the host (<see cref="INodeHost.ReadStandardInputAsync"/>): only vnoder-cli
/// reads standard input, once, and every CLI Read Stdin node and every pass sees the same text.
/// </summary>
/// <remarks>
/// Nothing piped in is a result, not an error: Text is empty and Found false. That is always the case
/// in the editor and in a harness with no host, and when vnoder-cli is run from a terminal with nothing
/// redirected, where reading would otherwise wait for the user to type.
/// </remarks>
[NodeDefinition("CLI/CLI Read Stdin", Description = "Reads what was piped into vnoder-cli (standard input), as text and lines.")]
public sealed class CliReadStdinNode : NodeBase
{
    private readonly OutputPort _textOut;
    private readonly OutputPort _linesOut;
    private readonly OutputPort _foundOut;

    public CliReadStdinNode()
    {
        _textOut = AddOutput<string>("Text");
        _linesOut = AddOutput<string[]>("Lines");
        _foundOut = AddOutput<bool>("Found");

        AddRunGate();
    }

    public override async Task EvaluateAsync(NodeEvaluationContext context)
    {
        var text = context.Host is { } host
            ? await host.ReadStandardInputAsync(context.CancellationToken).ConfigureAwait(false)
            : null;

        _textOut.Value = text ?? string.Empty;
        _linesOut.Value = text is null ? [] : SplitLines(text);
        _foundOut.Value = text is not null;
    }

    /// <summary>Lines without their endings; the empty "line" after a final newline is not one.</summary>
    private static string[] SplitLines(string text)
    {
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        if (lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);

        return [.. lines];
    }
}

/// <summary>
/// Shows what CLI Write wrote to one stream in the current run, the way a console would: in the
/// editor there is no console, so this is where a graph meant for vnoder-cli shows its output. Each
/// Run Once starts it empty, and a continuous run's ticks pile up, newest at the bottom.
/// </summary>
/// <remarks>
/// It has no ports, because what it shows does not arrive by wire: it reads the executor's
/// <see cref="ConsoleCapture"/>, and reads it when the view refreshes after the pass, not when the node
/// itself evaluates. So it shows everything written, whichever order the nodes happened to run in.
/// </remarks>
public abstract class ConsoleViewNode : NodeBase
{
    private ConsoleCapture? _capture;
    private bool _wrap = true;

    /// <summary>Whether this view shows standard error rather than standard output.</summary>
    public abstract bool ShowsError { get; }

    /// <summary>Whether long lines wrap, as in a console window, rather than run off to the right.</summary>
    public bool Wrap { get => _wrap; set => Set(ref _wrap, value); }

    public string DisplayText => _capture is null ? string.Empty : ShowsError ? _capture.Error : _capture.Output;

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        _capture = context.Console;
        return Task.CompletedTask;
    }
}

[NodeDefinition("CLI/Standard Output", Resizable = true, Description = "Shows what CLI Write wrote to standard output in this run, like a console.")]
public sealed class StandardOutputNode : ConsoleViewNode
{
    public override bool ShowsError => false;
}

[NodeDefinition("CLI/Standard Error", Resizable = true, Description = "Shows what CLI Write wrote to standard error in this run, like a console.")]
public sealed class StandardErrorNode : ConsoleViewNode
{
    public override bool ShowsError => true;
}
