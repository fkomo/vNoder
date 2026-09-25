using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using vNoder.Core;

namespace vNoder.Nodes;

// CSV as RFC 4180 has it: fields separated by a character, quoted with " when they contain the
// separator, a quote or a line break, a quote inside doubled. Rows travel as a list of lists of text
// (string[][]), which a per-item run peels one row at a time.

internal static class Csv
{
    private static readonly char[] Candidates = [',', ';', '\t', '|'];

    /// <summary>
    /// The separator a field names: one character, "\t" or "tab" for a tab, or empty to detect it from
    /// the first line (whichever of , ; tab | occurs most outside quotes).
    /// </summary>
    public static char Separator(string setting, string text)
    {
        var trimmed = setting.Trim();
        if (trimmed is @"\t" || trimmed.Equals("tab", StringComparison.OrdinalIgnoreCase))
            return '\t';

        if (setting.Length == 1)
            return setting[0];

        if (trimmed.Length > 0)
            throw new InvalidOperationException($"The separator must be one character, or \\t for a tab; got '{setting}'.");

        var firstLine = FirstLine(text);
        return Candidates.OrderByDescending(c => firstLine.Count(ch => ch == c)).First();
    }

    private static string FirstLine(string text)
    {
        var inQuotes = false;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '"')
                inQuotes = !inQuotes;
            else if (!inQuotes && text[i] is '\r' or '\n')
                return text[..i];
        }

        return text;
    }

    public static List<string[]> Parse(string text, char separator)
    {
        var rows = new List<string[]>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var fieldStarted = false;

        // A leading byte order mark is not part of the first header.
        var i = text.Length > 0 && text[0] == '﻿' ? 1 : 0;
        for (; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c != '"')
                    field.Append(c);
                else if (i + 1 < text.Length && text[i + 1] == '"')
                    field.Append(text[++i]);
                else
                    inQuotes = false;
            }
            else if (c == '"' && field.Length == 0)
            {
                inQuotes = true;
                fieldStarted = true;
            }
            else if (c == separator)
            {
                row.Add(field.ToString());
                field.Clear();
                fieldStarted = true;
            }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                    i++;

                EndRow();
            }
            else
            {
                field.Append(c);
                fieldStarted = true;
            }
        }

        if (fieldStarted || field.Length > 0 || row.Count > 0)
            EndRow();

        return rows;

        void EndRow()
        {
            row.Add(field.ToString());
            field.Clear();

            // A blank line is not a row of one empty field.
            if (row.Count > 1 || row[0].Length > 0 || fieldStarted)
                rows.Add([.. row]);

            row.Clear();
            fieldStarted = false;
        }
    }

    public static string Format(IEnumerable<IReadOnlyList<string>> rows, char separator, bool alwaysQuote)
    {
        var builder = new StringBuilder();
        foreach (var row in rows)
        {
            for (var i = 0; i < row.Count; i++)
            {
                if (i > 0)
                    builder.Append(separator);

                var field = row[i];
                var quote = alwaysQuote || field.IndexOfAny([separator, '"', '\r', '\n']) >= 0
                    || (field.Length > 0 && (char.IsWhiteSpace(field[0]) || char.IsWhiteSpace(field[^1])));
                builder.Append(quote ? '"' + field.Replace("\"", "\"\"") + '"' : field);
            }

            builder.Append("\r\n");
        }

        return builder.ToString();
    }

    /// <summary>Header names made unique and non-empty, so they can key JSON objects.</summary>
    public static string[] Names(string[] header, int width)
    {
        var names = new string[width];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < width; i++)
        {
            var name = i < header.Length && header[i].Trim().Length > 0 ? header[i].Trim() : $"column{i + 1}";
            var unique = name;
            for (var n = 2; !seen.Add(unique); n++)
                unique = $"{name}{n}";

            names[i] = unique;
        }

        return names;
    }

    /// <summary>Rows from a value: a list of lists, a text list (one field per row), or CSV-free text as one cell.</summary>
    public static List<string[]> Rows(object? value) => NodeValues.Items(value)
        .Select(row => row is Array cells and not byte[]
            ? cells.Cast<object?>().Select(NodeValues.ToText).ToArray()
            : [NodeValues.ToText(row)])
        .ToList();
}

