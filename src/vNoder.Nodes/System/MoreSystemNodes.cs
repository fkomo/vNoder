using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using vNoder.Core;

namespace vNoder.Nodes;

[NodeDefinition("System/Machine Info", Description = "Computer name, user, OS, processor count, memory, uptime and .NET version.")]
public sealed class MachineInfoNode : NodeBase
{
    private readonly OutputPort _machineOut;
    private readonly OutputPort _userOut;
    private readonly OutputPort _domainOut;
    private readonly OutputPort _osOut;
    private readonly OutputPort _architectureOut;
    private readonly OutputPort _processorsOut;
    private readonly OutputPort _memoryOut;
    private readonly OutputPort _uptimeOut;
    private readonly OutputPort _runtimeOut;

    public MachineInfoNode()
    {
        _machineOut = AddOutput<string>("Machine Name");
        _userOut = AddOutput<string>("User");
        _domainOut = AddOutput<string>("Domain");
        _osOut = AddOutput<string>("OS");
        _architectureOut = AddOutput<string>("Architecture");
        _processorsOut = AddOutput<double>("Processors");
        _memoryOut = AddOutput<double>("Memory");
        _uptimeOut = AddOutput<double>("Uptime Hours");
        _runtimeOut = AddOutput<string>(".NET");
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        _machineOut.Value = Environment.MachineName;
        _userOut.Value = Environment.UserName;
        _domainOut.Value = Environment.UserDomainName;
        _osOut.Value = RuntimeInformation.OSDescription;
        _architectureOut.Value = RuntimeInformation.OSArchitecture.ToString();
        _processorsOut.Value = (double)Environment.ProcessorCount;

        // Physical memory as the runtime sees it, in bytes (Number Format's File Size style reads it).
        _memoryOut.Value = (double)GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        _uptimeOut.Value = TimeSpan.FromMilliseconds(Environment.TickCount64).TotalHours;
        _runtimeOut.Value = RuntimeInformation.FrameworkDescription;
        return Task.CompletedTask;
    }
}

/// <summary>
/// The running processes, optionally only those whose name contains a filter, so "is it running?" is
/// Running and the names and ids feed Process Kill.
/// </summary>
[NodeDefinition("System/Process List", Description = "Lists running processes (names, ids, memory), optionally filtered by name; Running says whether any match.")]
public sealed class ProcessListNode : NodeBase
{
    private string _filter = string.Empty;

    private readonly InputPort _filterIn;
    private readonly OutputPort _namesOut;
    private readonly OutputPort _idsOut;
    private readonly OutputPort _memoryOut;
    private readonly OutputPort _countOut;
    private readonly OutputPort _runningOut;

    public ProcessListNode()
    {
        _filterIn = AddInput<string>("Filter");
        _namesOut = AddOutput<string[]>("Names");
        _idsOut = AddOutput<double[]>("Ids");
        _memoryOut = AddOutput<double[]>("Memory");
        _countOut = AddOutput<double>("Count");
        _runningOut = AddOutput<bool>("Running");

        AddRunGate();
    }

    /// <summary>Part of a process name, case-insensitive, with or without .exe; empty lists everything.</summary>
    public string Filter { get => _filter; set => Set(ref _filter, value ?? string.Empty); }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var filter = ProcessNames.Clean(_filterIn.ValueOr(Filter));
        var processes = Process.GetProcesses();
        try
        {
            var matches = processes
                .Where(p => filter.Length == 0 || p.ProcessName.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p.ProcessName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(p => p.Id)
                .ToList();

            _namesOut.Value = matches.Select(p => p.ProcessName).ToArray();
            _idsOut.Value = matches.Select(p => (double)p.Id).ToArray();
            _memoryOut.Value = matches.Select(p => (double)Memory(p)).ToArray();
            _countOut.Value = (double)matches.Count;
            _runningOut.Value = matches.Count > 0;
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }

        return Task.CompletedTask;
    }

    // A process that exited since the list was taken, or belongs to another user, has no readable size.
    private static long Memory(Process process)
    {
        try
        {
            return process.WorkingSet64;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return 0;
        }
    }
}

