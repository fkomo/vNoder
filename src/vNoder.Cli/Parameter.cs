using vNoder.Core;
using vNoder.Nodes;

namespace vNoder.Cli;

/// <summary>
/// One command-line parameter a graph declares, gathered from its CLI Param nodes. Several nodes may
/// use the same name: they make one parameter, required if any of them is, with the first default and
/// description found. A default wired into any of them is computed during the run, so the list says
/// that rather than showing a value.
/// </summary>
internal sealed record Parameter(string Name, bool Required, string Default, string Description, bool DefaultComputed)
{
    public static IReadOnlyList<Parameter> FromGraph(Graph graph) =>
        graph.Nodes
            .OfType<CliParamNode>()
            .Where(n => n.Name.Length > 0)
            .GroupBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new Parameter(
                g.First().Name,
                g.Any(n => n.Required),
                g.Select(n => n.Default).FirstOrDefault(d => d.Length > 0) ?? string.Empty,
                g.Select(n => n.Description.Trim()).FirstOrDefault(d => d.Length > 0) ?? string.Empty,
                g.Any(n => n.DefaultIsWired)))
            .OrderByDescending(p => p.Required)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Reads <c>--name value</c>, <c>--name=value</c> and a bare <c>--name</c> (which means "true", for a
    /// switch). A value may itself start with a single dash, so <c>--offset -5</c> works; only a
    /// <c>--</c> token starts the next name. A name given twice keeps the last value. A lone <c>--</c>
    /// is ignored.
    /// </summary>
    public static bool TryParse(
        IReadOnlyList<string> args,
        out Dictionary<string, string> values,
        out string? error)
    {
        values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        error = null;

        for (var i = 0; i < args.Count; i++)
        {
            var token = args[i];
            if (token == "--")
                continue;

            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                error = $"unexpected argument '{token}': parameters are given as --name value.";
                return false;
            }

            var name = token[2..];
            string value;

            var equals = name.IndexOf('=');
            if (equals >= 0)
            {
                value = name[(equals + 1)..];
                name = name[..equals];
            }
            else if (i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                value = args[++i];
            }
            else
            {
                value = "true";
            }

            if (name.Length == 0)
            {
                error = $"unexpected argument '{token}': a parameter needs a name.";
                return false;
            }

            values[name] = value;
        }

        return true;
    }

    public static void Print(TextWriter writer, string fileName, IReadOnlyList<Parameter> parameters)
    {
        if (parameters.Count == 0)
        {
            writer.WriteLine($"{fileName} takes no parameters (it has no CLI Param nodes).");
            return;
        }

        writer.WriteLine($"Parameters of {fileName}:");
        writer.WriteLine();

        var labels = parameters.Select(p => $"--{p.Name} <value>").ToList();
        var width = labels.Max(l => l.Length) + 3;

        for (var i = 0; i < parameters.Count; i++)
        {
            var parameter = parameters[i];
            var notes = new List<string>();
            if (parameter.Description.Length > 0)
                notes.Add(parameter.Description);
            if (parameter.Required)
                notes.Add("(required)");
            else if (parameter.DefaultComputed)
                notes.Add("(default: computed by the graph)");
            else if (parameter.Default.Length > 0)
                notes.Add($"(default: {parameter.Default})");
            else
                notes.Add("(optional)");

            writer.WriteLine($"  {labels[i].PadRight(width)}{string.Join(' ', notes)}");
        }

        writer.WriteLine();
        var example = string.Join(' ', parameters.Where(p => p.Required).Select(p => $"--{p.Name} <value>"));
        writer.WriteLine($"Run: vnoder-cli {fileName}{(example.Length > 0 ? " " + example : string.Empty)}");
    }
}
