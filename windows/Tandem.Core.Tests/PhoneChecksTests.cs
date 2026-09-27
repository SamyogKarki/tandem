using Tandem.Core.Devices;

namespace Tandem.Core.Tests;

public class PhoneChecksTests
{
    [Fact]
    public void Allowed_input_probe_is_not_blocked() =>
        Assert.False(PhoneChecks.IsInputBlocked("__TANDEM_RC=0\n")); // Xiaomi Pad 7 with control allowed

    [Theory]
    [InlineData("Exception occurred while executing 'keyevent':\njava.lang.SecurityException: Injecting input events requires the caller (or the source of the instrumentation, if any) to have the INJECT_EVENTS permission.\n__TANDEM_RC=255\n")]
    [InlineData("java.lang.SecurityException: blocked\n__TANDEM_RC=0\n")]
    [InlineData("__TANDEM_RC=1\n")]
    public void Blocked_input_probe_is_detected(string output) =>
        Assert.True(PhoneChecks.IsInputBlocked(output));

    [Theory]
    [InlineData("Xiaomi", true)]
    [InlineData("xiaomi", true)]
    [InlineData("Samsung", false)]
    [InlineData("Google", false)]
    public void Xiaomi_family_is_recognised_by_manufacturer(string manufacturer, bool expected)
    {
        var info = new PhoneInfo("s", "s", "Phone", "M", manufacturer, "16", 36, PhoneTransport.Wifi);
        Assert.Equal(expected, PhoneChecks.IsXiaomi(info));
    }
}
