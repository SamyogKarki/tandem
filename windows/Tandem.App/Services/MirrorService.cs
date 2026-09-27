using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using Tandem.Core;
using Tandem.Core.Devices;
using Tandem.Core.Mirroring;

namespace Tandem.App.Services;

public sealed partial class MirrorService(ToolPaths tools, SettingsStore settings, ClipboardService clipboard, DispatcherQueue ui)
    : ObservableObject
{
    private MirrorSession? _session;

    [ObservableProperty]
    public partial bool IsMirroring { get; private set; }

    /// <summary>Raised on the UI thread, at most once per problem kind per session.</summary>
    public event Action<MirrorProblem>? ProblemDetected;

    public void Start(PhoneConnection phone)
    {
        if (_session is { IsRunning: true }) return;
        var options = settings.Current.ToMirrorOptions(phone.Info.Name, clipboard.IsBridgeRunning);
        var session = MirrorSession.Start(tools, phone.Serial, options);
        var reported = new HashSet<MirrorProblem>();
        session.ProblemDetected += p => ui.TryEnqueue(() =>
        {
            if (reported.Add(p)) ProblemDetected?.Invoke(p);
        });
        session.Exited += s => ui.TryEnqueue(() =>
        {
            if (_session != s) return;
            _session = null;
            IsMirroring = false;
            s.Dispose();
        });
        _session = session;
        IsMirroring = true;
    }

    public async Task StopAsync()
    {
        if (_session is { } s) await s.StopAsync();
        foreach (var app in _appWindows.ToList()) await app.StopAsync();
    }

    private readonly List<MirrorSession> _appWindows = [];

    /// <summary>
    /// Opens one phone app in its own window on the PC (a separate virtual display, so the
    /// phone's own screen is left alone). Used when a notification is clicked.
    /// </summary>
    public void OpenApp(PhoneConnection phone, string package, string title)
    {
        var options = settings.Current.ToMirrorOptions(title, clipboard.IsBridgeRunning) with { StartApp = package };
        var session = MirrorSession.Start(tools, phone.Serial, options);
        _appWindows.Add(session);
        session.Exited += s => ui.TryEnqueue(() =>
        {
            _appWindows.Remove(s);
            s.Dispose();
        });
    }
}
