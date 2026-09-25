using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using vNoder.Core;

namespace vNoder.App.Presentation;

/// <summary>
/// The clipboard and desktop notifications, for the nodes that ask for them through
/// <see cref="NodeEvaluationContext.Host"/>. Both need the UI thread (the clipboard is an STA
/// resource, the notification icon belongs to the window), so every call is marshalled there.
/// </summary>
/// <remarks>
/// Notifications are Shell_NotifyIcon balloons, which Windows 10 and 11 show as toasts: the one kind
/// an unpackaged desktop app can raise without registering an app identity. That needs a tray icon,
/// added on the first notification and removed when the dispatcher shuts down. It is called directly
/// rather than through WinForms' NotifyIcon, which cannot silence the sound (NIIF_NOSOUND).
/// </remarks>
public sealed class DesktopNodeHost : INodeHost
{
    private const int IconId = 1;

    private readonly Dispatcher _dispatcher;
    private IntPtr _iconWindow;

    public DesktopNodeHost(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _dispatcher.ShutdownStarted += (_, _) => RemoveIcon();
    }

    public Task<string> GetClipboardTextAsync(CancellationToken cancellationToken) =>
        OnUiThread(() => Retry(() => Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty), cancellationToken);

    public Task SetClipboardTextAsync(string text, CancellationToken cancellationToken) =>
        OnUiThread(() => Retry(() =>
        {
            // SetDataObject with copy: the text stays on the clipboard after vNoder exits.
            Clipboard.SetDataObject(text, copy: true);
            return true;
        }), cancellationToken);

    public Task NotifyAsync(string title, string message, bool sound, CancellationToken cancellationToken) =>
        OnUiThread(() =>
        {
            var window = Application.Current?.MainWindow is { } main ? new WindowInteropHelper(main).Handle : IntPtr.Zero;
            if (window == IntPtr.Zero)
                throw new InvalidOperationException("There is no window to show the notification from.");

            var data = NewData(window);
            if (_iconWindow == IntPtr.Zero)
            {
                data.uFlags = NifIcon | NifTip;
                data.hIcon = AppIcon();
                data.szTip = "vNoder";
                if (!ShellNotifyIcon(NimAdd, ref data))
                    throw new InvalidOperationException("Windows refused the notification icon.");

                _iconWindow = window;
                data = NewData(window);
            }

            data.uFlags = NifInfo;
            data.szInfoTitle = Truncate(title.Length > 0 ? title : "vNoder", 63);

            // An empty message would remove the balloon rather than show it.
            data.szInfo = Truncate(message.Length > 0 ? message : " ", 255);
            data.dwInfoFlags = NiifInfo | (sound ? 0 : NiifNoSound);
            return ShellNotifyIcon(NimModify, ref data)
                ? true
                : throw new InvalidOperationException("Windows refused the notification.");
        }, cancellationToken);

    private Task<T> OnUiThread<T>(Func<T> action, CancellationToken cancellationToken) =>
        _dispatcher.InvokeAsync(action, DispatcherPriority.Normal, cancellationToken).Task;

    /// <summary>
    /// The clipboard is shared by every process, and another one holding it open makes the call fail
    /// (CLIPBRD_E_CANT_OPEN); that clears within milliseconds, so try a few times before giving up.
    /// </summary>
    private static T Retry<T>(Func<T> action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return action();
            }
            catch (COMException) when (attempt < 5)
            {
                Thread.Sleep(20 * attempt);
            }
            catch (COMException exception)
            {
                throw new InvalidOperationException($"The clipboard is in use by another program: {exception.Message}");
            }
        }
    }

    private void RemoveIcon()
    {
        if (_iconWindow == IntPtr.Zero)
            return;

        var data = NewData(_iconWindow);
        ShellNotifyIcon(NimDelete, ref data);
        _iconWindow = IntPtr.Zero;
    }

    private static NotifyIconData NewData(IntPtr window) => new()
    {
        cbSize = Marshal.SizeOf<NotifyIconData>(),
        hWnd = window,
        uID = IconId,
        szTip = string.Empty,
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..length];

    /// <summary>The executable's own icon, or Windows' generic application icon when it has none.</summary>
    private static IntPtr AppIcon()
    {
        if (Environment.ProcessPath is { } path && ExtractIcon(IntPtr.Zero, path, 0) is var icon && icon > 1)
            return icon;

        return LoadIcon(IntPtr.Zero, IdiApplication);
    }

    private const int NimAdd = 0;
    private const int NimModify = 1;
    private const int NimDelete = 2;
    private const int NifIcon = 0x02;
    private const int NifTip = 0x04;
    private const int NifInfo = 0x10;
    private const int NiifInfo = 0x01;
    private const int NiifNoSound = 0x10;
    private static readonly IntPtr IdiApplication = new(32512);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public int dwState;
        public int dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        public int uTimeoutOrVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellNotifyIcon(int message, ref NotifyIconData data);

    [DllImport("shell32.dll", EntryPoint = "ExtractIconW", CharSet = CharSet.Unicode)]
    private static extern IntPtr ExtractIcon(IntPtr instance, string path, int index);

    [DllImport("user32.dll", EntryPoint = "LoadIconW")]
    private static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);
}
