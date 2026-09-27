using Microsoft.UI.Dispatching;
using Tandem.Core;
using Tandem.Core.Adb;
using Tandem.Core.Devices;
using Tandem.Core.Pairing;

namespace Tandem.App.Services;

/// <summary>The app's long-lived services, created once at startup.</summary>
public sealed class AppServices
{
    private readonly CancellationTokenSource _cts = new();

    private AppServices(DispatcherQueue ui)
    {
        Ui = ui;
        Tools = ToolPaths.Locate(AppContext.BaseDirectory);
        Settings = new SettingsStore();
        Adb = new AdbHost(Tools);
        Tracker = new DeviceTracker(Adb);
        Pairing = new PairingService(Adb);
        Session = new PhoneSession(Tracker, ui);
        Clipboard = new ClipboardService(Session, Settings, Tools, ui);
        Mirror = new MirrorService(Tools, Settings, Clipboard, ui);
        Transfers = new TransferService(ui);
        Files = new ViewModels.FilesViewModel(Session, Settings);
    }

    public static AppServices Current { get; private set; } = null!;

    public DispatcherQueue Ui { get; }
    public ToolPaths Tools { get; }
    public SettingsStore Settings { get; }
    public AdbHost Adb { get; }
    public DeviceTracker Tracker { get; }
    public PairingService Pairing { get; }
    public PhoneSession Session { get; }
    public ClipboardService Clipboard { get; }
    public MirrorService Mirror { get; }
    public TransferService Transfers { get; }
    public ViewModels.FilesViewModel Files { get; }

    public static AppServices Create(DispatcherQueue ui) => Current = new AppServices(ui);

    /// <summary>Starts the bundled adb server, then watches for phones.</summary>
    public async Task StartAsync()
    {
        await Adb.StartServerAsync(_cts.Token);
        Session.AdbReady = true;
        _ = Task.Run(() => Tracker.RunAsync(_cts.Token));
    }

    public async Task ShutdownAsync()
    {
        _cts.Cancel();
        await Mirror.StopAsync();
        Clipboard.Dispose();
        try
        {
            // Our adb server lives in the app folder; don't leave it running after we exit.
            await Adb.RunAsync(["kill-server"], TimeSpan.FromSeconds(3));
        }
        catch (Exception)
        {
            // Best effort.
        }
    }
}
