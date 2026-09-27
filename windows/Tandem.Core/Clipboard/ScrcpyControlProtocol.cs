using System.Buffers.Binary;
using System.Text;

namespace Tandem.Core.Clipboard;

/// <summary>
/// The subset of scrcpy's control-socket protocol (v4.1) that Tandem speaks.
/// Mirrors server/.../control/ControlMessageReader.java and DeviceMessageWriter.java.
/// All integers are big-endian.
/// </summary>
public static class ScrcpyControlProtocol
{
    // Computer → device
    public const byte TypeGetClipboard = 8;
    public const byte TypeSetClipboard = 9;
    public const byte TypeScanFile = 22;

    // Device → computer
    public const byte DeviceTypeClipboard = 0;
    public const byte DeviceTypeAckClipboard = 1;
    public const byte DeviceTypeUhidOutput = 2;

    private const int MessageMaxSize = 1 << 18;
    /// <summary>type(1) + sequence(8) + paste(1) + length(4)</summary>
    public const int SetClipboardTextMax = MessageMaxSize - 14;

    /// <summary>[9][sequence:i64][paste:u8][len:i32][utf8]</summary>
    public static byte[] EncodeSetClipboard(long sequence, string text, bool paste)
    {
        var utf8 = TruncateUtf8(Encoding.UTF8.GetBytes(text), SetClipboardTextMax);
        var msg = new byte[14 + utf8.Length];
        msg[0] = TypeSetClipboard;
        BinaryPrimitives.WriteInt64BigEndian(msg.AsSpan(1), sequence);
        msg[9] = paste ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt32BigEndian(msg.AsSpan(10), utf8.Length);
        utf8.CopyTo(msg.AsSpan(14));
        return msg;
    }

    /// <summary>[8][copyKey:u8] — copyKey 0 = just read the clipboard.</summary>
    public static byte[] EncodeGetClipboard() => [TypeGetClipboard, 0];

    /// <summary>[22][len:i32][utf8 path]</summary>
    public static byte[] EncodeScanFile(string path)
    {
        var utf8 = Encoding.UTF8.GetBytes(path);
        var msg = new byte[5 + utf8.Length];
        msg[0] = TypeScanFile;
        BinaryPrimitives.WriteInt32BigEndian(msg.AsSpan(1), utf8.Length);
        utf8.CopyTo(msg.AsSpan(5));
        return msg;
    }

    /// <summary>Cuts at a UTF-8 character boundary so we never send half a code point.</summary>
    public static byte[] TruncateUtf8(byte[] utf8, int maxBytes)
    {
        if (utf8.Length <= maxBytes) return utf8;
        var end = maxBytes;
        while (end > 0 && (utf8[end] & 0xC0) == 0x80) end--; // back up over continuation bytes
        return utf8[..end];
    }
}

public abstract record DeviceMessage
{
    public sealed record ClipboardText(string Text) : DeviceMessage;
    public sealed record ClipboardAck(long Sequence) : DeviceMessage;
    public sealed record Ignored(byte Type) : DeviceMessage;

    /// <summary>Reads one message; returns null on a clean end of stream.</summary>
    public static async Task<DeviceMessage?> ReadAsync(Stream stream, CancellationToken ct)
    {
        var type = new byte[1];
        if (await stream.ReadAtLeastAsync(type, 1, throwOnEndOfStream: false, ct).ConfigureAwait(false) == 0)
            return null;

        switch (type[0])
        {
            case ScrcpyControlProtocol.DeviceTypeClipboard:
            {
                var len = BinaryPrimitives.ReadInt32BigEndian(await ReadExactlyAsync(stream, 4, ct).ConfigureAwait(false));
                if (len is < 0 or > 1 << 18) throw new InvalidDataException($"Bad clipboard length {len}");
                var text = Encoding.UTF8.GetString(await ReadExactlyAsync(stream, len, ct).ConfigureAwait(false));
                return new ClipboardText(text);
            }
            case ScrcpyControlProtocol.DeviceTypeAckClipboard:
                return new ClipboardAck(BinaryPrimitives.ReadInt64BigEndian(await ReadExactlyAsync(stream, 8, ct).ConfigureAwait(false)));
            case ScrcpyControlProtocol.DeviceTypeUhidOutput:
            {
                var header = await ReadExactlyAsync(stream, 4, ct).ConfigureAwait(false); // id:u16, len:u16
                var size = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2));
                await ReadExactlyAsync(stream, size, ct).ConfigureAwait(false);
                return new Ignored(type[0]);
            }
            default:
                throw new InvalidDataException($"Unknown device message type {type[0]}");
        }
    }

    private static async Task<byte[]> ReadExactlyAsync(Stream stream, int count, CancellationToken ct)
    {
        var buffer = new byte[count];
        await stream.ReadExactlyAsync(buffer, ct).ConfigureAwait(false);
        return buffer;
    }
}
