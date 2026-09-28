using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using Velopack;
using Velopack.Sources;

namespace Tandem.App.Services;

public enum UpdateState { NotInstalled, Idle, Checking, UpToDate, Downloading, Ready, Failed }

/// <summary>
/// Keeps an installed Tandem up to date from GitHub Releases (Velopack). New versions download
/// quietly in the background; the user restarts Tandem when it suits them, and if they never do,
/// Velopack finishes the update the next time Tandem starts. Does nothing in a developer build.
/// </summary>
public sealed partial class UpdateService : ObservableObject
{
    public const string RepoUrl = "https://github.com/SamyogKarki/tandem";
    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    private readonly UpdateManager? _manager;
    private readonly DispatcherQueue _ui;
    private int _checking;

    public UpdateService(DispatcherQueue ui)
    {
        _ui = ui;
        try
        {
            // TANDEM_UPDATE_SOURCE points at a local folder of releases, to try an update before publishing it.
            IUpdateSource source = Environment.GetEnvironmentVariable("TANDEM_UPDATE_SOURCE") is { Length: > 0 } folder
                ? new SimpleFileSource(new DirectoryInfo(folder))
                : new GithubSource(RepoUrl, null, false);
            var manager = new UpdateManager(source);
            if (manager.IsInstalled) _manager = manager;
        }
        catch (Exception e)
        {
            CrashLog.Info("updates: unavailable: " + e.Message);
        }

        if (_manager is null)
        {
            State = UpdateState.NotInstalled;
        }
        else if (_manager.UpdatePendingRestart is { } pending)
        {
            ReadyVersion = pending.Version.ToString();
            State = UpdateState.Ready;
        }
        else
        {
            State = UpdateState.Idle;
        }
    }

    /// <summary>A new version has downloaded and needs a restart (raised on the UI thread).</summary>
    public event Action? UpdateReady;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(IsReady), nameof(CanCheck))]
    public partial UpdateState State { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial string? ReadyVersion { get; private set; }

    public bool IsReady => State == UpdateState.Ready;
    public bool CanCheck => State is UpdateState.Idle or UpdateState.UpToDate or UpdateState.Failed;

    public string StatusText => State switch
    {
        UpdateState.NotInstalled => "Updates: this copy wasn't installed with the Tandem installer, so it doesn't update itself.",
        UpdateState.Checking => "Checking for updates…",
        UpdateState.UpToDate => "Tandem is up to date.",
        UpdateState.Downloading => "Downloading an update…",
        UpdateState.Ready => $"Tandem {ReadyVersion} is ready. Restart Tandem to finish updating.",
        UpdateState.Failed => "Couldn't check for updates. Tandem will try again later.",
        _ => "Tandem checks for updates by itself.",
    };

    /// <summary>Checks shortly after startup, then every few hours.</summary>
    public void Start()
    {
        if (_manager is null) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(FirstCheckDelay);
            while (true)
            {
                await CheckAsync();
                await Task.Delay(CheckInterval);
            }
        });
    }

    public Task CheckNowAsync() => _manager is null ? Task.CompletedTask : Task.Run(CheckAsync);

    private async Task CheckAsync()
    {
        if (_manager is null || IsReadyOnUi() || Interlocked.Exchange(ref _checking, 1) == 1) return;
        try
        {
            SetState(UpdateState.Checking);
            var update = await _manager.CheckForUpdatesAsync();
            if (update is null)
            {
                SetState(UpdateState.UpToDate);
                return;
            }
            SetState(UpdateState.Downloading);
            await _manager.DownloadUpdatesAsync(update);
            var version = update.TargetFullRelease.Version.ToString();
            CrashLog.Info($"updates: {version} downloaded");
            _ui.TryEnqueue(() =>
            {
                ReadyVersion = version;
                State = UpdateState.Ready;
                UpdateReady?.Invoke();
            });
        }
        catch (Exception e)
        {
            CrashLog.Info("updates: check failed: " + e.Message);
            SetState(UpdateState.Failed);
        }
        finally
        {
            Interlocked.Exchange(ref _checking, 0);
        }
    }

    /// <summary>Applies the downloaded update and starts the new version. Call after shutting services down.</summary>
    public void ApplyAndRestart()
    {
        if (_manager?.UpdatePendingRestart is not { } pending) return;
        CrashLog.Info($"updates: restarting into {pending.Version}");
        _manager.ApplyUpdatesAndRestart(pending);
    }

    private bool IsReadyOnUi() => State == UpdateState.Ready;

    private void SetState(UpdateState state) => _ui.TryEnqueue(() => State = state);
}
