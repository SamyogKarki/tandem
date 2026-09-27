using System.Security.Cryptography;

namespace Tandem.Core.Pairing;

/// <summary>
/// One wireless-debugging pairing attempt. The phone scans <see cref="QrPayload"/>
/// (Developer options → Wireless debugging → Pair device with QR code), then advertises
/// an mDNS service named <see cref="ServiceName"/> and waits for <see cref="Password"/>.
/// This is the same handshake Android Studio uses.
/// </summary>
public sealed record QrPairing(string ServiceName, string Password)
{
    // No ';', ':', ',' or '\' — they are delimiters in the WIFI: QR format.
    private const string Alphabet = "abcdefghijkmnopqrstuvwxyz23456789";

    public string QrPayload => $"WIFI:T:ADB;S:{ServiceName};P:{Password};;";

    public static QrPairing Create() =>
        new("tandem-" + RandomString(8), RandomString(12));

    private static string RandomString(int length) =>
        string.Create(length, 0, (span, _) =>
        {
            for (var i = 0; i < span.Length; i++)
                span[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        });
}
