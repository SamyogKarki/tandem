namespace Tandem.Core.Adb;

/// <summary>One line of `adb mdns services`.</summary>
public sealed record MdnsService(string Instance, string Type, string Host, int Port)
{
    public const string PairingType = "_adb-tls-pairing._tcp";
    public const string ConnectType = "_adb-tls-connect._tcp";

    public string Endpoint => Host.Contains(':') ? $"[{Host}]:{Port}" : $"{Host}:{Port}";

    /// <summary>
    /// Parses the output of `adb mdns services`:
    /// <code>
    /// List of discovered mdns services
    /// adb-2A221FDH2003RV-vWgGlL	_adb-tls-connect._tcp	192.168.1.10:37153
    /// </code>
    /// Unrecognised lines are skipped.
    /// </summary>
    public static IReadOnlyList<MdnsService> ParseList(string output)
    {
        var result = new List<MdnsService>();
        foreach (var raw in output.Split('\n'))
        {
            var fields = raw.Trim().Split(['\t', ' '], StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 3) continue;
            var type = fields[1].TrimEnd('.');
            if (!type.StartsWith("_adb", StringComparison.Ordinal)) continue;
            if (!TryParseEndpoint(fields[^1], out var host, out var port)) continue;
            result.Add(new MdnsService(fields[0], type, host, port));
        }
        return result;
    }

    public static bool TryParseEndpoint(string endpoint, out string host, out int port)
    {
        host = "";
        port = 0;
        var colon = endpoint.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(endpoint[(colon + 1)..], out port) || port is <= 0 or > 65535)
            return false;
        host = endpoint[..colon].Trim('[', ']');
        return host.Length > 0;
    }
}
