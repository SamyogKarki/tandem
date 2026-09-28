using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using Tandem.Core;
using Tandem.Core.Clipboard;
using Tandem.Core.Devices;
using Windows.ApplicationModel.DataTransfer;

namespace Tandem.App.Services;

public enum ClipboardState { Off, Starting, On, Error }

/// <summary>
/// Keeps the Windows clipboard and the phone's clipboard in sync (text) while a phone is
/// connected and the setting is on. Restarts the phone-side bridge if it drops.
/// </summary>
public sealed partial class ClipboardService : ObservableObject, IDisposable
{
    /// <summary>Password managers put this format on the clipboard to keep secrets out of sync/history.</summary>
    private const string ExcludeFromMonitoring = "ExcludeClipboardContentFromMonitorProcessing";

    private readonly PhoneSession _session;
    private readonly SettingsStore _settings;
    private readonly ToolPaths _tools;
    private readonly DispatcherQueue _ui;
    private readonly ClipboardWatcher _watcher;
    private ClipboardBridge? _bridge;
    private int _generation;
    private int _failures;

    public ClipboardService(PhoneSession session, SettingsStore settings, ToolPaths tools, DispatcherQueue ui)
    {
        _session = session;
        _settings = settings;
        _tools = tools;
        _ui = ui;
        _watcher = new ClipboardWatcher();
        _watcher.Changed += OnPcClipboardChanged;
        _session.PropertyChanged += OnSessionChanged;
        _sharing = settings.Current.ShareClipboard;
        _settings.Changed += () =>
        {
            if (_settings.Current.ShareClipboard == _sharing) return;
            _sharing = _settings.Current.ShareClipboard;
            _ = RestartAsync();
        };
    }

