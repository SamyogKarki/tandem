using System.Globalization;

namespace Tandem.Core.Mirroring;

public sealed record MirrorOptions
{
    /// <summary>Longest side in pixels; 0 keeps the phone's native resolution.</summary>
    public int MaxSize { get; init; } = 1600;
    public int BitRateMbps { get; init; } = 8;
    public int MaxFps { get; init; } = 60;
    public bool ForwardAudio { get; init; } = true;
    public bool TurnScreenOff { get; init; }
    public bool StayAwake { get; init; } = true;
    public bool AlwaysOnTop { get; init; }
    /// <summary>Off while Tandem's own clipboard bridge is running, so the two don't echo each other.</summary>
    public bool ClipboardAutosync { get; init; } = true;
    public string? WindowTitle { get; init; }
    /// <summary>Package name to open by itself in a separate virtual display (Android 11+).</summary>
    public string? StartApp { get; init; }

    public IReadOnlyList<string> ToArguments(string serial)
    {
        var inv = CultureInfo.InvariantCulture;
        var args = new List<string>
        {
            "--serial=" + serial,
            "--video-bit-rate=" + BitRateMbps.ToString(inv) + "M",
            "--max-fps=" + MaxFps.ToString(inv),
        };
        if (MaxSize > 0) args.Add("--max-size=" + MaxSize.ToString(inv));
        if (!ForwardAudio) args.Add("--no-audio");
        if (StayAwake) args.Add("--stay-awake");
        if (AlwaysOnTop) args.Add("--always-on-top");
        if (!ClipboardAutosync) args.Add("--no-clipboard-autosync");
        if (!string.IsNullOrWhiteSpace(WindowTitle)) args.Add("--window-title=" + WindowTitle);

        if (!string.IsNullOrWhiteSpace(StartApp))
        {
            // A resizable window with just this app, leaving the phone's own screen alone.
            args.Add("--new-display");
            args.Add("--flex-display");
            args.Add("--start-app=" + StartApp);
            args.Add("--no-vd-system-decorations");
        }
        else if (TurnScreenOff)
        {
            args.Add("--turn-screen-off");
        }
        return args;
    }
}
