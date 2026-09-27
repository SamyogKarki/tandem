using AdvancedSharpAdbClient.Models;

namespace Tandem.Core.Files;

public sealed record RemoteEntry(string Name, string Path, bool IsDirectory, ulong Size, DateTimeOffset Modified)
{
    public bool IsHidden => Name.StartsWith('.');

    public string Extension
    {
        get
        {
            if (IsDirectory) return "";
            var dot = Name.LastIndexOf('.');
            return dot > 0 ? Name[(dot + 1)..].ToLowerInvariant() : "";
        }
    }

    internal static bool IsDirectoryMode(UnixFileStatus mode) =>
        (mode & UnixFileStatus.TypeMask) == UnixFileStatus.Directory;

    internal static bool IsSymlinkMode(UnixFileStatus mode) =>
        (mode & UnixFileStatus.TypeMask) == UnixFileStatus.SymbolicLink;

    internal static bool IsRegularMode(UnixFileStatus mode) =>
        (mode & UnixFileStatus.TypeMask) == UnixFileStatus.Regular;

    /// <summary>Folders first, then case-insensitive natural-ish name order (like Explorer).</summary>
    public static int CompareForDisplay(RemoteEntry a, RemoteEntry b)
    {
        if (a.IsDirectory != b.IsDirectory) return a.IsDirectory ? -1 : 1;
        return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
    }
}
