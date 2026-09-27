namespace Tandem.Core.Adb;

/// <summary>Quoting for arguments passed to the device's POSIX shell (toybox/mksh).</summary>
public static class ShellQuote
{
    /// <summary>Wraps in single quotes; embedded single quotes become '\''.</summary>
    public static string Quote(string arg) => "'" + arg.Replace("'", "'\\''") + "'";

    public static string Join(IEnumerable<string> args) => string.Join(' ', args.Select(Quote));
}
