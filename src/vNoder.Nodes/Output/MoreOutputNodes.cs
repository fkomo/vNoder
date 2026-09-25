using System.Globalization;
using vNoder.Core;

namespace vNoder.Nodes;

/// <summary>What <see cref="TableNode"/> shows: column headers and rows of cells, as text.</summary>
public sealed record TableSnapshot(IReadOnlyList<string> Columns, IReadOnlyList<string[]> Rows, int TotalRows);

/// <summary>
/// Shows rows as a grid: a list of lists (CSV Parse's Rows, Zip's Pairs, a per-item run of Split), a
/// list as one column, or CSV text, which is parsed. With First Row Is Header the first row names the
/// columns. Like Output, it passes its input straight through.
/// </summary>
/// <remarks>
/// Only the first 10,000 rows are shown; the rest are counted in the summary but not drawn.
/// </remarks>
[NodeDefinition("Output/Table", Merges = true, Resizable = true, Description = "Shows a list of lists, a list or CSV text as a table.")]
public sealed class TableNode : NodeBase
{
    private const int MaxRows = 10_000;

    private bool _firstRowIsHeader = true;

    private readonly InputPort _in;
    private readonly OutputPort _out;

    public TableNode()
    {
        _in = AddInput("Rows", PortTypeCompatibility.Any);
        _out = AddOutput("Rows", PortTypeCompatibility.Any);
    }

    public bool FirstRowIsHeader { get => _firstRowIsHeader; set => Set(ref _firstRowIsHeader, value); }

    /// <summary>The table last shown. Assigned during evaluation without notifying, like <see cref="OutputNode.Value"/>.</summary>
    public TableSnapshot? Table { get; private set; }

    public string Summary => Table is not { } table ? string.Empty
        : table.TotalRows > table.Rows.Count ? $"{table.Rows.Count:N0} of {table.TotalRows:N0} rows × {table.Columns.Count} columns"
        : $"{table.TotalRows:N0} rows × {table.Columns.Count} columns";

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var value = _in.Value;
        _out.Value = value;

        var rows = value is string text ? Csv.Parse(text, Csv.Separator(string.Empty, text)) : Csv.Rows(value);
        string[] header = [];
        if (FirstRowIsHeader && rows.Count > 0)
        {
            header = rows[0];
            rows.RemoveAt(0);
        }

        var width = Math.Max(header.Length, rows.Count == 0 ? 0 : rows.Max(r => r.Length));
        var columns = Enumerable.Range(0, width)
            .Select(i => i < header.Length && header[i].Length > 0 ? header[i] : (i + 1).ToString(CultureInfo.InvariantCulture))
            .ToArray();

        Table = value is null ? null : new TableSnapshot(columns, rows.Take(MaxRows).ToArray(), rows.Count);
        return Task.CompletedTask;
    }
}

/// <summary>
/// A line chart. A number is added to a history of the last N values, which is what a continuous run
/// plots over time (a price, a temperature, a response time); a list of numbers is plotted as it is.
/// Like the Flow nodes, the history starts over when a continuous run starts.
/// </summary>
[NodeDefinition("Output/Plot", Merges = true, Resizable = true, Description = "Plots numbers as a line: a value over time in a continuous run, or a whole list at once.")]
public sealed class PlotNode : StatefulNode
{
    private double _length = 100;
    private readonly List<double> _history = [];

    private readonly InputPort _in;
    private readonly OutputPort _out;

    public PlotNode()
    {
        _in = AddInput("Value", PortTypeCompatibility.Any);
        _out = AddOutput("Value", PortTypeCompatibility.Any);
    }

    /// <summary>How many values of history to plot, 2 to 10,000.</summary>
    public double Length { get => _length; set => Set(ref _length, value); }

    /// <summary>What the view draws. Assigned during evaluation without notifying.</summary>
    public double[] Points { get; private set; } = [];

    public string Summary => Points.Length == 0 ? string.Empty
        : $"last {Format(Points[^1])}   min {Format(Points.Min())}   max {Format(Points.Max())}";

    protected override void Reset() => _history.Clear();

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        Remember(context);
        var value = _in.Value;
        _out.Value = value;

        if (PortTypeCompatibility.IsListValue(value))
        {
            Points = NodeValues.Items(value).Select(NodeValues.AsNumber).Where(n => n is { } d && double.IsFinite(d)).Select(n => n!.Value).ToArray();
            return Task.CompletedTask;
        }

        if (NodeValues.AsNumber(value) is { } number && double.IsFinite(number))
            _history.Add(number);

        var keep = double.IsNaN(Length) ? 100 : (int)Math.Clamp(Length, 2, 10_000);
        if (_history.Count > keep)
            _history.RemoveRange(0, _history.Count - keep);

        Points = [.. _history];
        return Task.CompletedTask;
    }

    private static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>
/// Keeps a timestamped line for each value that arrives, newest first, so a continuous run leaves a
/// readable trail instead of Output's latest value only. The lines are also an output, for File Write.
/// A value's line breaks become spaces, so one entry stays one line.
/// </summary>
[NodeDefinition("Output/Log", Merges = true, Resizable = true, Description = "Keeps a timestamped line per value across passes, newest first: a trail of what a continuous run saw.")]
public sealed class LogNode : StatefulNode
{
    private double _maxLines = 200;
    private bool _timestamp = true;
    private readonly List<string> _lines = [];

    private readonly InputPort _in;
    private readonly OutputPort _out;

    public LogNode()
    {
        _in = AddInput("Value", PortTypeCompatibility.Any);
        _out = AddOutput<string[]>("Lines");
    }

    public double MaxLines { get => _maxLines; set => Set(ref _maxLines, value); }

    public bool Timestamp { get => _timestamp; set => Set(ref _timestamp, value); }

    /// <summary>What the view shows. Assigned during evaluation without notifying.</summary>
    public string DisplayText { get; private set; } = string.Empty;

    protected override void Reset() => _lines.Clear();

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        Remember(context);

        // A skipped or empty branch arrives as nothing, which is not an entry.
        if (_in.Value is { } value)
        {
            var text = string.Join(' ', NodeValues.ToText(value).ReplaceLineEndings("\n").Split('\n'));
            _lines.Insert(0, Timestamp ? $"{DateTime.Now:HH:mm:ss.fff}  {text}" : text);
        }

        var keep = double.IsNaN(MaxLines) ? 200 : (int)Math.Clamp(MaxLines, 1, 100_000);
        if (_lines.Count > keep)
            _lines.RemoveRange(keep, _lines.Count - keep);

        _out.Value = _lines.ToArray();
        DisplayText = string.Join(Environment.NewLine, _lines);
        return Task.CompletedTask;
    }
}
