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
}
