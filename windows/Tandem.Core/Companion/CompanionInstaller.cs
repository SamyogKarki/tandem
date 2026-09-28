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
    /// <summary>versionName of the APK this build bundles; must match android/app/build.gradle.kts (a test checks).</summary>
    public const string BundledVersion = "0.3.3";
    public const string ListenerComponent = PackageName + "/" + PackageName + ".NotificationBridgeService";
    private const string PairReceiver = PackageName + "/.PairReceiver";
    private const string PairAction = PackageName + ".PAIR";
    /// <summary>Xiaomi's "Autostart" permission (MIUI app-op AUTO_START).</summary>
    public const int MiuiAutoStartOp = 10008;

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

        // Lets the phone app turn Wireless debugging back on after a restart. Best effort: some
        // phones (Xiaomi without "USB debugging (Security settings)") refuse; the rest still works.
        await GrantReconnectAsync(phone, ct).ConfigureAwait(false);

        progress?.Report("Allowing it to read notifications…");
        await phone.ShellCheckedAsync($"cmd notification allow_listener {ListenerComponent}", ct).ConfigureAwait(false);

        progress?.Report("Keeping it running in the background…");
        // Best effort: these differ between Android skins and aren't all available everywhere.
        foreach (var command in new[]
                 {
                     $"dumpsys deviceidle whitelist +{PackageName}",
                     $"cmd appops set {PackageName} RUN_ANY_IN_BACKGROUND allow",
                 })
        {
            try { await phone.ShellAsync(command + " 2>/dev/null", ct).ConfigureAwait(false); }
            catch (Exception) when (!ct.IsCancellationRequested) { }
        }
        if (PhoneChecks.IsXiaomi(phone.Info))
        {
            // Xiaomi's Security app resets Autostart to off a moment after an install or update,
            // overwriting anything set too early; let it do that first, then set and verify.
            await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
            await EnsureAutostartAsync(phone, ct).ConfigureAwait(false);
        }

        progress?.Report("Linking it to this PC…");
        await PairAsync(phone, secretHex, ct).ConfigureAwait(false);

        progress?.Report("Checking…");
        var status = await GetStatusAsync(phone, ct).ConfigureAwait(false);
        if (!status.ListenerEnabled)
            throw new AdbCommandException(
                "Your phone didn't allow notification access. On the phone, open Settings → Notifications → Notification access (or Device & app notifications) and turn on Tandem.");
    }

    /// <summary>
    /// Hands the companion a new shared secret. Safe to repeat at any time: the broadcast is
    /// addressed to our own package's receiver and only the adb shell may send it, so an app
    /// squatting on the socket can never learn the secret.
    /// </summary>
    public static async Task PairAsync(PhoneConnection phone, string secretHex, CancellationToken ct = default)
    {
        // -f 0x20 = FLAG_INCLUDE_STOPPED_PACKAGES: a freshly installed app counts as stopped.
        var pair = await phone.ShellAsync(
            $"am broadcast -n {PairReceiver} -a {PairAction} -f 0x20 --es secret {secretHex}", ct).ConfigureAwait(false);
        if (!pair.Contains("data=\"paired\"", StringComparison.Ordinal))
            throw new AdbCommandException("The Tandem app on your phone didn't accept this PC. " + pair.Trim());
    }

    /// <summary>
    /// Grants the phone app WRITE_SECURE_SETTINGS, which it uses for one thing only: switching
    /// Wireless debugging back on after a restart or a Wi-Fi drop, on Wi-Fi where this PC has
    /// connected before. Android lets the adb shell grant this "development" permission.
    /// </summary>
    public static async Task<bool> GrantReconnectAsync(PhoneConnection phone, CancellationToken ct = default)
    {
        try
        {
            var output = await phone.ShellAsync(
                $"pm grant {PackageName} android.permission.WRITE_SECURE_SETTINGS 2>&1; echo __TANDEM_RC=$?", ct).ConfigureAwait(false);
            return IsGrantSuccess(output);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }

    public static bool IsGrantSuccess(string output) =>
        output.Contains("__TANDEM_RC=0", StringComparison.Ordinal) &&
        !output.Contains("Exception", StringComparison.Ordinal);

    /// <summary>
    /// Restarts a companion that's installed and allowed but not answering: stop the process,
    /// then withdraw and re-grant notification access, which makes Android bind (and so start)
    /// the listener again.
    /// </summary>
    public static async Task RestartAsync(PhoneConnection phone, CancellationToken ct = default)
    {
        await phone.ShellAsync($"am force-stop {PackageName}", ct).ConfigureAwait(false);
        // On Xiaomi, Android may only start the listener again if Autostart is allowed.
        if (PhoneChecks.IsXiaomi(phone.Info)) await EnsureAutostartAsync(phone, ct).ConfigureAwait(false);
        await phone.ShellAsync($"cmd notification disallow_listener {ListenerComponent}", ct).ConfigureAwait(false);
        await phone.ShellCheckedAsync($"cmd notification allow_listener {ListenerComponent}", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Xiaomi's "Autostart" (MIUI app-op 10008). When it's off, HyperOS refuses to (re)start the
    /// notification listener ("AutoStartManagerService: Reject service"), so the companion stays
    /// dead after an update or a restart. Sets it and reads it back, retrying if something resets it.
    /// </summary>
    public static async Task<bool> EnsureAutostartAsync(PhoneConnection phone, CancellationToken ct = default)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await phone.ShellAsync($"appops set {PackageName} {MiuiAutoStartOp} allow 2>/dev/null", ct).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(1.5), ct).ConfigureAwait(false);
            if (!await IsAutostartBlockedAsync(phone, ct).ConfigureAwait(false)) return true;
        }
        return false;
    }

    public static async Task<bool> IsAutostartBlockedAsync(PhoneConnection phone, CancellationToken ct = default) =>
        PhoneChecks.IsXiaomi(phone.Info) &&
        IsMiuiOpBlocked(await phone.ShellAsync($"appops get {PackageName} {MiuiAutoStartOp}", ct).ConfigureAwait(false), MiuiAutoStartOp);

    /// <summary>Parses `appops get` output such as "MIUIOP(10008): ignore; rejectTime=…".</summary>
    public static bool IsMiuiOpBlocked(string appopsOutput, int op) =>
        Regex.IsMatch(appopsOutput, $@"MIUIOP\({op}\):\s*(ignore|deny|errored)", RegexOptions.IgnoreCase);

    /// <summary>True when the phone runs an older companion than the one this PC app bundles.</summary>
    public static bool IsOutdated(string? installedVersion) =>
        Version.TryParse(installedVersion, out var installed) &&
        Version.TryParse(BundledVersion, out var bundled) &&
        installed < bundled;

    /// <summary>
    /// Wakes the companion before connecting. HyperOS freezes idle background apps, and a
    /// frozen app can't answer its socket; delivering a broadcast thaws it, and the receiver
    /// starts the foreground service that keeps it thawed while the PC is connected.
    /// Only the adb shell (DUMP permission) can send this.
    /// </summary>
    public static Task WakeAsync(PhoneConnection phone, CancellationToken ct = default) =>
        phone.ShellAsync($"am broadcast -n {PackageName}/.WakeReceiver -a {PackageName}.WAKE -f 0x20", ct);

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
