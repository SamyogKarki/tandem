using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using Tandem.Core.Companion;

namespace Tandem.Core.Tests;

public class CompanionTests
{
    [Fact]
    public async Task Frames_round_trip_with_big_endian_length_prefix()
    {
        var stream = new MemoryStream();
        await CompanionProtocol.WriteAsync(stream, new JsonObject { ["t"] = "reply", ["text"] = "नमस्ते 👋" }, default);

        var bytes = stream.ToArray();
        var length = BinaryPrimitives.ReadInt32BigEndian(bytes);
        Assert.Equal(bytes.Length - 4, length);

        stream.Position = 0;
        var read = await CompanionProtocol.ReadAsync(stream, default);
        Assert.Equal("reply", (string?)read!["t"]);
        Assert.Equal("नमस्ते 👋", (string?)read["text"]);
        Assert.Null(await CompanionProtocol.ReadAsync(stream, default));
    }

    [Fact]
    public async Task Oversized_or_truncated_frames_are_rejected()
    {
        var huge = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(huge, 64 * 1024 * 1024);
        await Assert.ThrowsAsync<InvalidDataException>(() => CompanionProtocol.ReadAsync(new MemoryStream(huge), default));
        await Assert.ThrowsAnyAsync<EndOfStreamException>(() => CompanionProtocol.ReadAsync(new MemoryStream([0, 0]), default));
    }

    [Fact]
    public void Proof_matches_the_kotlin_side()
    {
        // Same inputs as Pairing.proof() in the companion: HMAC-SHA256(secret, "tandem-v1:" + nonce).
        var secret = new string('a', 64);
        var proof = CompanionProtocol.Proof(secret, "00112233445566778899aabbccddeeff");

        var expected = Convert.ToHexStringLower(System.Security.Cryptography.HMACSHA256.HashData(
            Convert.FromHexString(secret), Encoding.UTF8.GetBytes("tandem-v1:00112233445566778899aabbccddeeff")));
        Assert.Equal(expected, proof);
        Assert.Equal(64, proof.Length);
        Assert.True(CompanionProtocol.ProofMatches(secret, "00112233445566778899aabbccddeeff", proof));
        Assert.False(CompanionProtocol.ProofMatches(secret, "different-nonce", proof));
        Assert.False(CompanionProtocol.ProofMatches(secret, "x", null));
    }

    [Fact]
    public void Secrets_are_64_lowercase_hex_and_unique()
    {
        var a = CompanionProtocol.NewSecret();
        Assert.Matches("^[0-9a-f]{64}$", a);
        Assert.NotEqual(a, CompanionProtocol.NewSecret());
    }

