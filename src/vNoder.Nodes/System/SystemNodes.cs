using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using vNoder.Core;

namespace vNoder.Nodes;

[NodeDefinition("System/Environment Variable", Description = "Reads an environment variable, e.g. PATH, USERPROFILE or TEMP.")]
public sealed class EnvironmentVariableNode : NodeBase
{
    private string _name = string.Empty;

    private readonly InputPort _nameIn;
    private readonly OutputPort _valueOut;
    private readonly OutputPort _foundOut;

    public EnvironmentVariableNode()
    {
        _nameIn = AddInput<string>("Name");
        _valueOut = AddOutput<string>("Value");
        _foundOut = AddOutput<bool>("Found");
    }

    public string Name
    {
        get => _name;
        set => Set(ref _name, value ?? string.Empty);
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        // "%TEMP%" works as well as "TEMP", since that is how the names are usually written on Windows.
        var name = _nameIn.ValueOr(Name).Trim().Trim('%');
        var value = name.Length == 0 ? null : Environment.GetEnvironmentVariable(name);

        _valueOut.Value = value ?? string.Empty;
        _foundOut.Value = value is not null;
        return Task.CompletedTask;
    }
}

/// <summary>
/// Runs a program and captures what it prints. Like File Write and HTTP Request, it runs on every pass
/// of a continuous run; a tick is skipped while the previous one is still going, so a slow program
/// does not pile up. A program that outlives its timeout, or a continuous run that is stopped, has its
/// whole process tree killed.
/// </summary>
[NodeDefinition("System/Run Process", Description = "Runs a program or command and returns its output, errors and exit code.")]
public sealed class RunProcessNode : NodeBase
{
    private string _fileName = string.Empty;
    private string _arguments = string.Empty;
    private string _workingDirectory = string.Empty;
    private double _timeoutSeconds = 30;

    private readonly InputPort _fileNameIn;
    private readonly InputPort _argumentsIn;
    private readonly InputPort _inputIn;
    private readonly OutputPort _outputOut;
    private readonly OutputPort _errorOut;
    private readonly OutputPort _exitCodeOut;
    private readonly OutputPort _successOut;

    public RunProcessNode()
    {
        _fileNameIn = AddInput<string>("Program");
        _argumentsIn = AddInput<string>("Arguments");
        _inputIn = AddInput("Input", PortTypeCompatibility.Any);
        _outputOut = AddOutput<string>("Output");
        _errorOut = AddOutput<string>("Errors");
        _exitCodeOut = AddOutput<double>("Exit Code");
        _successOut = AddOutput<bool>("Success");

        AddRunGate();
    }

    /// <summary>The program: a path, or a name found on PATH such as <c>git</c> or <c>cmd</c>.</summary>
    public string FileName
    {
        get => _fileName;
        set => Set(ref _fileName, value ?? string.Empty);
    }

    public string Arguments
    {
        get => _arguments;
        set => Set(ref _arguments, value ?? string.Empty);
    }

    /// <summary>Empty means the app's own working directory.</summary>
    public string WorkingDirectory
    {
        get => _workingDirectory;
        set => Set(ref _workingDirectory, value ?? string.Empty);
    }

    /// <summary>Seconds before the program is killed; 0 or less waits indefinitely.</summary>
    public double TimeoutSeconds
    {
        get => _timeoutSeconds;
        set => Set(ref _timeoutSeconds, value);
    }

    public override async Task EvaluateAsync(NodeEvaluationContext context)
    {
        _outputOut.Value = null;
        _errorOut.Value = null;
        _exitCodeOut.Value = null;
        _successOut.Value = false;

        var fileName = PathText.Clean(_fileNameIn.ValueOr(FileName));
        if (fileName.Length == 0)
            throw new InvalidOperationException("No program set.");

        var workingDirectory = PathText.Clean(WorkingDirectory);
        if (workingDirectory.Length > 0 && !Directory.Exists(workingDirectory))
            throw new InvalidOperationException($"Working directory not found: {workingDirectory}");

        var input = _inputIn.Value;
        var start = new ProcessStartInfo(fileName, _argumentsIn.ValueOr(Arguments))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = input is not null,
            WorkingDirectory = workingDirectory,
        };

        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException($"Could not start '{fileName}': {exception.Message}");
        }

        var timeout = double.IsNaN(TimeoutSeconds) || TimeoutSeconds <= 0 ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(Math.Min(TimeoutSeconds, 86_400));
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, timeoutSource.Token);
        var token = linked.Token;

        try
        {
            // Both streams are read concurrently: reading one to the end first can deadlock once the
            // program fills the other's pipe buffer.
            var output = process.StandardOutput.ReadToEndAsync(token);
            var errors = process.StandardError.ReadToEndAsync(token);

            if (input is not null)
                await WriteInputAsync(process, NodeValues.AsBytes(input, "Process input"), token).ConfigureAwait(false);

            await process.WaitForExitAsync(token).ConfigureAwait(false);

            _outputOut.Value = await output.ConfigureAwait(false);
            _errorOut.Value = await errors.ConfigureAwait(false);
            _exitCodeOut.Value = (double)process.ExitCode;
            _successOut.Value = process.ExitCode == 0;
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            if (timeoutSource.IsCancellationRequested && !context.CancellationToken.IsCancellationRequested)
                throw new InvalidOperationException($"'{fileName}' did not finish within {timeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} s and was stopped.");

            throw;
        }
    }

    private static async Task WriteInputAsync(Process process, byte[] bytes, CancellationToken token)
    {
        try
        {
            await process.StandardInput.BaseStream.WriteAsync(bytes, token).ConfigureAwait(false);
            await process.StandardInput.BaseStream.FlushAsync(token).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // The program exited without reading all its input: a closed pipe, not a failure.
        }
        finally
        {
            try
            {
                process.StandardInput.Close();
            }
            catch (IOException)
            {
            }
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Exited between the check and the kill.
        }
    }
}
