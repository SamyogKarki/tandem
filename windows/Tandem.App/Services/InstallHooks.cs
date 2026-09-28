using System.Diagnostics;

namespace Tandem.App.Services;

/// <summary>
/// Run by the Tandem installer (Velopack) before it removes the app: undo everything Tandem set
/// up for itself outside its install folder, so an uninstall leaves nothing pointing at it.
/// Settings and the phone pairing in %LOCALAPPDATA%\Tandem stay, so reinstalling just works.
/// Must finish well within Velopack's 30 s limit and never throw.
/// </summary>
internal static class InstallHooks
{
    public static void BeforeUninstall()
    {
        // Tandem usually sits in the tray. Close it first, or it keeps its files locked (and would
        // re-register itself), and the uninstall leaves the folder behind.
        StopProcessesIn(AppContext.BaseDirectory, "Tandem", "scrcpy");
        Try(() => StartupRegistration.Apply(false));
        Try(() => Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\CLSID\{{{ToastActivator.Clsid}}}", false));
        Try(() => Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\AppUserModelId\" + Program.AppUserModelId, false));
        Try(() => File.Delete(StartMenuShortcut.ShortcutPath));
        // Our adb server runs from the install folder too: ask it to stop, and make sure it did.
        Try(() =>
        {
            var adb = Path.Combine(AppContext.BaseDirectory, "tools", "scrcpy", "adb.exe");
            if (!File.Exists(adb)) return;
            using var kill = Process.Start(new ProcessStartInfo(adb, "kill-server") { CreateNoWindow = true, UseShellExecute = false });
            kill?.WaitForExit(5000);
        });
        StopProcessesIn(AppContext.BaseDirectory, "adb");
    }

    /// <summary>Ends processes with these names whose exe lives under <paramref name="folder"/> (never this one).</summary>
    private static void StopProcessesIn(string folder, params string[] names)
    {
        foreach (var name in names)
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    Try(() =>
                    {
                        if (process.Id == Environment.ProcessId) return;
                        var path = process.MainModule?.FileName;
                        if (path is null || !path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) return;
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit(5000);
                    });
                }
            }
        }
    }

    private static void Try(Action action)
    {
        try { action(); }
        catch (Exception) { /* best effort: never block an uninstall */ }
    }
}