internal static class ProcessNames
{
    public static string Clean(string name)
    {
        name = name.Trim();
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }
}

/// <summary>
/// Ends processes by exact name or by id, with their child processes. Destructive, and in a continuous
/// run it fires every tick like every node with side effects, so gate it. Killed reports how many
/// this pass ended; none matching is a result, not an error.
/// </summary>
[NodeDefinition("System/Process Kill", Description = "Ends processes by name or id, with their child processes.")]
public sealed class ProcessKillNode : NodeBase
{
    private string _target = string.Empty;

    private readonly InputPort _targetIn;
    private readonly OutputPort _killedOut;

    public ProcessKillNode()
    {
        // "any": a name, an id, or a list of either (Process List's Names or Ids).
        _targetIn = AddInput("Process", PortTypeCompatibility.Any);
        _killedOut = AddOutput<double>("Killed");

        AddRunGate();
    }

    /// <summary>A process name (with or without .exe) or an id.</summary>
    public string Target { get => _target; set => Set(ref _target, value ?? string.Empty); }

    public override async Task EvaluateAsync(NodeEvaluationContext context)
    {
        var targets = _targetIn.IsConnected ? NodeValues.Items(_targetIn.Value) : [Target];
        var own = Environment.ProcessId;
        var killed = 0;

        foreach (var target in targets)
        {
            var processes = target switch
            {
                double id => Find((int)id),
                _ when int.TryParse(NodeValues.ToText(target).Trim(), out var id) => Find(id),
                _ => Process.GetProcessesByName(ProcessNames.Clean(NodeValues.ToText(target))),
            };

            foreach (var process in processes)
            {
                using (process)
                {
                    // Never this app, whatever the name or id says.
                    if (process.Id == own)
                        continue;

                    try
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync(context.CancellationToken).ConfigureAwait(false);
                        killed++;
                    }
                    catch (InvalidOperationException)
                    {
                        // It exited on its own in the meantime.
                    }
                }
            }
        }

        _killedOut.Value = (double)killed;
    }

    private static Process[] Find(int id)
    {
        try
        {
            return [Process.GetProcessById(id)];
        }
        catch (ArgumentException)
        {
            return [];
        }
    }
}

/// <summary>
/// Reads a value from the Windows registry. The key is written as regedit shows it, hive first
/// (HKEY_LOCAL_MACHINE\SOFTWARE\… or HKLM\SOFTWARE\…); an empty value name reads the key's default
/// value. A missing key or value is a result (Found = false). Only reads: nothing here writes.
/// </summary>
[NodeDefinition("System/Registry Read", Description = "Reads a Windows registry value, e.g. HKLM\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion ProductName.")]
public sealed class RegistryReadNode : NodeBase
{
    private string _key = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion";
    private string _valueName = "ProductName";

    private readonly InputPort _keyIn;
    private readonly InputPort _valueNameIn;
    private readonly OutputPort _valueOut;
    private readonly OutputPort _kindOut;
    private readonly OutputPort _foundOut;

    public RegistryReadNode()
    {
        _keyIn = AddInput<string>("Key");
        _valueNameIn = AddInput<string>("Value Name");
        _valueOut = AddOutput("Value", PortTypeCompatibility.Any);
        _kindOut = AddOutput<string>("Kind");
        _foundOut = AddOutput<bool>("Found");

        AddRunGate();
    }

    public string Key { get => _key; set => Set(ref _key, value ?? string.Empty); }

    public string ValueName { get => _valueName; set => Set(ref _valueName, value ?? string.Empty); }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The registry exists only on Windows.");

