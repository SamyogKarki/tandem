namespace Tandem.App;

/// <summary>
/// Plain-text logs in %LOCALAPPDATA%\Tandem\logs: errors.log for unhandled errors and
/// tandem.log for connection events. Never log clipboard or file contents.
/// </summary>
internal static class CrashLog
{
    private const long MaxBytes = 1 << 20;
    private static readonly Lock Gate = new();

    public static string Directory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tandem", "logs");

    public static void Write(string source, Exception? exception, string? message = null) =>
        Append("errors.log", $"{source}: {message}\n{exception}\n");

    public static void Info(string message) => Append("tandem.log", message);

    private static void Append(string file, string text)
    {
        try
        {
            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(Directory);
                var path = Path.Combine(Directory, file);
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                    File.Move(path, path + ".1", overwrite: true);
                File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {text}\n");
            }
        }
        catch (IOException)
        {
            // Never let logging take the app down.
        }
    }
}
