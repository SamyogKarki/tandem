namespace Tandem.Core.Clipboard;

/// <summary>
/// Stops copy loops: setting the PC clipboard fires a PC "changed" event, which would send
/// the same text back to the phone, which fires a phone "changed" event, and so on.
/// Both directions share one "last synced" value; a change equal to it is an echo.
/// </summary>
public sealed class ClipboardLoopGuard
{
    private readonly Lock _lock = new();
    private string? _lastSynced;

    public bool ShouldSendToPhone(string pcText) => Accept(pcText);

    public bool ShouldApplyToPc(string phoneText) => Accept(phoneText);

    private bool Accept(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        lock (_lock)
        {
            if (text == _lastSynced) return false;
            _lastSynced = text;
            return true;
        }
    }
}
