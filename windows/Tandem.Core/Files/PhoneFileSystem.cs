using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.Models;
using Tandem.Core.Adb;
using Tandem.Core.Devices;

namespace Tandem.Core.Files;

/// <summary>
/// The phone's storage over adb's sync protocol. Needs no app or permission on the phone:
/// adb's shell user can read and write shared storage (/storage/emulated/0 and SD cards).
/// </summary>
public sealed class PhoneFileSystem(PhoneConnection phone)
{
    // LIS2/STA2/SND2/RCV2 (64-bit sizes) exist on Android 11+; fall back to v1 otherwise.
    private bool SupportsV2 => phone.Device.Features?.Contains("ls_v2") ?? phone.Info.Sdk >= 30;

    public PhoneConnection Phone => phone;

    public async Task<IReadOnlyList<RemoteEntry>> ListAsync(string directory, CancellationToken ct = default)
    {
        using var sync = await OpenSyncAsync(ct).ConfigureAwait(false);
        var entries = new List<RemoteEntry>();
        var symlinks = new List<string>();

        if (SupportsV2)
        {
            foreach (var s in await sync.GetDirectoryListingExAsync(directory, ct).ConfigureAwait(false))
            {
                var name = RemotePath.GetFileName(s.Path);
                if (name is "." or ".." || name.Length == 0) continue;
                var path = RemotePath.Combine(directory, name);
                if (RemoteEntry.IsSymlinkMode(s.FileMode)) { symlinks.Add(path); continue; }
                if (!RemoteEntry.IsDirectoryMode(s.FileMode) && !RemoteEntry.IsRegularMode(s.FileMode)) continue;
                entries.Add(new RemoteEntry(name, path, RemoteEntry.IsDirectoryMode(s.FileMode), s.Size, s.ModifiedTime));
            }
        }
        else
        {
            foreach (var s in await sync.GetDirectoryListingAsync(directory, ct).ConfigureAwait(false))
            {
                var name = RemotePath.GetFileName(s.Path);
                if (name is "." or ".." || name.Length == 0) continue;
                var path = RemotePath.Combine(directory, name);
                if (RemoteEntry.IsSymlinkMode(s.FileMode)) { symlinks.Add(path); continue; }
                if (!RemoteEntry.IsDirectoryMode(s.FileMode) && !RemoteEntry.IsRegularMode(s.FileMode)) continue;
                entries.Add(new RemoteEntry(name, path, RemoteEntry.IsDirectoryMode(s.FileMode), s.Size, s.Time));
            }
        }

        // Listings report symlinks as links; stat (which follows them) to show the target's type.
        foreach (var link in symlinks)
        {
            var target = await StatAsync(sync, link, ct).ConfigureAwait(false);
            if (target is not null)
                entries.Add(target with { Name = RemotePath.GetFileName(link), Path = link });
        }

        entries.Sort(RemoteEntry.CompareForDisplay);
        return entries;
    }

    public async Task<RemoteEntry?> StatAsync(string path, CancellationToken ct = default)
    {
        using var sync = await OpenSyncAsync(ct).ConfigureAwait(false);
        return await StatAsync(sync, path, ct).ConfigureAwait(false);
    }

    private async Task<RemoteEntry?> StatAsync(SyncService sync, string path, CancellationToken ct)
    {
        var name = RemotePath.GetFileName(path);
        if (SupportsV2)
        {
            var s = await sync.StatExAsync(path, ct).ConfigureAwait(false);
            if (s.Error != UnixErrorCode.Default || s.FileMode == UnixFileStatus.None) return null;
            return new RemoteEntry(name, path, RemoteEntry.IsDirectoryMode(s.FileMode), s.Size, s.ModifiedTime);
        }
        var v1 = await sync.StatAsync(path, ct).ConfigureAwait(false);
        if (v1.FileMode == UnixFileStatus.None) return null;
        return new RemoteEntry(name, path, RemoteEntry.IsDirectoryMode(v1.FileMode), v1.Size, v1.Time);
    }

