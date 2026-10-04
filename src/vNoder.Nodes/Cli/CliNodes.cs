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
/// </remarks>
[NodeDefinition("CLI/CLI Write", Description = "Writes text (or any value, as text) to standard output or standard error, for graphs run by vnoder-cli.")]
public sealed class CliWriteNode : NodeBase
{
    private string _text = string.Empty;
    private bool _newLine = true;
    private ConsoleStream _stream;

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

    public override async Task EvaluateAsync(NodeEvaluationContext context)
    {
        var text = _in.IsConnected ? NodeValues.ToText(_in.Value) : Text;
        var writer = Stream == ConsoleStream.StandardError ? Console.Error : Console.Out;

        await writer.WriteAsync((NewLine ? text + Environment.NewLine : text).AsMemory(), context.CancellationToken)
            .ConfigureAwait(false);
        await writer.FlushAsync(context.CancellationToken).ConfigureAwait(false);

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
/// vnoder-cli finds every CLI Param in the graph to list them (with --help) and to check the
/// arguments, so Name, Required, Default and Description are read from the node's inline state and are
/// not wirable. Several nodes may share a name; they read the same value.
/// </remarks>
[NodeDefinition("CLI/CLI Param", Description = "A named parameter given to vnoder-cli as --name value; falls back to a default when absent.")]
public sealed class CliParamNode : NodeBase
{
    private string _name = string.Empty;
    private bool _required;
    private string _default = string.Empty;
    private string _description = string.Empty;

    private readonly OutputPort _valueOut;
    private readonly OutputPort _foundOut;

    public CliParamNode()
    {
        _valueOut = AddOutput<string>("Value");
        _foundOut = AddOutput<bool>("Found");
    }

    /// <summary>The name given on the command line, without the leading <c>--</c>; case does not matter.</summary>
    public string Name { get => _name; set => Set(ref _name, NormalizeName(value)); }

    public bool Required { get => _required; set => Set(ref _required, value); }

    public string Default { get => _default; set => Set(ref _default, value ?? string.Empty); }

    /// <summary>One line for the parameter list vnoder-cli prints.</summary>
    public string Description { get => _description; set => Set(ref _description, value ?? string.Empty); }

    /// <summary>A name as typed, minus surrounding space and any leading dashes the user added out of habit.</summary>
    public static string NormalizeName(string? name) => (name ?? string.Empty).Trim().TrimStart('-').Trim();

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var value = Name.Length > 0 ? context.Host?.GetParameter(Name) : null;

        _valueOut.Value = value ?? Default;
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
