using System.Globalization;
using System.Text.RegularExpressions;

namespace Tandem.Core.Devices;

public enum PhoneTransport { Usb, Wifi }

public sealed partial record PhoneInfo(
    string Serial,
    string HardwareSerial,
    string Name,
    string Model,
    string Manufacturer,
    string AndroidVersion,
    int Sdk,
    PhoneTransport Transport)
{
    /// <summary>Wireless debugging needs Android 11; scrcpy audio forwarding too.</summary>
    public bool SupportsAudio => Sdk >= 30;

    public static PhoneTransport TransportOf(string serial) =>
        serial.Contains("._adb-tls-connect.", StringComparison.Ordinal) || HostPortSerial().IsMatch(serial)
            ? PhoneTransport.Wifi
            : PhoneTransport.Usb;

    /// <summary>
    /// Builds from `getprop` output plus the user-visible device name
    /// (Settings → About phone, `settings get global device_name`).
    /// </summary>
    public static PhoneInfo FromProps(string serial, IReadOnlyDictionary<string, string> props, string? deviceName)
    {
        string Prop(params string[] keys) =>
            keys.Select(k => props.GetValueOrDefault(k)).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? "";

        var model = Prop("ro.product.model");
        var marketName = Prop("ro.product.marketname", "ro.product.vendor.marketname", "ro.config.marketing_name");
        var name = !string.IsNullOrWhiteSpace(deviceName) && deviceName.Trim() != "null"
            ? deviceName.Trim()
            : marketName.Length > 0 ? marketName : model;

        int.TryParse(Prop("ro.build.version.sdk"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var sdk);
        var hwSerial = Prop("ro.serialno", "ro.boot.serialno");

        return new PhoneInfo(
            serial,
            hwSerial.Length > 0 ? hwSerial : serial,
            name.Length > 0 ? name : "Android phone",
            model,
            Capitalize(Prop("ro.product.manufacturer")),
            Prop("ro.build.version.release"),
            sdk,
            TransportOf(serial));
    }

    /// <summary>Parses `getprop` lines of the form "[key]: [value]".</summary>
    public static Dictionary<string, string> ParseGetprop(string output)
    {
        var props = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in GetpropLine().Matches(output))
            props[m.Groups["k"].Value] = m.Groups["v"].Value;
        return props;
    }

    private static string Capitalize(string s) =>
        s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    [GeneratedRegex(@"^\[(?<k>[^\]]+)\]: \[(?<v>[^\]]*)\]", RegexOptions.Multiline)]
    private static partial Regex GetpropLine();

    [GeneratedRegex(@"^[\d.]+:\d+$|^\[[0-9a-fA-F:]+\]:\d+$")]
    private static partial Regex HostPortSerial();
}

public sealed record BatteryInfo(int Level, bool Charging)
{
    /// <summary>Parses `dumpsys battery`.</summary>
    public static BatteryInfo? Parse(string output)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in output.Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            values.TryAdd(line[..colon].Trim(), line[(colon + 1)..].Trim());
        }
        if (!values.TryGetValue("level", out var levelText) || !int.TryParse(levelText, out var level))
            return null;
        if (values.TryGetValue("scale", out var scaleText) && int.TryParse(scaleText, out var scale) && scale > 0 && scale != 100)
            level = (int)Math.Round(level * 100.0 / scale);

        // BatteryManager.BATTERY_STATUS_CHARGING = 2, FULL = 5
        var status = values.GetValueOrDefault("status");
        var powered = values.Where(kv => kv.Key.EndsWith("powered", StringComparison.OrdinalIgnoreCase))
                            .Any(kv => kv.Value.Equals("true", StringComparison.OrdinalIgnoreCase));
        return new BatteryInfo(Math.Clamp(level, 0, 100), status is "2" || (powered && status is not "3" and not "4"));
    }
}