        (_valueOut.Value, _kindOut.Value, _foundOut.Value) = Read(_keyIn.ValueOr(Key), _valueNameIn.ValueOr(ValueName));
        return Task.CompletedTask;
    }

    [SupportedOSPlatform("windows")]
    private static (object? Value, string? Kind, bool Found) Read(string path, string valueName)
    {
        path = path.Trim().Trim('\\');
        var separator = path.IndexOf('\\');
        var hiveName = separator < 0 ? path : path[..separator];
        var subKey = separator < 0 ? string.Empty : path[(separator + 1)..];

        using var hive = hiveName.ToUpperInvariant() switch
        {
            "HKEY_LOCAL_MACHINE" or "HKLM" => Microsoft.Win32.Registry.LocalMachine,
            "HKEY_CURRENT_USER" or "HKCU" => Microsoft.Win32.Registry.CurrentUser,
            "HKEY_CLASSES_ROOT" or "HKCR" => Microsoft.Win32.Registry.ClassesRoot,
            "HKEY_USERS" or "HKU" => Microsoft.Win32.Registry.Users,
            "HKEY_CURRENT_CONFIG" or "HKCC" => Microsoft.Win32.Registry.CurrentConfig,
            _ => throw new InvalidOperationException($"'{hiveName}' is not a registry hive; start the key with HKLM, HKCU or another."),
        };

        using var key = subKey.Length == 0 ? hive : hive.OpenSubKey(subKey);
        if (key?.GetValue(valueName.Trim()) is not { } raw)
            return (null, null, false);

        var kind = key.GetValueKind(valueName.Trim());
        object value = raw switch
        {
            int number => (double)number,
            long number => (double)number,
            string[] lines => lines,
            byte[] bytes => bytes,
            _ => raw.ToString() ?? string.Empty,
        };

        return (value, kind.ToString(), true);
    }
}

/// <summary>Reads the clipboard's text, through the application: a graph run outside it has none.</summary>
[NodeDefinition("System/Clipboard Get", Description = "Reads the text on the clipboard.")]
public sealed class ClipboardGetNode : NodeBase
{
    private readonly OutputPort _out;

    public ClipboardGetNode()
    {
        _out = AddOutput<string>("Text");

        AddRunGate();
    }

    public override async Task EvaluateAsync(NodeEvaluationContext context)
    {
        var host = context.Host ?? throw new InvalidOperationException("The clipboard is available only when the graph runs in vNoder.");
        _out.Value = await host.GetClipboardTextAsync(context.CancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Puts text on the clipboard. In a continuous run it does so on every tick like any node with side
/// effects, which would fight whatever the user copies; gate it with Changed.
/// </summary>
[NodeDefinition("System/Clipboard Set", Description = "Copies text (or any value, as text) to the clipboard.")]
public sealed class ClipboardSetNode : NodeBase
{
    private readonly InputPort _in;

    public ClipboardSetNode()
    {
        _in = AddInput("Value", PortTypeCompatibility.Any);

        AddRunGate();
    }

    public override async Task EvaluateAsync(NodeEvaluationContext context)
    {
        var host = context.Host ?? throw new InvalidOperationException("The clipboard is available only when the graph runs in vNoder.");
        await host.SetClipboardTextAsync(NodeValues.ToText(_in.Value), context.CancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Shows a Windows notification, so a continuous run can say when something happened while the window
/// is out of sight. Gate it (Changed, a Compare into Run If): ungated, it would notify every tick.
/// </summary>
[NodeDefinition("System/Notify", Description = "Shows a desktop notification, optionally with a sound; gate it with Run If so it fires only when something happens.")]
public sealed class NotifyNode : NodeBase
{
    private string _caption = "vNoder";
    private string _message = string.Empty;
    private bool _sound = true;

    private readonly InputPort _titleIn;
    private readonly InputPort _messageIn;

    public NotifyNode()
    {
        _titleIn = AddInput<string>("Title");
        _messageIn = AddInput("Message", PortTypeCompatibility.Any);

        AddRunGate();
    }

    // Not "Title": that is the node's own name on the canvas, inherited from NodeBase.
    public string Caption { get => _caption; set => Set(ref _caption, value ?? string.Empty); }

    public string Message { get => _message; set => Set(ref _message, value ?? string.Empty); }

    public bool Sound { get => _sound; set => Set(ref _sound, value); }

    public override async Task EvaluateAsync(NodeEvaluationContext context)
    {
        var host = context.Host ?? throw new InvalidOperationException("Notifications are available only when the graph runs in vNoder.");
        var message = _messageIn.IsConnected ? NodeValues.ToText(_messageIn.Value) : Message;
        await host.NotifyAsync(_titleIn.ValueOr(Caption), message, Sound, context.CancellationToken).ConfigureAwait(false);
    }
}
