using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using Tandem.App.ViewModels;
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
        PausedUntil = settings.Current.NotificationsPausedUntil;
        RefreshMutedApps();
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
        ClearItems();
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
            // The phone app lost or changed its secret (reinstalled, data cleared). Re-pairing over
            // adb is safe (see CompanionInstaller.PairAsync), so try that once before asking the user.
            if (DateTime.UtcNow - _lastRepair > TimeSpan.FromMinutes(5))
            {
                _lastRepair = DateTime.UtcNow;
                try
                {
                    var fresh = CompanionProtocol.NewSecret();
                    await CompanionInstaller.PairAsync(phone, fresh);
                    _secrets.Set(phone.Info.HardwareSerial, fresh);
                    CrashLog.Info("notifications: re-paired with the phone app");
                    if (generation == _generation) _ = RestartAsync();
                    return;
                }
                catch (Exception repairError)
                {
                    CrashLog.Info("notifications: re-pairing failed: " + repairError.Message);
                }
            }
            if (generation != _generation) return;
            Detail = e.Problem == CompanionProblem.Untrusted
                ? "Something other than the Tandem app answered on your phone. Choose Set up to reinstall it."
                : e.Message;
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
    private DateTime _lastRepair = DateTime.MinValue;

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
        Reconnect = link.Reconnect;
        link.ReconnectChanged += state => _ui.TryEnqueue(() => { if (_link == link) Reconnect = state; });
        link.AppIcon += (pkg, png) => SaveIcon(pkg, png);
        link.Snapshot += items => _ui.TryEnqueue(() => OnSnapshot(items));
        link.Posted += n => _ui.TryEnqueue(() => OnPosted(n));
        link.Removed += key => _ui.TryEnqueue(() => OnRemoved(key));
        link.PhoneError += message => CrashLog.Info("notifications: phone reported: " + message);
        link.Closed += error => _ui.TryEnqueue(() =>
        {
            if (_link != link || generation != _generation) return;
            _link = null;
            ClearItems();
            if (error is null) return;
            CrashLog.Info("notifications: link closed: " + error.Message);
            State = NotificationsState.Connecting;
            RetryLater(generation);
        });
        link.Start();
        if (link.Reconnect == ReconnectState.NoPermission) _ = GrantReconnectAsync(link, phone);
    }

    private readonly HashSet<string> _grantTried = [];

    /// <summary>
    /// The phone app can't turn Wireless debugging back on yet (e.g. updated by an older PC app,
    /// or the phone refused before). Grant it now, once per phone per run, and have it re-check.
    /// </summary>
    private async Task GrantReconnectAsync(CompanionConnection link, PhoneConnection phone)
    {
        if (!_grantTried.Add(phone.Info.HardwareSerial)) return;
        var granted = await CompanionInstaller.GrantReconnectAsync(phone);
        CrashLog.Info(granted
            ? "notifications: allowed the phone app to turn Wireless debugging back on"
            : "notifications: the phone didn't allow turning Wireless debugging back on");
        if (granted && _link == link)
        {
            try { await link.RearmReconnectAsync(); } catch (Exception) { /* link closing; next connect reports it */ }
        }
    }

    // ---------- the phone's notification shade, mirrored ----------

    /// <summary>What's in the phone's notification shade right now (muted apps left out), newest first.</summary>
    public ObservableCollection<NotificationItem> Items { get; } = [];

    /// <summary>Apps whose notifications stay on the phone, for the "turned off" list.</summary>
    public ObservableCollection<MutedApp> MutedApps { get; } = [];

    /// <summary>Whether the phone turns Wireless debugging back on by itself (as of the last connection).</summary>
    [ObservableProperty]
    public partial ReconnectState Reconnect { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPaused), nameof(PauseText))]
    public partial DateTime? PausedUntil { get; private set; }

    public bool IsPaused => PausedUntil is { } until && until > DateTime.Now;
    public string PauseText => IsPaused ? $"Pop-ups paused until {PausedUntil!.Value:t}. Calls still ring." : "";

    /// <summary>Notifications already on the phone when we connected: listed, but not news, so no pop-ups.</summary>
    private void OnSnapshot(IReadOnlyList<PhoneNotification> items)
    {
        ClearItems();
        foreach (var n in items.OrderByDescending(n => n.When))
        {
            if (IsMuted(n.Package)) continue;
            _shown[ToastPresenter.IdFor(n.Key)] = n;
            Items.Add(new NotificationItem(n, ExistingIcon(n.Package), ToastPresenter.SaveImage(n.Image, n.Key)));
        }
    }

    private void OnPosted(PhoneNotification n)
    {
        if (IsMuted(n.Package)) return;
        var id = ToastPresenter.IdFor(n.Key);
        var avatar = ToastPresenter.SaveImage(n.Image, n.Key);
        // Apps re-post unchanged notifications (ranking changes, re-sorting); don't ping twice.
        var unchanged = _shown.TryGetValue(id, out var previous) && previous.Title == n.Title && previous.Text == n.Text;
        _shown[id] = n;

        var existing = Items.FirstOrDefault(i => i.Key == n.Key);
        if (existing is not null)
        {
            existing.Update(n, ExistingIcon(n.Package), avatar);
            if (!unchanged && Items.IndexOf(existing) > 0) Items.Move(Items.IndexOf(existing), 0);
        }
        else
        {
            Items.Insert(0, new NotificationItem(n, ExistingIcon(n.Package), avatar));
        }

        if (unchanged) return;
        if (IsPaused && !n.IsIncomingCall) return;
        _toasts.Show(n, ExistingIcon(n.Package), avatar, _settings.Current.ShowMessageText);
    }

    private void OnRemoved(string key)
    {
        var id = ToastPresenter.IdFor(key);
        if (_shown.Remove(id, out var n)) _toasts.Remove(key, n.Package);
        if (Items.FirstOrDefault(i => i.Key == key) is { } item) Items.Remove(item);
    }

    private void ClearItems()
    {
        Items.Clear();
        _shown.Clear();
    }

    private bool IsMuted(string package) => _settings.Current.MutedApps.ContainsKey(package);

    public void MuteApp(string package, string appName)
    {
        _settings.Current.MutedApps[package] = appName;
        _settings.Save();
        foreach (var item in Items.Where(i => i.Package == package).ToList())
        {
            Items.Remove(item);
            _toasts.Remove(item.Key, package);
            _shown.Remove(ToastPresenter.IdFor(item.Key));
        }
        RefreshMutedApps();
    }

    /// <summary>Unmuted apps show up again as they post new notifications.</summary>
    public void UnmuteApp(string package)
    {
        if (!_settings.Current.MutedApps.Remove(package)) return;
        _settings.Save();
        RefreshMutedApps();
    }

    private void RefreshMutedApps()
    {
        MutedApps.Clear();
        foreach (var app in _settings.Current.MutedApps.OrderBy(a => a.Value, StringComparer.CurrentCultureIgnoreCase))
            MutedApps.Add(new MutedApp(app.Key, app.Value));
    }

    public void PauseFor(TimeSpan duration)
    {
        _settings.Current.NotificationsPausedUntil = DateTime.Now + duration;
        _settings.Save();
        PausedUntil = _settings.Current.NotificationsPausedUntil;
        // Lift the pause by itself when it runs out, so the page and tray menu update.
        _ = Task.Delay(duration + TimeSpan.FromSeconds(1)).ContinueWith(_ => _ui.TryEnqueue(() =>
        {
            if (!IsPaused) OnPropertyChanged(nameof(IsPaused));
            OnPropertyChanged(nameof(PauseText));
        }), TaskScheduler.Default);
    }

    public void Resume()
    {
        _settings.Current.NotificationsPausedUntil = null;
        _settings.Save();
        PausedUntil = null;
    }

    // ---------- actions (from the page or from a toast) ----------

    public void Open(NotificationItem item)
    {
        if (_session.Phone is { } phone) _mirror.OpenApp(phone, item.Package, item.AppName);
    }

    public Task DismissAsync(NotificationItem item) => RunAsync("dismiss", l => l.DismissAsync(item.Key));

    public Task DismissAllAsync() => RunAsync("clear all", l => l.DismissAllAsync());

    public Task InvokeAsync(NotificationItem item, NotificationAction action) =>
        RunAsync("action", l => l.InvokeActionAsync(item.Key, action.Index));

    public async Task ReplyAsync(NotificationItem item, string text)
    {
        if (item.Source.ReplyAction is not { } reply || string.IsNullOrWhiteSpace(text)) return;
        await RunAsync("reply", l => l.ReplyAsync(item.Key, reply.Index, text));
        CrashLog.Info($"notifications: replied to {item.Package} from the list ({text.Length} chars)");
    }

    public Task SendTestCallAsync() => RunAsync("test call", l => l.SendTestCallAsync());

    private async Task RunAsync(string what, Func<CompanionConnection, Task> action)
    {
        if (_link is not { } link) return;
        try
        {
            await action(link);
        }
        catch (Exception e)
        {
            CrashLog.Info($"notifications: {what} failed: {e.Message}");
            _toasts.ShowInfo("Couldn't reach your phone", e.Message);
        }
    }

    private async Task OnToastInvokedAsync(IDictionary<string, string> args, IDictionary<string, string> input)
    {
        if (!args.TryGetValue("id", out var id) || !_shown.TryGetValue(id, out var n)) return;
        var action = args.TryGetValue("action", out var a) ? a : null;
        var index = args.TryGetValue("i", out var raw) && int.TryParse(raw, out var i) ? i : -1;
        switch (action)
        {
            case "open" when _session.Phone is { } phone:
                _mirror.OpenApp(phone, n.Package, n.AppName);
                break;
            case "reply" when input.TryGetValue(ToastPresenter.ReplyInput, out var text) && !string.IsNullOrWhiteSpace(text):
                await RunAsync("reply", l => l.ReplyAsync(n.Key, index, text));
                CrashLog.Info($"notifications: replied to {n.Package} ({text.Length} chars)");
                break;
            case "act":
                await RunAsync("action", l => l.InvokeActionAsync(n.Key, index));
                if (n.IsIncomingCall) CrashLog.Info($"notifications: call button {index} pressed for {n.Package}");
                break;
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

    private static string? ExistingIcon(string package) => File.Exists(IconPath(package)) ? IconPath(package) : null;

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
