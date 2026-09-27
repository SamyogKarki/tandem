using Tandem.Core.Files;

namespace Tandem.App;

/// <summary>Segoe Fluent Icons glyph per file type.</summary>
internal static class FileIcons
{
    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        // images
        ["jpg"] = "\uE91B", ["jpeg"] = "\uE91B", ["png"] = "\uE91B", ["gif"] = "\uE91B", ["webp"] = "\uE91B",
        ["heic"] = "\uE91B", ["heif"] = "\uE91B", ["bmp"] = "\uE91B", ["dng"] = "\uE91B",
        // video
        ["mp4"] = "\uE714", ["mkv"] = "\uE714", ["mov"] = "\uE714", ["3gp"] = "\uE714", ["webm"] = "\uE714", ["avi"] = "\uE714",
        // audio
        ["mp3"] = "\uE8D6", ["m4a"] = "\uE8D6", ["aac"] = "\uE8D6", ["flac"] = "\uE8D6", ["ogg"] = "\uE8D6",
        ["opus"] = "\uE8D6", ["wav"] = "\uE8D6", ["amr"] = "\uE8D6",
        // documents
        ["pdf"] = "\uEA90", ["doc"] = "\uE8A5", ["docx"] = "\uE8A5", ["txt"] = "\uE8A5", ["rtf"] = "\uE8A5",
        ["xls"] = "\uE80A", ["xlsx"] = "\uE80A", ["csv"] = "\uE80A", ["ppt"] = "\uE8A5", ["pptx"] = "\uE8A5",
        // archives and apps
        ["zip"] = "\uF012", ["rar"] = "\uF012", ["7z"] = "\uF012", ["tar"] = "\uF012", ["gz"] = "\uF012",
        ["apk"] = "\uE71D", ["apks"] = "\uE71D", ["xapk"] = "\uE71D",
    };

    public static string GlyphFor(RemoteEntry entry) =>
        entry.IsDirectory ? "\uE8B7" : ByExtension.GetValueOrDefault(entry.Extension, "\uE7C3");
}
