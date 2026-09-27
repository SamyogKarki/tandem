using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace Tandem.App.Services;

/// <summary>
/// Keeps a Start menu shortcut for Tandem, stamped with its AppUserModelID and toast activator
/// CLSID. Windows only shows notification banners for a desktop (unpackaged) app when a Start
/// menu shortcut carries these; it also makes Tandem findable in Start search.
/// https://learn.microsoft.com/windows/win32/shell/enable-desktop-toast-with-appusermodelid
/// </summary>
internal static class StartMenuShortcut
{
    private static readonly Guid AppUserModelProps = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
    private const uint PidAppUserModelId = 5;
    private const uint PidToastActivatorClsid = 26;
    private const ushort VtLpwstr = 31;
    private const ushort VtClsid = 72;

    public static string ShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Tandem.lnk");

    /// <summary>Creates or updates the shortcut (e.g. when the exe moved). Cheap enough to run at every start.</summary>
    public static void Ensure(string exePath, string aumid, Guid toastActivator)
    {
        var link = (IShellLinkW)new CShellLink();
        link.SetPath(exePath);
        link.SetWorkingDirectory(Path.GetDirectoryName(exePath)!);
        link.SetDescription("Your Android phone on your PC");
        link.SetIconLocation(exePath, 0);

        var store = (IPropertyStore)link;
        SetString(store, PidAppUserModelId, aumid);
        SetClsid(store, PidToastActivatorClsid, toastActivator);
        store.Commit();

        Directory.CreateDirectory(Path.GetDirectoryName(ShortcutPath)!);
        ((IPersistFile)link).Save(ShortcutPath, true);
        Marshal.ReleaseComObject(link);
    }

    private static void SetString(IPropertyStore store, uint pid, string value)
    {
        var key = new PropertyKey(AppUserModelProps, pid);
        var pv = new PropVariant { vt = VtLpwstr, pointer = Marshal.StringToCoTaskMemUni(value) };
        try { store.SetValue(ref key, ref pv); }
        finally { Marshal.FreeCoTaskMem(pv.pointer); }
    }

    private static void SetClsid(IPropertyStore store, uint pid, Guid value)
    {
        var key = new PropertyKey(AppUserModelProps, pid);
        var pv = new PropVariant { vt = VtClsid, pointer = Marshal.AllocCoTaskMem(16) };
        try
        {
            Marshal.Copy(value.ToByteArray(), 0, pv.pointer, 16);
            store.SetValue(ref key, ref pv);
        }
        finally { Marshal.FreeCoTaskMem(pv.pointer); }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private readonly struct PropertyKey(Guid fmtid, uint pid)
    {
        public readonly Guid FormatId = fmtid;
        public readonly uint PropertyId = pid;
    }

    /// <summary>PROPVARIANT holding a pointer value (VT_LPWSTR / VT_CLSID); 24 bytes on x64.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr pointer;
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class CShellLink;

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder file, int max, IntPtr findData, uint flags);
        void GetIDList(out IntPtr idList);
        void SetIDList(IntPtr idList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder name, int max);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder dir, int max);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder args, int max);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder path, int max, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    private interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
        void SetValue(ref PropertyKey key, ref PropVariant value);
        void Commit();
    }
}
