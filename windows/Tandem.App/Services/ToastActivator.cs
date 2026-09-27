using System.Runtime.InteropServices;

namespace Tandem.App.Services;

/// <summary>
/// The COM object Windows calls when a Tandem notification (or one of its buttons) is clicked.
/// Unpackaged apps must register one as the AUMID's "CustomActivator"; without it Windows
/// files the toasts silently in the notification centre and never shows a banner.
/// Same mechanism as the Windows Community Toolkit's ToastNotificationManagerCompat.
/// </summary>
[ComVisible(true)]
[Guid(ToastActivator.Clsid)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class ToastActivator : ToastActivator.INotificationActivationCallback
{
    public const string Clsid = "5B2E8C1A-7F4D-4E63-A9B0-3D6C1E8F2A47";
    public const string LaunchArg = "-ToastActivated";

    private const uint ClsctxLocalServer = 4;
    private const uint RegclsMultipleUse = 1;
    private static readonly Guid IUnknown = new("00000000-0000-0000-C000-000000000046");
    private static uint _cookie;

    /// <summary>Raised on a COM thread with the toast's argument string and the user's input.</summary>
    public static event Action<string, IReadOnlyDictionary<string, string>>? Activated;

    public void Activate(string appUserModelId, string invokedArgs, NotificationUserInputData[] data, uint dataCount)
    {
        var input = new Dictionary<string, string>();
        for (var i = 0; i < Math.Min(dataCount, (uint)(data?.Length ?? 0)); i++)
            input[data![i].Key] = data[i].Value;
        Activated?.Invoke(invokedArgs, input);
    }

    /// <summary>Registers the class for this user (so Windows can start Tandem for a click) and for this process.</summary>
    public static void Register(string exePath)
    {
        using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"Software\Classes\CLSID\{{{Clsid}}}\LocalServer32"))
            key.SetValue(null, $"\"{exePath}\" {LaunchArg}");

        if (_cookie != 0) return;
        var clsid = new Guid(Clsid);
        Marshal.ThrowExceptionForHR(CoRegisterClassObject(ref clsid, new Factory(), ClsctxLocalServer, RegclsMultipleUse, out _cookie));
    }

    [ComImport]
    [Guid("53E31837-6600-4A81-9395-75CFFE746F94")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface INotificationActivationCallback
    {
        void Activate(
            [In, MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [In, MarshalAs(UnmanagedType.LPWStr)] string invokedArgs,
            [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 3)] NotificationUserInputData[] data,
            [In, MarshalAs(UnmanagedType.U4)] uint dataCount);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NotificationUserInputData
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string Key;
        [MarshalAs(UnmanagedType.LPWStr)] public string Value;
    }

    [ComImport]
    [Guid("00000001-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IClassFactory
    {
        [PreserveSig] int CreateInstance(IntPtr outer, ref Guid riid, out IntPtr instance);
        [PreserveSig] int LockServer(bool lockServer);
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class Factory : IClassFactory
    {
        private const int ClassENoAggregation = unchecked((int)0x80040110);
        private const int ENoInterface = unchecked((int)0x80004002);

        public int CreateInstance(IntPtr outer, ref Guid riid, out IntPtr instance)
        {
            instance = IntPtr.Zero;
            if (outer != IntPtr.Zero) return ClassENoAggregation;
            if (riid != IUnknown && riid != typeof(INotificationActivationCallback).GUID && riid != new Guid(Clsid))
                return ENoInterface;
            instance = Marshal.GetComInterfaceForObject(new ToastActivator(), typeof(INotificationActivationCallback));
            return 0;
        }

        public int LockServer(bool lockServer) => 0;
    }

    [DllImport("ole32.dll")]
    private static extern int CoRegisterClassObject(ref Guid clsid, [MarshalAs(UnmanagedType.IUnknown)] object unknown,
        uint context, uint flags, out uint cookie);
}
