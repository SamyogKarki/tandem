using AdvancedSharpAdbClient.Models;
using Tandem.Core.Adb;

namespace Tandem.Core.Devices;

public sealed record DeviceSnapshot(
    IReadOnlyList<PhoneConnection> Phones,
    bool HasUnauthorizedDevice,
    bool HasOfflineDevice)
{
    public static readonly DeviceSnapshot Empty = new([], false, false);
}

/// <summary>
/// Polls adb for devices and keeps one <see cref="PhoneConnection"/> per physical phone.
/// The same phone plugged in over USB while also on wireless debugging shows up twice in
/// adb; we keep the USB transport because it's faster.
/// </summary>
public sealed class DeviceTracker(AdbHost adb)
{
    private readonly Dictionary<string, PhoneInfo> _infoCache = new(StringComparer.Ordinal);
    private DeviceSnapshot _current = DeviceSnapshot.Empty;

    public event Action<DeviceSnapshot>? Changed;
    public DeviceSnapshot Current => _current;

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RefreshAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                // adb server restarting or momentarily unreachable; try again next tick.
                Publish(DeviceSnapshot.Empty);
                try { await adb.StartServerAsync(ct).ConfigureAwait(false); } catch (Exception) when (!ct.IsCancellationRequested) { }
            }
            await Task.Delay(TimeSpan.FromSeconds(1.5), ct).ConfigureAwait(false);
        }
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        var devices = (await adb.Client.GetDevicesAsync(ct).ConfigureAwait(false)).ToList();
        var phones = new List<PhoneConnection>();
        foreach (var d in devices.Where(d => d.State == DeviceState.Online && !d.IsEmpty))
        {
            if (!_infoCache.TryGetValue(d.Serial, out var info))
            {
                info = await PhoneConnection.ReadInfoAsync(adb, d, ct).ConfigureAwait(false);
                _infoCache[d.Serial] = info;
            }
            phones.Add(new PhoneConnection(adb, d, info));
        }

        var deduped = phones
            .GroupBy(p => p.Info.HardwareSerial)
            .Select(g => g.OrderBy(p => p.Info.Transport == PhoneTransport.Usb ? 0 : 1).First())
            .OrderBy(p => p.Info.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var snapshot = new DeviceSnapshot(
            deduped,
            devices.Any(d => d.State is DeviceState.Unauthorized or DeviceState.Authorizing),
            devices.Any(d => d.State == DeviceState.Offline));

        // Forget serials that went away so a re-plugged phone gets fresh info.
        foreach (var gone in _infoCache.Keys.Except(devices.Select(d => d.Serial)).ToList())
            _infoCache.Remove(gone);

        if (!SameAs(_current, snapshot))
            Publish(snapshot);
    }

    private void Publish(DeviceSnapshot snapshot)
    {
        if (SameAs(_current, snapshot)) return;
        _current = snapshot;
        Changed?.Invoke(snapshot);
    }

    private static bool SameAs(DeviceSnapshot a, DeviceSnapshot b) =>
        a.HasUnauthorizedDevice == b.HasUnauthorizedDevice &&
        a.HasOfflineDevice == b.HasOfflineDevice &&
        a.Phones.Select(p => p.Serial).SequenceEqual(b.Phones.Select(p => p.Serial));
}