    [Fact]
    public void Notification_json_maps_to_record()
    {
        var json = JsonNode.Parse("""
            {"key":"0|com.whatsapp|1|abc|10123","pkg":"com.whatsapp","app":"WhatsApp","title":"Mum",
             "text":"Dinner at 7?","sub":"","when":1790000000000,"cat":"msg","img":"iVBORw0=",
             "actions":[{"i":0,"title":"Reply","reply":true},{"i":1,"title":"Mark as read","reply":false}]}
            """)!.AsObject();

        var n = PhoneNotification.FromJson(json);

        Assert.Equal("WhatsApp", n.AppName);
        Assert.Equal("Dinner at 7?", n.Text);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1790000000000), n.When);
        Assert.Equal(new NotificationAction(0, "Reply", true), n.ReplyAction);
        Assert.Equal(2, n.Actions.Count);
        Assert.NotNull(n.Image);
        Assert.False(n.IsIncomingCall);
    }

    [Fact]
    public void Incoming_calls_are_flagged()
    {
        var json = JsonNode.Parse("""
            {"key":"k","pkg":"com.whatsapp","app":"WhatsApp","title":"Mum","text":"Incoming voice call","when":0,"cat":"call",
             "call":"incoming","actions":[{"i":0,"title":"Decline","reply":false},{"i":1,"title":"Answer","reply":false}]}
            """)!.AsObject();
        Assert.True(PhoneNotification.FromJson(json).IsIncomingCall);
    }

    [Fact]
    public void Status_parses_version_and_listener_state()
    {
        const string output = """
                versionName=0.2.0
            __TANDEM_SPLIT__
            com.android.systemui/.SomeListener:io.github.samyogkarki.tandem/io.github.samyogkarki.tandem.NotificationBridgeService
            """;
        Assert.Equal(new CompanionStatus(true, "0.2.0", true), CompanionInstaller.ParseStatus(output));
        Assert.Equal(new CompanionStatus(false, null, false), CompanionInstaller.ParseStatus("__TANDEM_SPLIT__\nnull\n"));
    }

    [Fact]
    public void Bundled_version_matches_the_android_build()
    {
        // Walk up from the test output to the repo root, then read the companion's versionName.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "android", "app", "build.gradle.kts"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var gradle = File.ReadAllText(Path.Combine(dir!.FullName, "android", "app", "build.gradle.kts"));
        var match = System.Text.RegularExpressions.Regex.Match(gradle, "versionName = \"([^\"]+)\"");
        Assert.True(match.Success);
        Assert.Equal(match.Groups[1].Value, CompanionInstaller.BundledVersion);
    }

    [Theory]
    // Real output from the Xiaomi Pad 7 right after an update, when HyperOS refused to start the listener:
    [InlineData("MIUIOP(10008): ignore; time=+1h2m12s943ms ago; rejectTime=+1m33s184ms ago", true)]
    [InlineData("MIUIOP(10008): allow; time=+1h2m33s627ms ago; rejectTime=+1m53s868ms ago", false)]
    [InlineData("Error: Unknown operation string: 10008", false)] // not a Xiaomi phone
    public void Xiaomi_autostart_block_is_detected(string output, bool blocked) =>
        Assert.Equal(blocked, CompanionInstaller.IsMiuiOpBlocked(output, CompanionInstaller.MiuiAutoStartOp));

    [Fact]
    public void Older_companions_are_flagged_for_update()
    {
        Assert.True(CompanionInstaller.IsOutdated("0.0.1"));
        Assert.False(CompanionInstaller.IsOutdated(CompanionInstaller.BundledVersion));
        Assert.False(CompanionInstaller.IsOutdated("99.0.0"));
        Assert.False(CompanionInstaller.IsOutdated(null));
        Assert.False(CompanionInstaller.IsOutdated("garbage"));
    }

    [Theory]
    [InlineData("Failure [INSTALL_FAILED_USER_RESTRICTED: Install canceled by user]", "Install via USB")]
    [InlineData("Failure [INSTALL_FAILED_UPDATE_INCOMPATIBLE: signatures do not match]", "Uninstall")]
    [InlineData("something odd", "something odd")]
    public void Install_failures_are_explained(string output, string expectedFragment) =>
        Assert.Contains(expectedFragment, CompanionInstaller.ExplainInstallFailure(output));

    [Theory]
    [InlineData("\"on\"", ReconnectState.On)]
    [InlineData("\"off\"", ReconnectState.SwitchedOff)]
    [InlineData("\"noPermission\"", ReconnectState.NoPermission)]
    [InlineData("null", ReconnectState.Unsupported)] // companion 0.2.x doesn't send it
    [InlineData("true", ReconnectState.Unsupported)]
    public void Reconnect_state_is_read_from_hello(string json, ReconnectState expected) =>
        Assert.Equal(expected, CompanionConnection.ParseReconnect(JsonNode.Parse(json)));

    [Theory]
    [InlineData("__TANDEM_RC=0\n", true)]
    [InlineData("Exception occurred while executing 'grant':\njava.lang.SecurityException: grantRuntimePermission: Neither user 2000 nor current process has android.permission.GRANT_RUNTIME_PERMISSIONS.\n__TANDEM_RC=255\n", false)]
    [InlineData("java.lang.SecurityException: something\n__TANDEM_RC=0\n", false)]
    public void Permission_grant_result_is_parsed(string output, bool granted) =>
        Assert.Equal(granted, CompanionInstaller.IsGrantSuccess(output));
}
