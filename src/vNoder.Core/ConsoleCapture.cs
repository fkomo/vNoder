using System.Text;

namespace vNoder.Core;

/// <summary>
/// What the graph wrote to standard output and standard error in the current run, kept so the editor
/// can show it the way a console would (the Standard Output and Standard Error nodes): in the editor
/// there is no console, and what CLI Write sends to the real one goes nowhere.
/// </summary>
/// <remarks>
/// <see cref="GraphExecutor"/> owns one and clears it when a run starts: each Run Once, and the start of
/// a continuous run, whose ticks then accumulate as a console's output would. Each stream keeps only
/// its last <see cref="Capacity"/> characters, so a long continuous run cannot grow it without bound.
/// Written from the evaluation thread and read from the UI thread, hence the lock.
/// </remarks>
public sealed class ConsoleCapture
{
    /// <summary>How many characters each stream keeps; older text is dropped from the front.</summary>
    public const int Capacity = 200_000;

    private readonly StringBuilder _output = new();
    private readonly StringBuilder _error = new();
    private readonly Lock _lock = new();

    public string Output
    {
        get { lock (_lock) return _output.ToString(); }
    }

    public string Error
    {
        get { lock (_lock) return _error.ToString(); }
    }

    public void Write(string text, bool error)
    {
        lock (_lock)
        {
            var buffer = error ? _error : _output;
            buffer.Append(text);
            if (buffer.Length > Capacity)
                buffer.Remove(0, buffer.Length - Capacity);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _output.Clear();
            _error.Clear();
        }
    }
}
