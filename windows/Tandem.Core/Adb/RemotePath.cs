namespace Tandem.Core.Adb;

/// <summary>POSIX path helpers for paths on the phone (always '/'-separated).</summary>
public static class RemotePath
{
    public const string InternalStorage = "/storage/emulated/0";

    public static string Combine(string dir, string name) =>
        dir.EndsWith('/') ? dir + name : dir + "/" + name;

    public static string GetFileName(string path)
    {
        var trimmed = path.TrimEnd('/');
        var i = trimmed.LastIndexOf('/');
        return i < 0 ? trimmed : trimmed[(i + 1)..];
    }

    /// <summary>Parent directory, or null at the root.</summary>
    public static string? GetParent(string path)
    {
        var trimmed = path.TrimEnd('/');
        if (trimmed.Length == 0) return null;
        var i = trimmed.LastIndexOf('/');
        if (i < 0) return null;
        return i == 0 ? "/" : trimmed[..i];
    }

    /// <summary>
    /// A name the phone's filesystem will accept. FAT/exFAT SD cards reject the same
    /// characters Windows does, so the Windows rules are a safe superset.
    /// </summary>
    public static bool IsValidName(string name) =>
        name.Length > 0 && name != "." && name != ".." &&
        name.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|', '\0']) < 0;

    /// <summary>"photo.jpg" -> "photo (2).jpg" when the name is already taken.</summary>
    public static string UniqueName(string name, IReadOnlySet<string> taken)
    {
        if (!taken.Contains(name)) return name;
        var dot = name.LastIndexOf('.');
        var (stem, ext) = dot > 0 ? (name[..dot], name[dot..]) : (name, "");
        for (var n = 2; ; n++)
        {
            var candidate = $"{stem} ({n}){ext}";
            if (!taken.Contains(candidate)) return candidate;
        }
    }
}
