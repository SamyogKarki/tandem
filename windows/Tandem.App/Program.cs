using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace Tandem.App;

/// <summary>
/// Custom entry point (DISABLE_XAML_GENERATED_MAIN) so only one Tandem runs: launching it
/// again, e.g. from the Start menu while it sits in the tray, just brings the window back.
/// </summary>
public static class Program
{
    /// <summary>Must match the Start menu shortcut and the toast registration (ToastPresenter).</summary>
    public const string AppUserModelId = "SamyogKarki.Tandem";

    [STAThread]
    private static int Main(string[] args)
    {
        // Before any window exists: ties taskbar grouping, pinning and notifications to one identity.
        SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
        WinRT.ComWrappersSupport.InitializeComWrappers();

        var main = AppInstance.FindOrRegisterForKey("Tandem.Main");
        if (!main.IsCurrent)
        {
            var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
            // Redirect off the STA thread; blocking on it here could deadlock.
            Task.Run(() => main.RedirectActivationToAsync(activation).AsTask()).Wait();
            return 0;
        }

        Application.Start(init =>
        {
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });
        return 0;
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, PreserveSig = false)]
    private static extern void SetCurrentProcessExplicitAppUserModelID(string appId);
}
