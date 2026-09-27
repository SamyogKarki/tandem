using System.Text;
using AdvancedSharpAdbClient.Models;
using Tandem.Core.Adb;

namespace Tandem.Core.Devices;

/// <summary>A phone that adb currently has online, plus helpers to run commands on it.</summary>
public sealed class PhoneConnection(AdbHost adb, DeviceData device, PhoneInfo info)
{
    private const string ExitMarker = "__TANDEM_RC=";

    public AdbHost Adb { get; } = adb;
    public DeviceData Device { get; } = device;
    public PhoneInfo Info { get; } = info;
    public string Serial => Device.Serial;

    /// <summary>Runs a shell command and returns its output (stdout and stderr interleaved).</summary>
    public async Task<string> ShellAsync(string command, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        await foreach (var line in Adb.Client.ExecuteRemoteEnumerableAsync(command, Device, Encoding.UTF8, ct).ConfigureAwait(false))
            sb.Append(line).Append('\n');
        return sb.ToString();
    }

    /// <summary>Runs a shell command and throws <see cref="AdbCommandException"/> if it exits non-zero.</summary>
    public async Task<string> ShellCheckedAsync(string command, CancellationToken ct = default)
    {
        var output = await ShellAsync($"{command} 2>&1; echo {ExitMarker}$?", ct).ConfigureAwait(false);
        var (text, code) = SplitExitCode(output);
        if (code != 0)
            throw new AdbCommandException(text.Length > 0 ? text : $"Command failed with exit code {code}");
        return text;
    }

    public static (string Output, int ExitCode) SplitExitCode(string output)
    {
        var i = output.LastIndexOf(ExitMarker, StringComparison.Ordinal);
        if (i < 0) return (output.Trim(), -1);
        var rest = output[(i + ExitMarker.Length)..].Trim();
        return (output[..i].Trim(), int.TryParse(rest, out var code) ? code : -1);
    }

    public async Task<BatteryInfo?> GetBatteryAsync(CancellationToken ct = default) =>
        BatteryInfo.Parse(await ShellAsync("dumpsys battery", ct).ConfigureAwait(false));

    internal static async Task<PhoneInfo> ReadInfoAsync(AdbHost adb, DeviceData device, CancellationToken ct)
    {
        var probe = new PhoneConnection(adb, device, PhoneInfo.FromProps(device.Serial, new Dictionary<string, string>(), null));
        const string split = "__TANDEM_SPLIT__";
        var output = await probe.ShellAsync($"getprop; echo {split}; settings get global device_name", ct).ConfigureAwait(false);
        var parts = output.Split(split);
        var props = PhoneInfo.ParseGetprop(parts[0]);
        var deviceName = parts.Length > 1 ? parts[1].Trim() : null;
        return PhoneInfo.FromProps(device.Serial, props, deviceName);
    }
}