[NodeDefinition("Data/CSV Parse", Description = "Reads CSV (or TSV) text into rows, with the header and any one column as a list.")]
public sealed class CsvParseNode : NodeBase
{
    private string _separator = string.Empty;
    private bool _hasHeader = true;
    private string _column = string.Empty;

    private readonly InputPort _in;
    private readonly InputPort _columnIn;
    private readonly OutputPort _rowsOut;
    private readonly OutputPort _headerOut;
    private readonly OutputPort _columnOut;
    private readonly OutputPort _countOut;

    public CsvParseNode()
    {
        _in = AddInput<string>("CSV");
        _columnIn = AddInput<string>("Column");
        _rowsOut = AddOutput<string[][]>("Rows");
        _headerOut = AddOutput<string[]>("Header");
        _columnOut = AddOutput<string[]>("Column");
        _countOut = AddOutput<double>("Row Count");
    }

    /// <summary>One character, \t for tab, or empty to detect , ; tab or |.</summary>
    public string Separator { get => _separator; set => Set(ref _separator, value ?? string.Empty); }

    /// <summary>Whether the first row names the columns; it is then left out of Rows.</summary>
    public bool HasHeader { get => _hasHeader; set => Set(ref _hasHeader, value); }

    /// <summary>A column to extract as a list: its header name, or its number counting from 1.</summary>
    public string Column { get => _column; set => Set(ref _column, value ?? string.Empty); }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var text = _in.ValueOr(string.Empty);
        var rows = Csv.Parse(text, Csv.Separator(Separator, text));

        var header = HasHeader && rows.Count > 0 ? rows[0] : [];
        var body = HasHeader && rows.Count > 0 ? rows.Skip(1).ToArray() : rows.ToArray();

        _rowsOut.Value = body;
        _headerOut.Value = header;
        _countOut.Value = (double)body.Length;
        _columnOut.Value = ColumnValues(_columnIn.ValueOr(Column).Trim(), header, body);
        return Task.CompletedTask;
    }

    private static string[]? ColumnValues(string column, string[] header, string[][] rows)
    {
        if (column.Length == 0)
            return null;

        var index = Array.FindIndex(header, name => string.Equals(name.Trim(), column, StringComparison.OrdinalIgnoreCase));
        if (index < 0 && int.TryParse(column, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            index = number - 1;

        if (index < 0)
            throw new InvalidOperationException($"No column '{column}'.");

        // A short row has an empty cell there rather than none, so the list lines up with Rows.
        return rows.Select(row => index < row.Length ? row[index] : string.Empty).ToArray();
    }
}

[NodeDefinition("Data/CSV Format", Description = "Writes rows (a list of lists, or a list for one column) as CSV text, quoting where needed.")]
public sealed class CsvFormatNode : NodeBase
{
    private string _separator = ",";
    private bool _alwaysQuote;

    private readonly InputPort _rowsIn;
    private readonly InputPort _headerIn;
    private readonly OutputPort _out;

    public CsvFormatNode()
    {
        // "any" so a list of lists, a text list or a list of "any" all fit whole.
        _rowsIn = AddInput("Rows", PortTypeCompatibility.Any);
        _headerIn = AddInput<string[]>("Header");
        _out = AddOutput<string>("CSV");
    }

    public string Separator { get => _separator; set => Set(ref _separator, value ?? string.Empty); }

    public bool AlwaysQuote { get => _alwaysQuote; set => Set(ref _alwaysQuote, value); }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var rows = Csv.Rows(_rowsIn.Value);
        if (_headerIn.Value is string[] header)
            rows.Insert(0, header);

        var separator = Csv.Separator(Separator.Length == 0 ? "," : Separator, string.Empty);
        _out.Value = Csv.Format(rows, separator, AlwaysQuote);
        return Task.CompletedTask;
    }
}

