using System.Text.Json;

namespace Tandem.Core.Companion;

/// <summary>
/// The per-phone secrets shared with the companion at setup, keyed by the phone's hardware
/// serial (stable across USB and Wi-Fi). Stored in %LOCALAPPDATA%\Tandem\companions.json.
/// </summary>
public sealed class CompanionSecrets
{
    private readonly string _path;
    private readonly Lock _lock = new();

    public CompanionSecrets(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tandem");
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "companions.json");
    }

    public string? Get(string hardwareSerial)
    {
        lock (_lock) return Load().GetValueOrDefault(hardwareSerial);
    }

    public void Set(string hardwareSerial, string secretHex)
    {
        lock (_lock)
        {
            var all = Load();
            all[hardwareSerial] = secretHex;
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(all));
            File.Move(tmp, _path, overwrite: true);
        }
    }

    private Dictionary<string, string> Load()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path)) ?? []
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
