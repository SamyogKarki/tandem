using Tandem.Core.Adb;
using Tandem.Core.Devices;
using Tandem.Core.Pairing;

namespace Tandem.Core.Tests;

public class ParsingTests
{
    [Fact]
    public void Mdns_list_parses_pairing_and_connect_services()
    {
        const string output = """
            List of discovered mdns services
            adb-2A221FDH2003RV-vWgGlL	_adb-tls-connect._tcp	192.168.1.10:37153
            tandem-abcd2345	_adb-tls-pairing._tcp.	192.168.1.10:41231
            something-else	_googlecast._tcp	192.168.1.3:8009
            """;

        var services = MdnsService.ParseList(output);

        Assert.Equal(2, services.Count);
        Assert.Equal(new MdnsService("adb-2A221FDH2003RV-vWgGlL", MdnsService.ConnectType, "192.168.1.10", 37153), services[0]);
        Assert.Equal(MdnsService.PairingType, services[1].Type);
        Assert.Equal("192.168.1.10:41231", services[1].Endpoint);
    }

    [Fact]
    public void Mdns_endpoint_handles_ipv6()
    {
        Assert.True(MdnsService.TryParseEndpoint("[fe80::1]:5555", out var host, out var port));
        Assert.Equal("fe80::1", host);
        Assert.Equal(5555, port);
        Assert.Equal("[fe80::1]:5555", new MdnsService("x", MdnsService.ConnectType, host, port).Endpoint);
        Assert.False(MdnsService.TryParseEndpoint("192.168.1.2", out _, out _));
        Assert.False(MdnsService.TryParseEndpoint("192.168.1.2:99999", out _, out _));
    }

    [Fact]
    public void Pair_output_yields_guid_or_throws_with_message()
    {
        Assert.Equal("adb-R58M123ABC-XyZ12a",
            AdbHost.ParsePairOutput("Successfully paired to 192.168.1.5:37899 [guid=adb-R58M123ABC-XyZ12a]\n"));

        var ex = Assert.Throws<AdbCommandException>(() => AdbHost.ParsePairOutput("Failed: Wrong password or connection was dropped."));
        Assert.Contains("Wrong password", ex.Message);
    }

    [Fact]
    public void Only_tandems_own_forwards_are_considered_stale()
    {
        const string list = """
            adb-9d397901-NZFVkZ._adb-tls-connect._tcp tcp:60793 localabstract:tandem_companion
            adb-9d397901-NZFVkZ._adb-tls-connect._tcp tcp:55687 localabstract:scrcpy_38095c4f
            emulator-5554 tcp:9222 localabstract:chrome_devtools_remote
            R58M123 tcp:8081 tcp:8081
            """;

        var stale = AdbHost.ParseStaleForwards(list).ToList();

        Assert.Equal(
            [("adb-9d397901-NZFVkZ._adb-tls-connect._tcp", "tcp:60793"), ("adb-9d397901-NZFVkZ._adb-tls-connect._tcp", "tcp:55687")],
            stale);
    }

    [Fact]
    public void Qr_payload_matches_android_studio_format_and_avoids_delimiters()
    {
        for (var i = 0; i < 50; i++)
        {
            var qr = QrPairing.Create();
            Assert.Matches(@"^WIFI:T:ADB;S:tandem-[a-z0-9]{8};P:[a-z0-9]{12};;$", qr.QrPayload);
        }
        Assert.NotEqual(QrPairing.Create().Password, QrPairing.Create().Password);
    }

    [Fact]
    public void Phone_info_prefers_user_device_name_then_marketing_name()
    {
        const string getprop = """
            [ro.build.version.release]: [14]
            [ro.build.version.sdk]: [34]
            [ro.product.manufacturer]: [Xiaomi]
            [ro.product.model]: [23049RAD8C]
            [ro.product.marketname]: [Redmi Note 12 Turbo]
            [ro.serialno]: [a1b2c3d4]
            [persist.sys.empty]: []
            """;
        var props = PhoneInfo.ParseGetprop(getprop);
        Assert.Equal("", props["persist.sys.empty"]);

        var withName = PhoneInfo.FromProps("adb-a1b2c3d4-Qw3rTy._adb-tls-connect._tcp", props, "Samyog's Redmi");
        Assert.Equal("Samyog's Redmi", withName.Name);
        Assert.Equal(PhoneTransport.Wifi, withName.Transport);
        Assert.Equal(34, withName.Sdk);
        Assert.Equal("a1b2c3d4", withName.HardwareSerial);
        Assert.True(withName.SupportsAudio);

        // `settings get` prints "null" when the setting is unset.
        var noName = PhoneInfo.FromProps("a1b2c3d4", props, "null");
        Assert.Equal("Redmi Note 12 Turbo", noName.Name);
        Assert.Equal(PhoneTransport.Usb, noName.Transport);
    }

    [Theory]
    [InlineData("R58M123ABC", PhoneTransport.Usb)]
    [InlineData("192.168.1.20:5555", PhoneTransport.Wifi)]
    [InlineData("adb-R58M-abc._adb-tls-connect._tcp", PhoneTransport.Wifi)]
    [InlineData("emulator-5554", PhoneTransport.Usb)]
    public void Transport_is_derived_from_serial(string serial, PhoneTransport expected) =>
        Assert.Equal(expected, PhoneInfo.TransportOf(serial));

    [Fact]
    public void Battery_parses_level_and_charging()
    {
        const string charging = """
            Current Battery Service state:
              AC powered: false
              USB powered: true
              Wireless powered: false
              status: 2
              level: 85
              scale: 100
            """;
        Assert.Equal(new BatteryInfo(85, true), BatteryInfo.Parse(charging));

        const string discharging = "  AC powered: false\n  USB powered: false\n  status: 3\n  level: 41\n  scale: 100\n";
        Assert.Equal(new BatteryInfo(41, false), BatteryInfo.Parse(discharging));
        Assert.Null(BatteryInfo.Parse("Can't find service: battery"));
    }

    [Fact]
    public void Exit_code_marker_is_split_from_output()
    {
        Assert.Equal(("mkdir: '/x': Permission denied", 1),
            PhoneConnection.SplitExitCode("mkdir: '/x': Permission denied\n__TANDEM_RC=1\n"));
        Assert.Equal(("", 0), PhoneConnection.SplitExitCode("__TANDEM_RC=0\n"));
        Assert.Equal(-1, PhoneConnection.SplitExitCode("no marker").ExitCode);
    }
}
