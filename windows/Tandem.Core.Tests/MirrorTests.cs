using Tandem.Core.Mirroring;

namespace Tandem.Core.Tests;

public class MirrorTests
{
    [Fact]
    public void Default_arguments()
    {
        var args = new MirrorOptions { WindowTitle = "Redmi" }.ToArguments("abc123");

        Assert.Equal(
            ["--serial=abc123", "--video-bit-rate=8M", "--max-fps=60", "--max-size=1600", "--stay-awake", "--window-title=Redmi"],
            args);
    }

    [Fact]
    public void Options_map_to_scrcpy_flags()
    {
        var args = new MirrorOptions
        {
            MaxSize = 0, ForwardAudio = false, TurnScreenOff = true, StayAwake = false,
            AlwaysOnTop = true, ClipboardAutosync = false,
        }.ToArguments("s");

        Assert.DoesNotContain(args, a => a.StartsWith("--max-size"));
        Assert.Contains("--no-audio", args);
        Assert.Contains("--turn-screen-off", args);
        Assert.Contains("--always-on-top", args);
        Assert.Contains("--no-clipboard-autosync", args);
        Assert.DoesNotContain("--stay-awake", args);
    }

    [Fact]
    public void App_window_uses_a_flex_virtual_display_and_leaves_the_screen_on()
    {
        var args = new MirrorOptions { StartApp = "com.whatsapp", TurnScreenOff = true }.ToArguments("s");

        Assert.Contains("--new-display", args);
        Assert.Contains("--flex-display", args);
        Assert.Contains("--start-app=com.whatsapp", args);
        Assert.DoesNotContain("--turn-screen-off", args);
    }

    [Theory]
    [InlineData("java.lang.SecurityException: Injecting input events requires the caller (or the source of the instrumentation, if any) to have the INJECT_EVENTS permission.", MirrorProblem.InputBlocked)]
    [InlineData("WARN: Device disconnected", MirrorProblem.DeviceGone)]
    [InlineData("ERROR: Something odd", MirrorProblem.Other)]
    [InlineData("INFO: Renderer: direct3d", MirrorProblem.None)]
    public void Classifies_scrcpy_output(string line, MirrorProblem expected) =>
        Assert.Equal(expected, MirrorSession.Classify(line));
}
