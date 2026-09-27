using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Tandem.App.Services;

namespace Tandem.App;

public partial class App : Application
{
    private MainWindow? _window;

    public Window? Window => _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            CrashLog.Write("UI", e.Exception, e.Message);
            // One broken handler shouldn't take the whole app (and the phone link) down.
            e.Handled = true;
            _window?.ShowError("Something went wrong: " + e.Message + " (details in " + CrashLog.Directory + ")");
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => CrashLog.Write("AppDomain", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            CrashLog.Write("Task", e.Exception);
            e.SetObserved();
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        AppServices? services = null;
        string? startupError = null;
        try
        {
            services = AppServices.Create(DispatcherQueue.GetForCurrentThread());
        }
        catch (Exception e)
        {
            startupError = e.Message;
        }

        _window = new MainWindow(startupError);
        _window.Closed += async (_, _) =>
        {
            if (services is not null) await services.ShutdownAsync();
        };
        _window.Activate();

        if (services is not null)
            _ = StartServicesAsync(services);
    }

    private async Task StartServicesAsync(AppServices services)
    {
        try
        {
            await services.StartAsync();
        }
        catch (Exception e)
        {
            _window?.ShowFatalError("Tandem couldn't start its connection service (adb). " + e.Message);
        }
    }
}
