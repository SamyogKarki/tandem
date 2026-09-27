using System.Text.RegularExpressions;
using Tandem.Core.Adb;
using Tandem.Core.Devices;

namespace Tandem.Core.Companion;

public sealed record CompanionStatus(bool Installed, string? Version, bool ListenerEnabled);

/// <summary>
/// One-click setup of the companion app, done entirely over adb so the user never has to
/// sideload an APK or dig through Android settings:
/// install → notification access → background exemptions → hand over the shared secret.
/// </summary>
public static partial class CompanionInstaller
{
    public const string PackageName = "io.github.samyogkarki.tandem";
    public const string ListenerComponent = PackageName + "/" + PackageName + ".NotificationBridgeService";
    private const string PairReceiver = PackageName + "/.PairReceiver";
    private const string PairAction = PackageName + ".PAIR";
    /// <summary>Xiaomi's "Autostart" permission (MIUI app-op AUTO_START).</summary>
    private const int MiuiAutoStartOp = 10008;

    public static async Task<CompanionStatus> GetStatusAsync(PhoneConnection phone, CancellationToken ct = default)
    {
        const string split = "__TANDEM_SPLIT__";
        var output = await phone.ShellAsync(
            $"dumpsys package {PackageName} | grep versionName; echo {split}; settings get secure enabled_notification_listeners", ct)
            .ConfigureAwait(false);
        return ParseStatus(output, split);
    }

    public static CompanionStatus ParseStatus(string output, string split = "__TANDEM_SPLIT__")
    {
        var parts = output.Split(split);
        var version = VersionName().Match(parts[0]) is { Success: true } m ? m.Groups["v"].Value : null;
        var listeners = parts.Length > 1 ? parts[1] : "";
        var enabled = listeners.Split(':', StringSplitOptions.TrimEntries)
            .Any(c => c == ListenerComponent || c == PackageName + "/.NotificationBridgeService");
        return new CompanionStatus(version is not null, version, enabled);
    }

    public static async Task InstallAsync(PhoneConnection phone, string apkPath, string secretHex,
        IProgress<string>? progress, CancellationToken ct = default)
    {
        progress?.Report("Installing the Tandem app on your phone…");
        // -r replace, -d allow downgrade (dev builds), -g grant runtime permissions (POST_NOTIFICATIONS).
        var install = await phone.Adb.RunAsync(["-s", phone.Serial, "install", "-r", "-d", "-g", apkPath],
            TimeSpan.FromMinutes(3), ct).ConfigureAwait(false);
        if (!install.Combined.Contains("Success", StringComparison.Ordinal))
            throw new AdbCommandException(ExplainInstallFailure(install.Combined));

        progress?.Report("Allowing it to read notifications…");
        await phone.ShellCheckedAsync($"cmd notification allow_listener {ListenerComponent}", ct).ConfigureAwait(false);

        progress?.Report("Keeping it running in the background…");
        // Best effort: these differ between Android skins and aren't all available everywhere.
        foreach (var command in new[]
                 {
                     $"dumpsys deviceidle whitelist +{PackageName}",
                     $"cmd appops set {PackageName} RUN_ANY_IN_BACKGROUND allow",
                     $"appops set {PackageName} {MiuiAutoStartOp} allow",
                 })
        {
            try { await phone.ShellAsync(command + " 2>/dev/null", ct).ConfigureAwait(false); }
            catch (Exception) when (!ct.IsCancellationRequested) { }
        }

        progress?.Report("Linking it to this PC…");
        // -f 0x20 = FLAG_INCLUDE_STOPPED_PACKAGES: a freshly installed app counts as stopped.
        var pair = await phone.ShellAsync(
            $"am broadcast -n {PairReceiver} -a {PairAction} -f 0x20 --es secret {secretHex}", ct).ConfigureAwait(false);
        if (!pair.Contains("data=\"paired\"", StringComparison.Ordinal))
            throw new AdbCommandException("The Tandem app on your phone didn't accept this PC. " + pair.Trim());

        progress?.Report("Checking…");
        var status = await GetStatusAsync(phone, ct).ConfigureAwait(false);
        if (!status.ListenerEnabled)
            throw new AdbCommandException(
                "Your phone didn't allow notification access. On the phone, open Settings → Notifications → Notification access (or Device & app notifications) and turn on Tandem.");
    }

    public static async Task OpenNotificationAccessSettingsAsync(PhoneConnection phone, CancellationToken ct = default) =>
        await phone.ShellAsync("am start -a android.settings.ACTION_NOTIFICATION_LISTENER_SETTINGS", ct).ConfigureAwait(false);

    public static string ExplainInstallFailure(string output)
    {
        if (output.Contains("INSTALL_FAILED_USER_RESTRICTED", StringComparison.Ordinal))
            return "Your phone blocked the install. On Xiaomi, Redmi and POCO phones, turn on \"Install via USB\" in Developer options (and tap Install if the phone asks), then try again.";
        if (output.Contains("INSTALL_FAILED_UPDATE_INCOMPATIBLE", StringComparison.Ordinal))
            return "A different copy of the Tandem phone app is already installed. Uninstall \"Tandem\" on the phone, then try again.";
        if (output.Contains("INSTALL_FAILED_INSUFFICIENT_STORAGE", StringComparison.Ordinal))
            return "Your phone is out of storage space.";
        if (output.Contains("INSTALL_FAILED_ABORTED", StringComparison.Ordinal) ||
            output.Contains("INSTALL_CANCELED_BY_USER", StringComparison.Ordinal))
            return "The install was cancelled on the phone. Try again and tap Install when your phone asks.";
        return "Couldn't install the Tandem app on your phone: " + output.Trim();
    }

    [GeneratedRegex(@"versionName=(?<v>\S+)")]
    private static partial Regex VersionName();
}
