namespace Tandem.Core.Devices;

/// <summary>
/// Detects phone settings Tandem depends on, so the app can guide people to the one missing
/// switch (and notice the moment it's flipped) instead of failing with an error.
/// Xiaomi, Redmi and POCO phones add two security switches in Developer options on top of
/// stock Android; other phones need neither.
/// </summary>
public static class PhoneChecks
{
    public static bool IsXiaomi(PhoneInfo info) =>
        info.Manufacturer.Equals("Xiaomi", StringComparison.OrdinalIgnoreCase);

    /// <summary>"Install via USB" (Xiaomi) — needed to install the companion app over adb.</summary>
    public static async Task<bool> CanInstallAppsAsync(PhoneConnection phone, CancellationToken ct = default)
    {
        if (!IsXiaomi(phone.Info)) return true;
        var value = await phone.ShellAsync("getprop persist.security.adbinstall", ct).ConfigureAwait(false);
        return value.Trim() == "1";
    }

    /// <summary>
    /// "USB debugging (Security settings)" (Xiaomi) — needed for mouse and keyboard control.
    /// Probed by injecting KEYCODE_UNKNOWN, which Android ignores; when control is blocked the
    /// injection fails with a SecurityException about INJECT_EVENTS.
    /// </summary>
    public static async Task<bool> CanControlAsync(PhoneConnection phone, CancellationToken ct = default)
    {
        if (!IsXiaomi(phone.Info)) return true;
        var output = await phone.ShellAsync("input keyevent 0 2>&1; echo __TANDEM_RC=$?", ct).ConfigureAwait(false);
        return !IsInputBlocked(output);
    }

    public static bool IsInputBlocked(string output)
    {
        if (output.Contains("INJECT_EVENTS", StringComparison.Ordinal) ||
            output.Contains("SecurityException", StringComparison.Ordinal))
            return true;
        var (_, exitCode) = PhoneConnection.SplitExitCode(output);
        return exitCode > 0;
    }

    /// <summary>Opens Developer options on the phone's own screen.</summary>
    public static Task OpenDeveloperOptionsAsync(PhoneConnection phone, CancellationToken ct = default) =>
        phone.ShellAsync("am start -a android.settings.APPLICATION_DEVELOPMENT_SETTINGS", ct);
}
