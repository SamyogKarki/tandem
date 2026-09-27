using System.Buffers.Binary;
using System.Text;
using Tandem.Core.Clipboard;

namespace Tandem.Core.Tests;

public class ClipboardProtocolTests
{
    [Fact]
    public void Set_clipboard_layout_matches_scrcpy_reader()
    {
        var msg = ScrcpyControlProtocol.EncodeSetClipboard(0x0102030405060708, "héllo", paste: true);

        Assert.Equal(9, msg[0]);
        Assert.Equal(0x0102030405060708, BinaryPrimitives.ReadInt64BigEndian(msg.AsSpan(1)));
        Assert.Equal(1, msg[9]);
        var len = BinaryPrimitives.ReadInt32BigEndian(msg.AsSpan(10));
        Assert.Equal(6, len); // é is two bytes
        Assert.Equal("héllo", Encoding.UTF8.GetString(msg, 14, len));
        Assert.Equal(14 + len, msg.Length);
    }

    [Fact]
    public void Scan_file_and_get_clipboard_layout()
    {
        var scan = ScrcpyControlProtocol.EncodeScanFile("/sdcard/a.jpg");
        Assert.Equal(22, scan[0]);
        Assert.Equal(13, BinaryPrimitives.ReadInt32BigEndian(scan.AsSpan(1)));
        Assert.Equal([8, 0], ScrcpyControlProtocol.EncodeGetClipboard());
    }

    [Fact]
    public void Oversized_text_is_truncated_on_a_character_boundary()
    {
        // 3-byte characters: a naive cut would split one in half.
        var text = new string('€', ScrcpyControlProtocol.SetClipboardTextMax);
        var msg = ScrcpyControlProtocol.EncodeSetClipboard(0, text, false);
        var len = BinaryPrimitives.ReadInt32BigEndian(msg.AsSpan(10));

        Assert.True(len <= ScrcpyControlProtocol.SetClipboardTextMax);
        Assert.Equal(0, len % 3);
        Assert.DoesNotContain('\uFFFD', Encoding.UTF8.GetString(msg, 14, len));
    }

    [Fact]
    public async Task Device_messages_round_trip_in_sequence()
    {
        var stream = new MemoryStream();
        void Write(params byte[] b) => stream.Write(b);
        var text = Encoding.UTF8.GetBytes("copied on phone ✓");
        var len = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, text.Length);
        Write(0); Write(len); Write(text);                       // CLIPBOARD
        var seq = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(seq, 42);
        Write(1); Write(seq);                                    // ACK_CLIPBOARD
        Write(2, 0, 7, 0, 3, 9, 9, 9);                           // UHID_OUTPUT id=7, 3 bytes
        stream.Position = 0;

        Assert.Equal(new DeviceMessage.ClipboardText("copied on phone ✓"), await DeviceMessage.ReadAsync(stream, default));
        Assert.Equal(new DeviceMessage.ClipboardAck(42), await DeviceMessage.ReadAsync(stream, default));
        Assert.Equal(new DeviceMessage.Ignored(2), await DeviceMessage.ReadAsync(stream, default));
        Assert.Null(await DeviceMessage.ReadAsync(stream, default));
    }

    [Fact]
    public async Task Unknown_or_truncated_messages_throw()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => DeviceMessage.ReadAsync(new MemoryStream([77]), default));
        await Assert.ThrowsAnyAsync<EndOfStreamException>(() => DeviceMessage.ReadAsync(new MemoryStream([0, 0, 0, 0, 5, 1]), default));
    }

    [Theory]
    [InlineData("MIUIOP(10053): ignore\n", true)]                                  // Xiaomi Pad 7, HyperOS 3.0
    [InlineData("MIUIOP(10053): deny; time=+2m ago\n", true)]
    [InlineData("MIUIOP(10053): allow\n", false)]
    [InlineData("Error: Unknown operation string: 10053\n", false)]                // non-Xiaomi phones
    [InlineData("No operations.\n", false)]
    public void HyperOs_clipboard_block_is_detected(string appopsOutput, bool blocked) =>
        Assert.Equal(blocked, HyperOsClipboardAccess.IsBlocked(appopsOutput));

    [Fact]
    public void Loop_guard_drops_echoes_in_both_directions()
    {
        var guard = new ClipboardLoopGuard();

        Assert.True(guard.ShouldApplyToPc("from phone"));
        Assert.False(guard.ShouldSendToPhone("from phone"));   // PC clipboard event caused by us
        Assert.True(guard.ShouldSendToPhone("from pc"));
        Assert.False(guard.ShouldApplyToPc("from pc"));        // phone listener echo
        Assert.True(guard.ShouldApplyToPc("from phone"));      // copying the old text again is real
        Assert.False(guard.ShouldSendToPhone(""));
    }
}
