using System.Text.RegularExpressions;
using Tandem.Core.Devices;

namespace Tandem.Core.Clipboard;

/// <summary>
/// Xiaomi's HyperOS/MIUI adds its own clipboard-privacy app-op on top of Android's.
/// When it is "ignore" for the Shell package, the clipboard bridge (which runs as shell)
/// never sees phone-side copies, although PC → phone still works. Verified on a Xiaomi
/// Pad 7, HyperOS 3.0 / Android 16; see also Genymobile/scrcpy#5961.
/// </summary>
public static partial class HyperOsClipboardAccess
{
    public const int MiuiOp = 10053;
    private const string ShellPackage = "com.android.shell";

    /// <summary>True when the phone is blocking the shell from reading its clipboard.</summary>
    public static async Task<bool> IsBlockedAsync(PhoneConnection phone, CancellationToken ct = default) =>
        IsBlocked(await phone.ShellAsync($"appops get {ShellPackage} {MiuiOp}", ct).ConfigureAwait(false));

    /// <summary>Lets the shell read the clipboard. Reverse with `appops set com.android.shell 10053 ignore`.</summary>
    public static Task AllowAsync(PhoneConnection phone, CancellationToken ct = default) =>
        phone.ShellCheckedAsync($"appops set {ShellPackage} {MiuiOp} allow", ct);

    /// <summary>Parses `appops get`; phones without the MIUI op print an error or nothing.</summary>
    public static bool IsBlocked(string appopsOutput) => BlockedLine().IsMatch(appopsOutput);

    [GeneratedRegex(@"MIUIOP\(10053\):\s*(ignore|deny|errored)", RegexOptions.IgnoreCase)]
    private static partial Regex BlockedLine();
}
