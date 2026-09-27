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
    private CancellationTokenSource? _batteryCts;

    public PhoneSession(DeviceTracker tracker, DispatcherQueue ui)
    {
        _ui = ui;
        tracker.Changed += snapshot => _ui.TryEnqueue(() => Apply(snapshot));
    }

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
        _batteryCts?.Cancel();
        if (next is not null)
        {
            _batteryCts = new CancellationTokenSource();
            _ = PollBatteryAsync(next, _batteryCts.Token);
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
