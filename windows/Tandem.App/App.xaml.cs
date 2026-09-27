using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Tandem.App.Services;

namespace Tandem.App;

public partial class App : Application
{
    private MainWindow? _window;
    private TrayIcon? _tray;
    private AppServices? _services;

    public Window? Window => _window;

    /// <summary>True once the user chose Quit; until then closing the window just hides it.</summary>
    public bool IsExiting { get; private set; }

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
        // Keep running in the tray when the window is closed; Quit exits explicitly.
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;

        string? startupError = null;
        try
        {
            _services = AppServices.Create(DispatcherQueue.GetForCurrentThread());
        }
        catch (Exception e)
        {
            startupError = e.Message;
        }

        _window = new MainWindow(startupError);
        _window.AppWindow.Closing += OnWindowClosing;
        // A second launch (Start menu, taskbar) is redirected here by Program.Main.
        AppInstance.GetCurrent().Activated += (_, _) => _window.DispatcherQueue.TryEnqueue(ShowWindow);

        var commandLine = Environment.GetCommandLineArgs();
        var startHidden = commandLine.Contains(StartupRegistration.BackgroundArg) || commandLine.Contains(ToastActivator.LaunchArg);
        if (_services is not null)
        {
            _services.Toasts.Register();
            _tray = new TrayIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "tandem.ico"), ShowWindow, BuildTrayMenu);
            _services.Session.PropertyChanged += (_, _) => UpdateTrayTooltip();
            try
            {
                StartupRegistration.Apply(_services.Settings.Current.StartWithWindows);
            }
            catch (Exception e)
            {
                CrashLog.Write("Startup", e, "Couldn't update the sign-in entry");
            }
            _ = StartServicesAsync(_services);
        }

        if (!startHidden || _services is null) _window.Activate();
    }

    public void ShowWindow()
    {
        if (_window is null) return;
        _window.AppWindow.Show();
        if (_window.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } p) p.Restore();
        _window.Activate();
    }

    public async Task ExitAsync()
    {
        if (IsExiting) return;
        IsExiting = true;
        _tray?.Dispose();
        _tray = null;
        if (_services is not null) await _services.ShutdownAsync();
        Exit();
    }

    private void OnWindowClosing(AppWindow sender, AppWindowClosingEventArgs e)
    {
        if (IsExiting) return;
        if (_services is null)
        {
            // Nothing runs in the background after a failed start; closing means quit.
            _ = ExitAsync();
            return;
        }
        e.Cancel = true;
        sender.Hide();
        var settings = _services.Settings;
        if (!settings.Current.TrayHintShown)
        {
            _services.Toasts.ShowInfo("Tandem is still running",
                "It keeps your phone connected from the system tray. Right-click the Tandem icon there to quit.");
            settings.Current.TrayHintShown = true;
            settings.Save();
        }
    }

    private IReadOnlyList<TrayMenuItem> BuildTrayMenu()
    {
        var s = _services!;
        var phone = s.Session.Phone;
        var main = _window as MainWindow;
        return
        [
            new("Open Tandem", ShowWindow),
            new(s.Mirror.IsMirroring ? "Stop mirroring" : "Mirror screen", () =>
            {
                if (s.Mirror.IsMirroring) _ = s.Mirror.StopAsync();
                else if (s.Session.Phone is { } p) s.Mirror.Start(p);
            }, Enabled: phone is not null),
            new("Phone files", () =>
            {
                ShowWindow();
                main?.NavigateTo("files");
            }, Enabled: phone is not null),
            TrayMenuItem.Separator,
            new("Show phone notifications", () =>
            {
                s.Settings.Current.ShowNotifications = !s.Settings.Current.ShowNotifications;
                s.Settings.Save();
            }, Checked: s.Settings.Current.ShowNotifications),
            TrayMenuItem.Separator,
            new("Quit Tandem", () => _ = ExitAsync()),
        ];
    }

    private void UpdateTrayTooltip() =>
        _tray?.SetTooltip(_services?.Session.Phone is { } p ? $"Tandem · {p.Info.Name} connected" : "Tandem · No phone connected");

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