/// <summary>
/// CSV with a header to a JSON array of objects, one per row, keyed by column name. Values are
/// strings; with Detect Types, numbers, true/false and empty cells become JSON numbers, booleans and
/// nulls.
/// </summary>
[NodeDefinition("Data/CSV To JSON", Description = "Turns CSV with a header row into a JSON array of objects.")]
public sealed class CsvToJsonNode : NodeBase
{
    private string _separator = string.Empty;
    private bool _detectTypes = true;

    private readonly InputPort _in;
    private readonly OutputPort _out;

    public CsvToJsonNode()
    {
        _in = AddInput<string>("CSV");
        _out = AddOutput<string>("JSON");
    }

    public string Separator { get => _separator; set => Set(ref _separator, value ?? string.Empty); }

    public bool DetectTypes { get => _detectTypes; set => Set(ref _detectTypes, value); }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var text = _in.ValueOr(string.Empty);
        var rows = Csv.Parse(text, Csv.Separator(Separator, text));
        var array = new JsonArray();

        if (rows.Count > 0)
        {
            var width = rows.Max(r => r.Length);
            var names = Csv.Names(rows[0], width);
            foreach (var row in rows.Skip(1))
            {
                var obj = new JsonObject();
                for (var i = 0; i < width; i++)
                    obj[names[i]] = Value(i < row.Length ? row[i] : string.Empty);

                array.Add(obj);
            }
        }

        _out.Value = array.ToJsonString(JsonText.Compact);
        return Task.CompletedTask;
    }

    private JsonNode? Value(string cell)
    {
        if (!DetectTypes)
            return JsonValue.Create(cell);

        if (cell.Length == 0)
            return null;

        if (cell is "true" or "false")
            return JsonValue.Create(cell == "true");

        // Only numbers that read back the same, so "007" and "1e5" stay text: those are usually ids.
        if (double.TryParse(cell, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            && double.IsFinite(number) && number.ToString(CultureInfo.InvariantCulture) == cell)
        {
            return JsonValue.Create(number);
        }

        return JsonValue.Create(cell);
    }
}

/// <summary>
/// A JSON array to CSV. Objects become rows under a header of every key, in the order first seen;
/// arrays become rows as they are; plain values a single column. Nested objects and arrays are written
/// as their JSON text.
/// </summary>
[NodeDefinition("Data/JSON To CSV", Description = "Turns a JSON array of objects (or of arrays) into CSV with a header row.")]
public sealed class JsonToCsvNode : NodeBase
{
    private string _separator = ",";

    private readonly InputPort _in;
    private readonly OutputPort _out;

    public JsonToCsvNode()
    {
        _in = AddInput<string>("JSON");
        _out = AddOutput<string>("CSV");
    }

    public string Separator { get => _separator; set => Set(ref _separator, value ?? string.Empty); }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var root = JsonText.Parse(_in.ValueOr(string.Empty));
        var items = root is JsonArray array ? array.ToList() : [root];
        var rows = new List<IReadOnlyList<string>>();

        if (items.Count > 0 && items.All(i => i is JsonObject))
        {
            var keys = items.Cast<JsonObject>().SelectMany(o => o.Select(p => p.Key)).Distinct().ToList();
            rows.Add(keys);
            foreach (JsonObject obj in items.Cast<JsonObject>())
                rows.Add(keys.Select(k => obj.TryGetPropertyValue(k, out var v) ? Cell(v) : string.Empty).ToList());
        }
        else
        {
            foreach (var item in items)
                rows.Add(item is JsonArray cells ? cells.Select(Cell).ToList() : [Cell(item)]);
        }

        var separator = Csv.Separator(Separator.Length == 0 ? "," : Separator, string.Empty);
        _out.Value = Csv.Format(rows, separator, alwaysQuote: false);
        return Task.CompletedTask;
    }

    private static string Cell(JsonNode? node) => node is JsonValue value && value.GetValueKind() == JsonValueKind.Null
        ? string.Empty
        : JsonText.ToText(node);
}
