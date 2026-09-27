using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using Tandem.Core.Devices;

namespace Tandem.App.Services;

/// <summary>
/// The phone Tandem is working with right now, as bindable UI state.
/// Fed by <see cref="DeviceTracker"/>; all property changes happen on the UI thread.
/// </summary>
public sealed partial class PhoneSession : ObservableObject
{
    private readonly DispatcherQueue _ui;
    private readonly SettingsStore _settings;
    private CancellationTokenSource? _batteryCts;
    private CancellationTokenSource? _controlCts;

    public PhoneSession(DeviceTracker tracker, SettingsStore settings, DispatcherQueue ui)
    {
        _ui = ui;
        _settings = settings;
        tracker.Changed += snapshot => _ui.TryEnqueue(() => Apply(snapshot));
    }

    /// <summary>
    /// Xiaomi phones block mouse/keyboard control until "USB debugging (Security settings)" is on.
    /// While blocked we re-check every couple of seconds, so the Home page notices the switch
    /// being flipped without the user having to press anything.
    /// </summary>
    [ObservableProperty]
    public partial bool ControlBlocked { get; private set; }

    /// <summary>Raised when a blocked phone becomes controllable (for a "you're all set" message).</summary>
    public event Action? ControlUnlocked;

    public BrandGuide Brand => Phone is { } p ? BrandGuide.ForManufacturer(p.Info.Manufacturer) : BrandGuide.ById(_settings.Current.PhoneBrand);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnected), nameof(Name), nameof(Details), nameof(TransportGlyph))]
    public partial PhoneConnection? Phone { get; private set; }

    [ObservableProperty]
    public partial bool HasUnauthorizedDevice { get; private set; }

    [ObservableProperty]
    public partial bool AdbReady { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BatteryText), nameof(BatteryGlyph))]
    public partial BatteryInfo? Battery { get; private set; }

    public bool IsConnected => Phone is not null;
    public string Name => Phone?.Info.Name ?? "";

    public string Details
    {
        get
        {
            if (Phone is not { } p) return "";
            var via = p.Info.Transport == PhoneTransport.Usb ? "USB" : "Wi-Fi";
            var maker = p.Info.Manufacturer.Length > 0 && !p.Info.Name.StartsWith(p.Info.Manufacturer, StringComparison.OrdinalIgnoreCase)
                ? p.Info.Manufacturer + " · "
                : "";
            return $"{maker}Android {p.Info.AndroidVersion} · Connected over {via}";
        }
    }

    public string TransportGlyph => Phone?.Info.Transport == PhoneTransport.Usb ? "\uE88E" : "\uE701";

    public string BatteryText => Battery is { } b ? $"{b.Level}%{(b.Charging ? " · Charging" : "")}" : "";

    /// <summary>Segoe Fluent Icons Battery0–10 (EBA0–EBAA) and BatteryCharging0–10 (EBAB–EBB5).</summary>
    public string BatteryGlyph => Battery is { } b
        ? ((char)((b.Charging ? 0xEBAB : 0xEBA0) + Math.Clamp((int)Math.Round(b.Level / 10.0), 0, 10))).ToString()
        : "";

    private void Apply(DeviceSnapshot snapshot)
    {
        HasUnauthorizedDevice = snapshot.HasUnauthorizedDevice;
        var next = snapshot.Phones.FirstOrDefault(p => p.Serial == Phone?.Serial) ?? snapshot.Phones.FirstOrDefault();
        if (next?.Serial == Phone?.Serial) return;

        Phone = next;
        Battery = null;
        ControlBlocked = false;
        _batteryCts?.Cancel();
        _controlCts?.Cancel();
        if (next is not null)
        {
            _batteryCts = new CancellationTokenSource();
            _ = PollBatteryAsync(next, _batteryCts.Token);
            _controlCts = new CancellationTokenSource();
            _ = WatchControlAsync(next, _controlCts.Token);

            // Remember the phone: next time Home says "Looking for …" instead of the full guide.
            _settings.Current.LastPhoneName = next.Info.Name;
            _settings.Current.PhoneBrand = BrandGuide.ForManufacturer(next.Info.Manufacturer).Id;
            _settings.Save();
        }
    }

    private async Task WatchControlAsync(PhoneConnection phone, CancellationToken ct)
    {
        var wasBlocked = false;
        while (!ct.IsCancellationRequested)
        {
            bool allowed;
            try
            {
                allowed = await PhoneChecks.CanControlAsync(phone, ct);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                return; // can't tell; don't nag
            }
            catch (OperationCanceledException)
            {
                return;
            }
            _ui.TryEnqueue(() =>
            {
                if (ct.IsCancellationRequested) return;
                ControlBlocked = !allowed;
                if (allowed && wasBlocked) ControlUnlocked?.Invoke();
            });
            if (allowed) return;
            wasBlocked = true;
            try { await Task.Delay(TimeSpan.FromSeconds(2), ct); } catch (OperationCanceledException) { return; }
        }
    }

    private async Task PollBatteryAsync(PhoneConnection phone, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var battery = await phone.GetBatteryAsync(ct);
                _ui.TryEnqueue(() => { if (!ct.IsCancellationRequested) Battery = battery; });
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // Transient adb hiccup; the tracker will notice a real disconnect.
            }
            try { await Task.Delay(TimeSpan.FromSeconds(45), ct); } catch (OperationCanceledException) { return; }
        }
    }
}
