using System.Runtime.InteropServices;

namespace Tandem.App.Services;

public sealed record TrayMenuItem(string Text, Action? OnClick, bool Enabled = true, bool Checked = false)
{
    public static readonly TrayMenuItem Separator = new("-", null);
}

/// <summary>
/// The notification-area (system tray) icon. WinUI has no tray API, so this is plain Win32:
/// Shell_NotifyIcon plus a message-only window for its callbacks. Create on the UI thread.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const uint CallbackMessage = 0x8001; // WM_APP + 1
    private const uint WmLButtonUp = 0x0202, WmRButtonUp = 0x0205, WmContextMenu = 0x007B;
    private const uint NimAdd = 0, NimModify = 1, NimDelete = 2;
    private const uint NifMessage = 0x1, NifIcon = 0x2, NifTip = 0x4;
    private const uint MfString = 0x0, MfSeparator = 0x800, MfGrayed = 0x1, MfChecked = 0x8;
    private const uint TpmReturnCmd = 0x100, TpmRightButton = 0x2;
    private static readonly IntPtr HwndMessage = new(-3);

    private readonly WndProc _wndProc;
    private readonly uint _taskbarCreated;
    private readonly IntPtr _icon;
    private readonly Func<IReadOnlyList<TrayMenuItem>> _menu;
    private readonly Action _onClick;
    private IntPtr _hwnd;
    private string _tooltip = "Tandem";

    public TrayIcon(string iconPath, Action onClick, Func<IReadOnlyList<TrayMenuItem>> menu)
    {
        _onClick = onClick;
        _menu = menu;
        _wndProc = WindowProc;
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        _icon = LoadImage(IntPtr.Zero, iconPath, 1 /* IMAGE_ICON */, GetSystemMetrics(49), GetSystemMetrics(50), 0x10 /* LR_LOADFROMFILE */);

        var hInstance = GetModuleHandle(null);
        var wc = new WndClassEx
        {
            cbSize = (uint)Marshal.SizeOf<WndClassEx>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = hInstance,
            lpszClassName = "TandemTrayIcon",
        };
        RegisterClassEx(ref wc);
        _hwnd = CreateWindowEx(0, "TandemTrayIcon", "", 0, 0, 0, 0, 0, HwndMessage, IntPtr.Zero, hInstance, IntPtr.Zero);
        Add();
    }

    public void SetTooltip(string text)
    {
        _tooltip = text.Length > 120 ? text[..120] : text;
        var data = NewData(NifTip);
        Shell_NotifyIcon(NimModify, ref data);
    }

    private void Add()
    {
        var data = NewData(NifMessage | NifIcon | NifTip);
        Shell_NotifyIcon(NimAdd, ref data);
    }

    private NotifyIconData NewData(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
        hWnd = _hwnd,
        uID = 1,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = _icon,
        szTip = _tooltip,
    };

    private IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == CallbackMessage)
        {
            var mouse = (uint)(lParam.ToInt64() & 0xFFFF);
            if (mouse == WmLButtonUp) _onClick();
            else if (mouse is WmRButtonUp or WmContextMenu) ShowMenu();
            return IntPtr.Zero;
        }
        if (msg == _taskbarCreated)
        {
            Add(); // Explorer restarted; put the icon back.
            return IntPtr.Zero;
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void ShowMenu()
    {
        var items = _menu();
        var menu = CreatePopupMenu();
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (ReferenceEquals(item, TrayMenuItem.Separator))
            {
                AppendMenu(menu, MfSeparator, UIntPtr.Zero, null);
                continue;
            }
            var flags = MfString | (item.Enabled ? 0 : MfGrayed) | (item.Checked ? MfChecked : 0);
            AppendMenu(menu, flags, (UIntPtr)(i + 1), item.Text);
        }
        GetCursorPos(out var pt);
        // Required so the menu closes when clicking elsewhere (documented Shell quirk).
        SetForegroundWindow(_hwnd);
        var chosen = TrackPopupMenuEx(menu, TpmReturnCmd | TpmRightButton, pt.X, pt.Y, _hwnd, IntPtr.Zero);
        PostMessage(_hwnd, 0, IntPtr.Zero, IntPtr.Zero);
        DestroyMenu(menu);
        if (chosen > 0 && chosen <= items.Count) items[chosen - 1].OnClick?.Invoke();
    }

    public void Dispose()
    {
        if (_hwnd == IntPtr.Zero) return;
        var data = NewData(0);
        Shell_NotifyIcon(NimDelete, ref data);
        DestroyWindow(_hwnd);
        _hwnd = IntPtr.Zero;
        if (_icon != IntPtr.Zero) DestroyIcon(_icon);
    }

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WndClassEx wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(IntPtr hInst, string name, uint type, int cx, int cy, uint load);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr id, string? text);
    [DllImport("user32.dll")] private static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}

/// <summary>"Start Tandem when you sign in": a per-user Run entry that launches straight into the tray.</summary>
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Tandem";
    public const string BackgroundArg = "--background";

    public static void Apply(bool enabled)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
            key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" {BackgroundArg}");
        else if (key.GetValue(ValueName) is not null)
            key.DeleteValue(ValueName);
    }
}
