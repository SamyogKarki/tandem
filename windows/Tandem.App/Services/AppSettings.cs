using System.Text.Json;
using Tandem.Core.Mirroring;

namespace Tandem.App.Services;

public sealed class AppSettings
{
    public int MirrorMaxSize { get; set; } = 1600;
    public int MirrorBitRateMbps { get; set; } = 8;
    public int MirrorMaxFps { get; set; } = 60;
    public bool MirrorAudio { get; set; } = true;
    public bool MirrorScreenOff { get; set; }
    public bool MirrorAlwaysOnTop { get; set; }
    public bool ShareClipboard { get; set; } = true;
    public bool ShowHiddenFiles { get; set; }
    public bool ShowNotifications { get; set; } = true;
    public bool StartWithWindows { get; set; } = true;
    /// <summary>Whether we've told the user that closing the window keeps Tandem in the tray.</summary>
    public bool TrayHintShown { get; set; }
    /// <summary>Name of the last phone that connected; when set, a disconnected Home shows "Looking for …" rather than the full setup guide.</summary>
    public string? LastPhoneName { get; set; }
    /// <summary>BrandGuide id, for brand-specific wording ("OS version" vs "Build number").</summary>
    public string? PhoneBrand { get; set; }
    public string DownloadFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Tandem");

    public MirrorOptions ToMirrorOptions(string windowTitle, bool clipboardBridgeRunning) => new()
    {
        MaxSize = MirrorMaxSize,
        BitRateMbps = MirrorBitRateMbps,
        MaxFps = MirrorMaxFps,
        ForwardAudio = MirrorAudio,
        TurnScreenOff = MirrorScreenOff,
        AlwaysOnTop = MirrorAlwaysOnTop,
        ClipboardAutosync = !clipboardBridgeRunning,
        WindowTitle = windowTitle,
    };
}

/// <summary>Settings live in %LOCALAPPDATA%\Tandem\settings.json.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _path;

    public SettingsStore()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tandem");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "settings.json");
        Current = Load();
    }

    public AppSettings Current { get; }

    public event Action? Changed;

    public void Save()
    {
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(Current, Json));
        File.Move(tmp, _path, overwrite: true);
        Changed?.Invoke();
    }

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), Json) ?? new AppSettings();
        }
        catch (JsonException)
        {
            // Corrupt file: start fresh rather than refusing to launch.
        }
        return new AppSettings();
    }
}
