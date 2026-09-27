using System.Runtime.InteropServices;

namespace Tandem.App.Services;

/// <summary>
/// Notifies on every Windows clipboard change, including while Tandem is in the background
/// (the WinRT Clipboard.ContentChanged event only fires reliably for the focused app).
/// Uses a message-only window registered with AddClipboardFormatListener.
/// Must be created on the UI thread, whose message loop delivers WM_CLIPBOARDUPDATE.
/// </summary>
internal sealed class ClipboardWatcher : IDisposable
{
    private const uint WmClipboardUpdate = 0x031D;
    private static readonly IntPtr HwndMessage = new(-3);
    private const string ClassName = "TandemClipboardWatcher";

    private readonly WndProc _wndProc; // must stay referenced while the window exists
    private IntPtr _hwnd;

    public event Action? Changed;

    public ClipboardWatcher()
    {
        _wndProc = WindowProc;
        var hInstance = GetModuleHandle(null);
        var wc = new WndClassEx
        {
            cbSize = (uint)Marshal.SizeOf<WndClassEx>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = hInstance,
            lpszClassName = ClassName,
        };
        RegisterClassEx(ref wc); // fails harmlessly if already registered
        _hwnd = CreateWindowEx(0, ClassName, "", 0, 0, 0, 0, 0, HwndMessage, IntPtr.Zero, hInstance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero || !AddClipboardFormatListener(_hwnd))
            throw new InvalidOperationException("Could not listen for clipboard changes (error " + Marshal.GetLastWin32Error() + ").");
    }

    private IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WmClipboardUpdate)
        {
            Changed?.Invoke();
            return IntPtr.Zero;
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hwnd == IntPtr.Zero) return;
        RemoveClipboardFormatListener(_hwnd);
        DestroyWindow(_hwnd);
        _hwnd = IntPtr.Zero;
    }

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

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

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WndClassEx lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
