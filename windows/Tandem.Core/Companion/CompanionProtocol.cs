using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Tandem.Core.Companion;

public sealed record NotificationAction(int Index, string Title, bool IsReply);

/// <summary>A notification as the companion app reports it (see NotificationMapper.kt).</summary>
public sealed record PhoneNotification(
    string Key,
    string Package,
    string AppName,
    string Title,
    string Text,
    string SubText,
    DateTimeOffset When,
    string Category,
    byte[]? Image,
    IReadOnlyList<NotificationAction> Actions,
    bool IsIncomingCall = false)
{
    public NotificationAction? ReplyAction => Actions.FirstOrDefault(a => a.IsReply);

    public static PhoneNotification FromJson(JsonObject n)
    {
        var actions = (n["actions"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Select(a => new NotificationAction(
                (int?)a["i"] ?? 0,
                (string?)a["title"] ?? "",
                (bool?)a["reply"] ?? false))
            .ToList();
        var img = (string?)n["img"];
        return new PhoneNotification(
            (string?)n["key"] ?? throw new InvalidDataException("Notification without key"),
            (string?)n["pkg"] ?? "",
            (string?)n["app"] ?? "",
            (string?)n["title"] ?? "",
            (string?)n["text"] ?? "",
            (string?)n["sub"] ?? "",
            DateTimeOffset.FromUnixTimeMilliseconds((long?)n["when"] ?? 0),
            (string?)n["cat"] ?? "",
            string.IsNullOrEmpty(img) ? null : Convert.FromBase64String(img),
            actions,
            (string?)n["call"] == "incoming");
    }
}

/// <summary>
/// Framing and authentication for the PC ⇄ companion link.
/// Frame: [u32 big-endian length][UTF-8 JSON object]; see PcLink.kt.
/// </summary>
public static class CompanionProtocol
{
    public const int Version = 1;
    private const int MaxFrame = 8 * 1024 * 1024;

    public static async Task WriteAsync(Stream stream, JsonObject message, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(message.ToJsonString());
        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, body.Length);
        body.CopyTo(frame, 4);
        await stream.WriteAsync(frame, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Reads one message; null on a clean end of stream.</summary>
    public static async Task<JsonObject?> ReadAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        var got = await stream.ReadAtLeastAsync(header, 4, throwOnEndOfStream: false, ct).ConfigureAwait(false);
        if (got == 0) return null;
        if (got < 4) throw new EndOfStreamException("Truncated frame header");
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length is < 0 or > MaxFrame) throw new InvalidDataException($"Bad frame length {length}");
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, ct).ConfigureAwait(false);
        return JsonNode.Parse(body) as JsonObject ?? throw new InvalidDataException("Frame is not a JSON object");
    }

    /// <summary>32 random bytes as lowercase hex, shared with the companion at setup.</summary>
    public static string NewSecret() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    public static string NewNonce() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    /// <summary>hex(HMAC-SHA256(secret, "tandem-v1:" + nonce)) — must match Pairing.proof() in Kotlin.</summary>
    public static string Proof(string secretHex, string nonce) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Convert.FromHexString(secretHex), Encoding.UTF8.GetBytes("tandem-v1:" + nonce)));

    public static bool ProofMatches(string secretHex, string nonce, string? proof) =>
        proof is not null &&
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Proof(secretHex, nonce)), Encoding.ASCII.GetBytes(proof));
}