    private bool _sharing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial ClipboardState State { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial string? ErrorMessage { get; private set; }

    /// <summary>HyperOS is hiding phone-side copies from us; PC → phone still works.</summary>
    [ObservableProperty]
    public partial bool PhoneCopyBlocked { get; private set; }

    public bool IsBridgeRunning => State == ClipboardState.On;

    /// <summary>Runs after the user clicks "Allow" in the clipboard card.</summary>
    public async Task AllowPhoneCopyAsync()
    {
        if (_session.Phone is not { } phone) return;
        await HyperOsClipboardAccess.AllowAsync(phone);
        CrashLog.Info($"clipboard: allowed HyperOS clipboard access on {phone.Info.Name}");
        PhoneCopyBlocked = await HyperOsClipboardAccess.IsBlockedAsync(phone);
        // HyperOS forgets this at every restart; remember the user's choice so we can put it back.
        if (!_settings.Current.ClipboardAllowedPhones.Contains(phone.Info.HardwareSerial))
        {
            _settings.Current.ClipboardAllowedPhones.Add(phone.Info.HardwareSerial);
            _settings.Save();
        }
    }

    public string StatusText => State switch
    {
        ClipboardState.On => "Copy on one, paste on the other.",
        ClipboardState.Starting => "Connecting…",
        ClipboardState.Error => ErrorMessage ?? "Clipboard sharing stopped.",
        _ => _settings.Current.ShareClipboard ? "Waiting for your phone." : "Off",
    };

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PhoneSession.Phone))
        {
            _failures = 0;
            _ = RestartAsync();
        }
    }

    public async Task RestartAsync()
    {
        var generation = Interlocked.Increment(ref _generation);
        await StopBridgeAsync();

        var phone = _session.Phone;
        PhoneCopyBlocked = false;
        if (phone is null || !_settings.Current.ShareClipboard)
        {
            State = ClipboardState.Off;
            OnPropertyChanged(nameof(StatusText));
            return;
        }

        State = ClipboardState.Starting;
        var bridge = new ClipboardBridge(phone, _tools);
        bridge.PhoneClipboardChanged += text =>
        {
            CrashLog.Info($"clipboard: phone -> PC ({text.Length} chars)");
            _ui.TryEnqueue(() => SetPcClipboard(text));
        };
        bridge.Stopped += error => _ui.TryEnqueue(() => OnBridgeStopped(bridge, error, generation));
        try
        {
            await bridge.StartAsync();
        }
        catch (Exception e)
        {
            CrashLog.Info($"clipboard: bridge failed to start on {phone.Info.Name}: {e.Message}");
            await bridge.DisposeAsync();
            if (generation != _generation) return;
            Fail(e.Message, generation);
            return;
        }

        if (generation != _generation)
        {
            await bridge.DisposeAsync();
            return;
        }
        CrashLog.Info($"clipboard: bridge running on {phone.Info.Name}");
        _bridge = bridge;
        _failures = 0;
        ErrorMessage = null;
        State = ClipboardState.On;

        try
        {
            PhoneCopyBlocked = await HyperOsClipboardAccess.IsBlockedAsync(phone);
            if (PhoneCopyBlocked && _settings.Current.ClipboardAllowedPhones.Contains(phone.Info.HardwareSerial))
            {
                // The user allowed this before; HyperOS reset it when the phone restarted.
                await HyperOsClipboardAccess.AllowAsync(phone);
                PhoneCopyBlocked = await HyperOsClipboardAccess.IsBlockedAsync(phone);
                CrashLog.Info("clipboard: HyperOS clipboard access was reset (phone restarted); allowed it again");
            }
            if (PhoneCopyBlocked) CrashLog.Info("clipboard: HyperOS is blocking phone -> PC");
        }
        catch (Exception)
        {
            PhoneCopyBlocked = false; // can't tell; don't nag
        }
    }

    private void OnBridgeStopped(ClipboardBridge bridge, Exception? error, int generation)
    {
        if (bridge != _bridge || generation != _generation) return;
        CrashLog.Info("clipboard: bridge stopped" + (error is null ? "" : ": " + error.Message));
        _bridge = null;
        if (error is null) return;
        Fail(error.Message, generation);
    }

    private void Fail(string message, int generation)
    {
        ErrorMessage = "Clipboard sharing stopped: " + message;
        State = ClipboardState.Error;
        // Retry with backoff while the phone stays connected (e.g. the phone locked, Wi-Fi blipped).
        var delay = TimeSpan.FromSeconds(Math.Min(60, 3 * Math.Pow(2, _failures++)));
        _ = Task.Delay(delay).ContinueWith(_ => _ui.TryEnqueue(() =>
        {
            if (generation == _generation && State == ClipboardState.Error) _ = RestartAsync();
        }), TaskScheduler.Default);
    }

    private async Task StopBridgeAsync()
    {
        var bridge = _bridge;
        _bridge = null;
        if (bridge is not null) await bridge.DisposeAsync();
    }

    private async void OnPcClipboardChanged()
    {
        var bridge = _bridge;
        if (bridge is null) return;
        try
        {
            var text = await ReadPcTextAsync();
            if (text is not null && await bridge.SetPhoneClipboardAsync(text))
                CrashLog.Info($"clipboard: PC -> phone ({text.Length} chars)");
        }
        catch (Exception e)
        {
            // Clipboard locked by another app, or the bridge just dropped; the next copy will retry.
            CrashLog.Info("clipboard: PC -> phone failed: " + e.Message);
        }
    }

    private static async Task<string?> ReadPcTextAsync()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var view = Clipboard.GetContent();
                if (view.Contains(ExcludeFromMonitoring) || !view.Contains(StandardDataFormats.Text)) return null;
                return await view.GetTextAsync();
            }
            catch (Exception) when (attempt < 2)
            {
                await Task.Delay(60); // another app has the clipboard open
            }
        }
        return null;
    }

    private static void SetPcClipboard(string text)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
        }
        catch (Exception)
        {
            // Clipboard busy; dropping one sync is better than crashing.
        }
    }

    public void Dispose()
    {
        _watcher.Dispose();
        _ = StopBridgeAsync();
    }
}