    public async Task DownloadAsync(string remotePath, Stream destination, IProgress<ulong>? progress, CancellationToken ct = default)
    {
        using var sync = await OpenSyncAsync(ct).ConfigureAwait(false);
        await sync.PullAsync(remotePath, destination,
            progress is null ? null : e => progress.Report(e.ReceivedBytesSize),
            SupportsV2, ct).ConfigureAwait(false);
    }

    public async Task UploadAsync(Stream source, string remotePath, DateTimeOffset modified, IProgress<ulong>? progress, CancellationToken ct = default)
    {
        using var sync = await OpenSyncAsync(ct).ConfigureAwait(false);
        const UnixFileStatus mode = UnixFileStatus.Regular | UnixFileStatus.UserRead | UnixFileStatus.UserWrite |
                                    UnixFileStatus.GroupRead | UnixFileStatus.GroupWrite | UnixFileStatus.OtherRead;
        await sync.PushAsync(source, remotePath, mode, modified,
            progress is null ? null : e => progress.Report(e.ReceivedBytesSize),
            SupportsV2, ct).ConfigureAwait(false);
    }

    public Task CreateDirectoryAsync(string path, CancellationToken ct = default) =>
        phone.ShellCheckedAsync("mkdir -p " + ShellQuote.Quote(path), ct);

    public Task DeleteAsync(IEnumerable<string> paths, CancellationToken ct = default)
    {
        var list = paths.ToList();
        if (list.Count == 0) return Task.CompletedTask;
        if (list.Any(p => p is "" or "/" || p.TrimEnd('/') == RemotePath.InternalStorage))
            throw new InvalidOperationException("Refusing to delete a storage root.");
        return phone.ShellCheckedAsync("rm -rf -- " + ShellQuote.Join(list), ct);
    }

    /// <summary>Renames or moves; fails instead of overwriting an existing target.</summary>
    public async Task MoveAsync(string from, string to, CancellationToken ct = default)
    {
        var exists = await phone.ShellAsync($"[ -e {ShellQuote.Quote(to)} ] && echo yes", ct).ConfigureAwait(false);
        if (exists.Trim() == "yes")
            throw new AdbCommandException($"\"{RemotePath.GetFileName(to)}\" already exists.");
        await phone.ShellCheckedAsync($"mv -- {ShellQuote.Quote(from)} {ShellQuote.Quote(to)}", ct).ConfigureAwait(false);
    }

    /// <summary>Asks MediaStore to index a new file so it shows up in Gallery/Music right away.</summary>
    public Task ScanMediaAsync(string path, CancellationToken ct = default) =>
        phone.ShellAsync("am broadcast -a android.intent.action.MEDIA_SCANNER_SCAN_FILE -d " +
                         ShellQuote.Quote("file://" + path), ct);

    /// <summary>Internal storage plus any SD cards / USB drives mounted under /storage.</summary>
    public async Task<IReadOnlyList<(string Name, string Path)>> GetVolumesAsync(CancellationToken ct = default)
    {
        var volumes = new List<(string, string)> { ("Internal storage", RemotePath.InternalStorage) };
        try
        {
            foreach (var e in await ListAsync("/storage", ct).ConfigureAwait(false))
                if (e.IsDirectory && e.Name is not ("emulated" or "self") && e.Name.Contains('-'))
                    volumes.Add(("SD card", e.Path));
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Some ROMs don't let the shell list /storage; internal storage is still fine.
        }
        return volumes;
    }

    private async Task<SyncService> OpenSyncAsync(CancellationToken ct)
    {
        // The constructor opens a socket synchronously; keep it off the UI thread.
        var sync = await Task.Run(() => new SyncService(phone.Adb.Client, phone.Device), ct).ConfigureAwait(false);
        if (!sync.IsOpen) await sync.OpenAsync(ct).ConfigureAwait(false);
        return sync;
    }
}
