namespace Tandem.Core;

/// <summary>
/// Locations of the bundled third-party binaries: adb (the transport for everything)
/// and scrcpy (screen mirroring, control, and the on-device server used for clipboard sync).
/// </summary>
public sealed record ToolPaths(string AdbExe, string ScrcpyExe, string ScrcpyServer)
{
    /// <summary>
    /// Version of the bundled scrcpy. The on-device server refuses to start unless the
    /// client passes exactly this string, so it must match vendor/scrcpy-win64-v{this}.
    /// </summary>
    public const string ScrcpyVersion = "4.1";

    public string ScrcpyDirectory => Path.GetDirectoryName(ScrcpyExe)!;

    /// <summary>Looks for tools\scrcpy next to the running app.</summary>
    public static ToolPaths Locate(string appDirectory)
    {
        var dir = Path.Combine(appDirectory, "tools", "scrcpy");
        var paths = new ToolPaths(
            Path.Combine(dir, "adb.exe"),
            Path.Combine(dir, "scrcpy.exe"),
            Path.Combine(dir, "scrcpy-server"));

        var missing = new[] { paths.AdbExe, paths.ScrcpyExe, paths.ScrcpyServer }.Where(p => !File.Exists(p)).ToList();
        if (missing.Count > 0)
            throw new FileNotFoundException(
                "Bundled tools are missing (run tools\\fetch-vendor.ps1, then rebuild): " + string.Join(", ", missing));
        return paths;
    }
}
