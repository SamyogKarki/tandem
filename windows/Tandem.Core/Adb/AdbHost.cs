using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using AdvancedSharpAdbClient;

namespace Tandem.Core.Adb;

public sealed record AdbResult(int ExitCode, string StdOut, string StdErr)
{
    public string Combined => (StdOut + "\n" + StdErr).Trim();
}

/// <summary>
/// Owns the local adb server (always the bundled adb.exe) and runs the few adb commands
/// that are simplest through the CLI: mDNS discovery, pairing and connecting.
/// Everything else goes through <see cref="Client"/> over the adb server socket.
/// </summary>
public sealed partial class AdbHost(ToolPaths tools)
{
    public ToolPaths Tools { get; } = tools;
    public AdbClient Client { get; } = new();

    public async Task StartServerAsync(CancellationToken ct = default)
    {
        var server = new AdbServer(Client);
        await server.StartServerAsync(Tools.AdbExe, restartServerIfNewer: false, ct).ConfigureAwait(false);
    }

    public async Task<AdbResult> RunAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(Tools.AdbExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Could not start adb.exe");
        var stdout = proc.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = proc.StandardError.ReadToEndAsync(CancellationToken.None);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException($"adb {string.Join(' ', args)} timed out");
        }
        return new AdbResult(proc.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    public async Task<IReadOnlyList<MdnsService>> GetMdnsServicesAsync(CancellationToken ct = default)
    {
        var r = await RunAsync(["mdns", "services"], TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        return MdnsService.ParseList(r.StdOut);
    }

    /// <summary>Returns the device guid (e.g. "adb-R58M123-AbCdEf") on success.</summary>
    public async Task<string> PairAsync(string endpoint, string password, CancellationToken ct = default)
    {
        var r = await RunAsync(["pair", endpoint, password], TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        return ParsePairOutput(r.Combined);
    }

    public async Task ConnectAsync(string endpoint, CancellationToken ct = default)
    {
        var r = await RunAsync(["connect", endpoint], TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
        // adb connect exits 0 even on failure; the text is the only reliable signal.
        if (!r.Combined.Contains("connected to", StringComparison.OrdinalIgnoreCase) ||
            r.Combined.Contains("failed", StringComparison.OrdinalIgnoreCase))
            throw new AdbCommandException("Could not connect: " + r.Combined);
    }

    public static string ParsePairOutput(string output)
    {
        var m = PairSuccessRegex().Match(output);
        if (!m.Success)
            throw new AdbCommandException(string.IsNullOrWhiteSpace(output) ? "Pairing failed." : output.Trim());
        return m.Groups["guid"].Value;
    }

    [GeneratedRegex(@"Successfully paired to \S+ \[guid=(?<guid>[^\]]+)\]")]
    private static partial Regex PairSuccessRegex();
}

public sealed class AdbCommandException(string message) : Exception(message);
