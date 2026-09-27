using AdvancedSharpAdbClient.Models;
using Tandem.Core.Adb;

namespace Tandem.Core.Pairing;

public enum PairingStage
{
    WaitingForScan,
    Pairing,
    Connecting,
    Connected,
}

/// <summary>Pairs a phone over wireless debugging and waits until adb sees it online.</summary>
public sealed class PairingService(AdbHost adb)
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(700);

    /// <summary>
    /// Waits for the phone to scan the QR code, pairs, and returns the connected device serial.
    /// Runs until paired or <paramref name="ct"/> is cancelled.
    /// </summary>
    public async Task<string> PairWithQrAsync(QrPairing qr, IProgress<PairingStage>? progress, CancellationToken ct)
    {
        progress?.Report(PairingStage.WaitingForScan);
        MdnsService service;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var services = await adb.GetMdnsServicesAsync(ct).ConfigureAwait(false);
            var match = services.FirstOrDefault(s => s.Type == MdnsService.PairingType && s.Instance == qr.ServiceName);
            if (match is not null)
            {
                service = match;
                break;
            }
            await Task.Delay(PollInterval, ct).ConfigureAwait(false);
        }

        progress?.Report(PairingStage.Pairing);
        var guid = await adb.PairAsync(service.Endpoint, qr.Password, ct).ConfigureAwait(false);
        return await WaitForConnectionAsync(guid, service.Host, progress, ct).ConfigureAwait(false);
    }

    /// <summary>Fallback: "Pair device with pairing code" on the phone shows an IP:port and a 6-digit code.</summary>
    public async Task<string> PairWithCodeAsync(string endpoint, string code, IProgress<PairingStage>? progress, CancellationToken ct)
    {
        if (!MdnsService.TryParseEndpoint(endpoint.Trim(), out var host, out _))
            throw new ArgumentException("Enter the IP address and port shown on the phone, like 192.168.1.20:37123.");
        progress?.Report(PairingStage.Pairing);
        var guid = await adb.PairAsync(endpoint.Trim(), code.Trim(), ct).ConfigureAwait(false);
        return await WaitForConnectionAsync(guid, host, progress, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// After pairing, adb's mDNS auto-connect normally brings the device online within a
    /// couple of seconds. If it doesn't, connect explicitly to the advertised endpoint.
    /// </summary>
    private async Task<string> WaitForConnectionAsync(string guid, string host, IProgress<PairingStage>? progress, CancellationToken ct)
    {
        progress?.Report(PairingStage.Connecting);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        var triedExplicit = false;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var devices = await adb.Client.GetDevicesAsync(ct).ConfigureAwait(false);
            var online = devices.FirstOrDefault(d => d.State == DeviceState.Online &&
                (d.Serial.StartsWith(guid, StringComparison.Ordinal) || d.Serial.StartsWith(host + ":", StringComparison.Ordinal)));
            if (online is not null && !online.IsEmpty)
            {
                progress?.Report(PairingStage.Connected);
                return online.Serial;
            }

            if (!triedExplicit && DateTime.UtcNow > deadline - TimeSpan.FromSeconds(26))
            {
                var services = await adb.GetMdnsServicesAsync(ct).ConfigureAwait(false);
                var connect = services.FirstOrDefault(s => s.Type == MdnsService.ConnectType && s.Instance == guid)
                              ?? services.FirstOrDefault(s => s.Type == MdnsService.ConnectType && s.Host == host);
                if (connect is not null)
                {
                    triedExplicit = true;
                    try { await adb.ConnectAsync(connect.Endpoint, ct).ConfigureAwait(false); }
                    catch (AdbCommandException) { /* keep waiting for auto-connect */ }
                }
            }
            await Task.Delay(PollInterval, ct).ConfigureAwait(false);
        }
        throw new TimeoutException("Paired, but the phone didn't come online. Make sure Wireless debugging is still on and both devices are on the same Wi-Fi.");
    }
}
