using vNoder.Core;

namespace vNoder.Cli;

/// <summary>
/// What a command-line run offers the nodes: an exit code, and the parameters given after the file
/// (keyed case-insensitively by the caller). The clipboard and desktop notifications belong to the
/// editor, so the nodes that need them fail with a message saying so.
/// </summary>
internal sealed class CliNodeHost(IReadOnlyDictionary<string, string> parameters) : INodeHost
{
    private readonly IReadOnlyDictionary<string, string> _parameters = parameters;
    private int? _exitCode;

    /// <summary>The code set by an Exit Code node, or null when none ran.</summary>
    public int? ExitCode => _exitCode;

    public Task<string> GetClipboardTextAsync(CancellationToken cancellationToken) =>
        throw new InvalidOperationException("The clipboard is not available from the command line.");

    public Task SetClipboardTextAsync(string text, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("The clipboard is not available from the command line.");

    public Task NotifyAsync(string title, string message, bool sound, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Desktop notifications are not available from the command line.");

    public void SetExitCode(int code) => _exitCode = code;

    public string? GetParameter(string name) => _parameters.GetValueOrDefault(name);

    /// <summary>Standard input can be read once, so the first read is kept for every later node and pass.</summary>
    private readonly Lazy<Task<string?>> _input = new(ReadInputAsync);

    public Task<string?> ReadStandardInputAsync(CancellationToken cancellationToken) =>
        _input.Value.WaitAsync(cancellationToken);

    /// <summary>
    /// The whole of standard input as UTF-8 (a byte-order mark picks another encoding), or null when
    /// nothing is redirected into it, where reading would wait for the user to type.
    /// </summary>
    private static async Task<string?> ReadInputAsync()
    {
        if (!Console.IsInputRedirected)
            return null;

        using var reader = new StreamReader(Console.OpenStandardInput(), System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync().ConfigureAwait(false);
    }
}
