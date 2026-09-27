using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using Tandem.Core;
using Tandem.Core.Companion;
using Tandem.Core.Devices;

namespace Tandem.App.Services;

public enum NotificationsState { NoPhone, Unavailable, NotSetUp, SettingUp, Connecting, Connected, Off, Problem }

/// <summary>
/// Phone notifications on the PC: keeps a link to the companion app while a phone is
/// connected, shows new notifications as Windows toasts, and carries replies/clicks back.
/// </summary>
public sealed partial class NotificationService : ObservableObject
{
    private static readonly string IconDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tandem", "icons");

    private readonly PhoneSession _session;
    private readonly SettingsStore _settings;
    private readonly ToolPaths _tools;
    private readonly ToastPresenter _toasts;
    private readonly MirrorService _mirror;
    private readonly DispatcherQueue _ui;
    private readonly CompanionSecrets _secrets = new();
    /// <summary>Toast id → phone notification, so clicks and replies can find their way back.</summary>
    private readonly Dictionary<string, PhoneNotification> _shown = [];
    private CompanionConnection? _link;
    private int _generation;
    private int _failures;
    private bool _showNotifications;

    public NotificationService(PhoneSession session, SettingsStore settings, ToolPaths tools,
        ToastPresenter toasts, MirrorService mirror, DispatcherQueue ui)
    {
        _session = session;
        _settings = settings;
        _tools = tools;
        _toasts = toasts;
        _mirror = mirror;
        _ui = ui;
        _showNotifications = settings.Current.ShowNotifications;
        _toasts.Invoked += (args, input) => _ui.TryEnqueue(() => _ = OnToastInvokedAsync(args, input));
        _session.PropertyChanged += OnSessionChanged;
        _settings.Changed += () =>
        {
            if (_settings.Current.ShowNotifications == _showNotifications) return;
            _showNotifications = _settings.Current.ShowNotifications;
            _ = RestartAsync();
        };
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(CanSetUp), nameof(SetUpLabel), nameof(IsConnected), nameof(IsBusy), nameof(IsSetUp))]
    public partial NotificationsState State { get; private set; }

    /// <summary>Step text while setting up, or what went wrong.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial string? Detail { get; private set; }

    public bool CanSetUp => State is NotificationsState.NotSetUp or NotificationsState.Problem;
    public string SetUpLabel => State == NotificationsState.Problem ? "Try again" : "Set up";
    public bool IsConnected => State == NotificationsState.Connected;
    public bool IsBusy => State is NotificationsState.SettingUp or NotificationsState.Connecting;
    public bool IsSetUp => State is NotificationsState.Connected or NotificationsState.Connecting or NotificationsState.Off;

    public string StatusText => State switch
    {
        NotificationsState.Connected => "Your phone's notifications show up here. Reply right from the notification.",
        NotificationsState.Connecting => "Connecting to the Tandem app on your phone…",
        NotificationsState.SettingUp => Detail ?? "Setting up…",
        NotificationsState.Off => "Off. Turn on to see your phone's notifications here.",
        NotificationsState.Unavailable => "This copy of Tandem was built without the phone app.",
        NotificationsState.Problem => Detail ?? "Something went wrong.",
        NotificationsState.NotSetUp => Detail ?? "See and reply to your phone's notifications on this PC. Takes a few seconds to set up.",
        _ => "",
    };

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PhoneSession.Phone)) return;
        _failures = 0;
        _unansweredStreak = 0;
        _ = RestartAsync();
    }

    public async Task RestartAsync()
    {
        var generation = ++_generation;
        await CloseLinkAsync();
        Detail = null;

        var phone = _session.Phone;
        CompanionOutdated = false;
        if (phone is null) { State = NotificationsState.NoPhone; return; }
        if (_tools.CompanionApk is null) { State = NotificationsState.Unavailable; return; }
        if (!_settings.Current.ShowNotifications) { State = NotificationsState.Off; return; }
        if (_secrets.Get(phone.Info.HardwareSerial) is not { } secret) { State = NotificationsState.NotSetUp; return; }

        State = NotificationsState.Connecting;
        try
        {
            var link = await CompanionConnection.ConnectAsync(phone, secret);
            if (generation != _generation) { await link.DisposeAsync(); return; }
            Attach(link, phone, generation);
            _failures = 0;
            _unansweredStreak = 0;
            CompanionOutdated = CompanionInstaller.IsOutdated(link.AppVersion);
            State = NotificationsState.Connected;
            CrashLog.Info($"notifications: connected to companion {link.AppVersion} on {phone.Info.Name}" +
                          (CompanionOutdated ? $" (update to {CompanionInstaller.BundledVersion} available)" : ""));
        }
        catch (CompanionException e) when (e.Problem is CompanionProblem.NotPaired or CompanionProblem.Untrusted)
        {
            if (generation != _generation) return;
            CrashLog.Info("notifications: " + e.Message);
            Detail = e.Message;
            State = NotificationsState.NotSetUp;
        }
        catch (Exception e)
        {
            if (generation != _generation) return;
            await OnUnavailableAsync(phone, e, generation);
        }
    }

    /// <summary>
    /// Nothing answered. Usually the phone just hasn't started the listener yet, but if the
    /// app was uninstalled or notification access was turned off, say so instead of retrying forever.
    /// </summary>
    private async Task OnUnavailableAsync(PhoneConnection phone, Exception error, int generation)
    {
        CrashLog.Info("notifications: companion unavailable: " + error.Message);
        CompanionStatus? status = null;
        try { status = await CompanionInstaller.GetStatusAsync(phone); } catch (Exception) { }
        if (generation != _generation) return;

        if (status is { Installed: false })
        {
            Detail = "The Tandem app isn't on your phone any more. Set it up again.";
            State = NotificationsState.NotSetUp;
            return;
        }
        if (status is { ListenerEnabled: false })
        {
            Detail = "Notification access for Tandem is turned off on your phone. Choose Set up to turn it back on.";
            State = NotificationsState.Problem;
            return;
        }

        // Xiaomi: with Autostart off, HyperOS refuses to start the listener at all (e.g. after an
        // update or a force-stop). Turn it back on and restart right away.
        var autostartBlocked = false;
        try { autostartBlocked = await CompanionInstaller.IsAutostartBlockedAsync(phone); } catch (Exception) { }
        if (generation != _generation) return;
        if (autostartBlocked && DateTime.UtcNow - _lastRestart > TimeSpan.FromSeconds(30))
        {
            _lastRestart = DateTime.UtcNow;
            CrashLog.Info("notifications: Xiaomi Autostart is off for the phone app; turning it on and restarting it");
            try
            {
                await CompanionInstaller.RestartAsync(phone);
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
            catch (Exception e)
            {
                CrashLog.Info("notifications: restart failed: " + e.Message);
            }
            if (generation != _generation) return;
            _ = RestartAsync();
            return;
        }

        // Installed and allowed, yet silent: the app is stuck or Android stopped it. Restart it
        // ourselves (once in a while) before bothering the user.
        _unansweredStreak++;
        if (_unansweredStreak == 2 && DateTime.UtcNow - _lastRestart > TimeSpan.FromMinutes(2))
        {
            _lastRestart = DateTime.UtcNow;
            CrashLog.Info("notifications: companion not answering; restarting it");
            try
            {
                await CompanionInstaller.RestartAsync(phone);
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
            catch (Exception e)
            {
                CrashLog.Info("notifications: restart failed: " + e.Message);
            }
            if (generation != _generation) return;
            _ = RestartAsync();
            return;
        }
        if (_unansweredStreak >= 5)
        {
            Detail = "The Tandem app on your phone isn't responding. Choose Try again to reinstall it.";
            State = NotificationsState.Problem;
            return;
        }
        State = NotificationsState.Connecting;
        RetryLater(generation);
    }

    private int _unansweredStreak;
    private DateTime _lastRestart = DateTime.MinValue;

    /// <summary>The phone runs an older companion than the one bundled with this PC app.</summary>
    [ObservableProperty]
    public partial bool CompanionOutdated { get; private set; }

    private void RetryLater(int generation)
    {
        var delay = TimeSpan.FromSeconds(Math.Min(60, 2 * Math.Pow(2, _failures++)));
        _ = Task.Delay(delay).ContinueWith(_ => _ui.TryEnqueue(() =>
        {
            if (generation == _generation) _ = RestartAsync();
        }), TaskScheduler.Default);
    }

    private void Attach(CompanionConnection link, PhoneConnection phone, int generation)
    {
        _link = link;
        link.AppIcon += (pkg, png) => SaveIcon(pkg, png);
        link.Posted += n => _ui.TryEnqueue(() => OnPosted(n));
        link.Removed += key => _ui.TryEnqueue(() => OnRemoved(key));
        link.PhoneError += message => CrashLog.Info("notifications: phone reported: " + message);
        link.Closed += error => _ui.TryEnqueue(() =>
        {
            if (_link != link || generation != _generation) return;
            _link = null;
            if (error is null) return;
            CrashLog.Info("notifications: link closed: " + error.Message);
            State = NotificationsState.Connecting;
            RetryLater(generation);
        });
        // Notifications already on the phone are not news; only new ones pop up.
        link.Start();
    }

    private void OnPosted(PhoneNotification n)
    {
        var id = ToastPresenter.IdFor(n.Key);
        // Apps re-post unchanged notifications (ranking changes, re-sorting); don't ping twice.
        if (_shown.TryGetValue(id, out var previous) && previous.Title == n.Title && previous.Text == n.Text)
        {
            _shown[id] = n;
            return;
        }
        _shown[id] = n;
        _toasts.Show(n, IconPath(n.Package));
    }

    private void OnRemoved(string key)
    {
        var id = ToastPresenter.IdFor(key);
        if (_shown.Remove(id, out var n)) _toasts.Remove(key, n.Package);
    }

    private async Task OnToastInvokedAsync(IDictionary<string, string> args, IDictionary<string, string> input)
    {
        if (!args.TryGetValue("id", out var id) || !_shown.TryGetValue(id, out var n)) return;
        var action = args.TryGetValue("action", out var a) ? a : null;
        var index = args.TryGetValue("i", out var raw) && int.TryParse(raw, out var i) ? i : -1;
        try
        {
            switch (action)
            {
                case "open" when _session.Phone is { } phone:
                    _mirror.OpenApp(phone, n.Package, n.AppName);
                    break;
                case "reply" when _link is { } link && input.TryGetValue(ToastPresenter.ReplyInput, out var text) && !string.IsNullOrWhiteSpace(text):
                    await link.ReplyAsync(n.Key, index, text);
                    CrashLog.Info($"notifications: replied to {n.Package} ({text.Length} chars)");
                    break;
                case "act" when _link is { } link:
                    await link.InvokeActionAsync(n.Key, index);
                    break;
            }
        }
        catch (Exception e)
        {
            CrashLog.Info($"notifications: {action} failed: {e.Message}");
            _toasts.ShowInfo("Couldn't reach your phone", e.Message);
        }
    }

    /// <summary>One-click setup: install the phone app over adb, grant access, link it to this PC.</summary>
    public async Task SetUpAsync()
    {
        if (_session.Phone is not { } phone || _tools.CompanionApk is not { } apk) return;
        var generation = ++_generation;
        await CloseLinkAsync();
        State = NotificationsState.SettingUp;
        var progress = new Progress<string>(step => { if (generation == _generation) Detail = step; });
        try
        {
            var secret = CompanionProtocol.NewSecret();
            await CompanionInstaller.InstallAsync(phone, apk, secret, progress);
            _secrets.Set(phone.Info.HardwareSerial, secret);
            CrashLog.Info($"notifications: companion set up on {phone.Info.Name}");
        }
        catch (Exception e)
        {
            if (generation != _generation) return;
            CrashLog.Info("notifications: setup failed: " + e.Message);
            Detail = e.Message;
            State = NotificationsState.Problem;
            return;
        }
        if (!_settings.Current.ShowNotifications)
        {
            _settings.Current.ShowNotifications = true;
            _settings.Save(); // triggers RestartAsync via Changed
        }
        else
        {
            await RestartAsync();
        }
        // Give the listener a moment to come up, then prove it works end to end.
        for (var attempt = 0; attempt < 10 && State != NotificationsState.Connected; attempt++)
            await Task.Delay(500);
        if (State == NotificationsState.Connected) await SendTestAsync();
    }

    public async Task SendTestAsync()
    {
        if (_link is { } link) await link.SendTestNotificationAsync();
    }

    private async Task CloseLinkAsync()
    {
        var link = _link;
        _link = null;
        if (link is not null) await link.DisposeAsync();
    }

    private static string IconPath(string package) => Path.Combine(IconDir, package + ".png");

    private static void SaveIcon(string package, byte[] png)
    {
        try
        {
            Directory.CreateDirectory(IconDir);
            File.WriteAllBytes(IconPath(package), png);
        }
        catch (IOException)
        {
            // Toasts fall back to Tandem's own icon.
        }
    }
}
