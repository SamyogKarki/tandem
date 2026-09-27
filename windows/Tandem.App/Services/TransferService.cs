using System.Collections.ObjectModel;
using System.Threading.Channels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Tandem.Core.Adb;
using Tandem.Core.Files;

namespace Tandem.App.Services;

public enum TransferDirection { Upload, Download }
public enum TransferStatus { Queued, Running, Done, Failed, Cancelled }

public sealed partial class TransferItem : ObservableObject
{
    internal readonly CancellationTokenSource Cts = new();
    private readonly TaskCompletionSource<bool> _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes with true when the transfer succeeded, false if it failed or was cancelled.</summary>
    public Task<bool> WhenFinished => _finished.Task;

    partial void OnStatusChanged(TransferStatus value)
    {
        if (value is TransferStatus.Done or TransferStatus.Failed or TransferStatus.Cancelled)
            _finished.TrySetResult(value == TransferStatus.Done);
    }

    public TransferItem(string name, TransferDirection direction)
    {
        Name = name;
        Direction = direction;
    }

    public string Name { get; }
    public TransferDirection Direction { get; }

    public override string ToString() => $"{Name}: {Detail}";
    public string DirectionGlyph => Direction == TransferDirection.Upload ? "\uE898" : "\uE896";

    /// <summary>For downloads: where the file landed, so "Show in folder" works.</summary>
    public string? LocalPath { get; internal set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Percent), nameof(Detail))]
    public partial ulong Transferred { get; internal set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Percent), nameof(Detail), nameof(IsIndeterminate))]
    public partial ulong Total { get; internal set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Detail), nameof(IsActive), nameof(IsIndeterminate), nameof(CanShowInFolder))]
    public partial TransferStatus Status { get; internal set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Detail))]
    public partial string? Error { get; internal set; }

    public double Percent => Total == 0 ? 0 : Math.Min(100, Transferred * 100.0 / Total);
    public bool IsActive => Status is TransferStatus.Queued or TransferStatus.Running;
    public bool IsIndeterminate => Status == TransferStatus.Queued || (Status == TransferStatus.Running && Total == 0);
    public bool CanShowInFolder => Status == TransferStatus.Done && LocalPath is not null;

    public string Detail => Status switch
    {
        TransferStatus.Queued => "Waiting…",
        TransferStatus.Running => Total > 0 ? $"{Format.Bytes(Transferred)} of {Format.Bytes(Total)}" : "Preparing…",
        TransferStatus.Done => Direction == TransferDirection.Upload ? "Sent to phone" : "Saved to PC",
        TransferStatus.Cancelled => "Cancelled",
        _ => Error ?? "Failed",
    };

    [RelayCommand]
    private void Cancel() => Cts.Cancel();

    [RelayCommand]
    private void ShowInFolder()
    {
        if (LocalPath is null) return;
        System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{LocalPath}\"");
    }
}

/// <summary>
/// Runs uploads and downloads one at a time (adb gives each its full bandwidth that way)
/// and exposes them to the UI.
/// </summary>
public sealed class TransferService
{
    private static readonly string[] MediaExtensions =
        ["jpg", "jpeg", "png", "gif", "webp", "heic", "heif", "bmp", "mp4", "mkv", "mov", "3gp", "webm", "mp3", "m4a", "aac", "flac", "ogg", "wav", "opus"];

    private readonly DispatcherQueue _ui;
    private readonly Channel<(TransferItem Item, Func<TransferItem, Task> Run)> _queue =
        Channel.CreateUnbounded<(TransferItem, Func<TransferItem, Task>)>();

    public TransferService(DispatcherQueue ui)
    {
        _ui = ui;
        _ = Task.Run(WorkerAsync);
    }

    public ObservableCollection<TransferItem> Items { get; } = [];

    /// <summary>Raised on the UI thread with the phone folder that just received files.</summary>
    public event Action<string>? UploadFinished;

    public void ClearFinished()
    {
        foreach (var item in Items.Where(i => !i.IsActive).ToList()) Items.Remove(item);
    }

    /// <summary>Uploads a PC file or folder into <paramref name="remoteDir"/>, keeping both on name clashes.</summary>
    public void Upload(PhoneFileSystem fs, string localPath, string remoteDir, HashSet<string> takenNames)
    {
        var isDir = Directory.Exists(localPath);
        var name = RemotePath.UniqueName(Path.GetFileName(localPath.TrimEnd('\\')), takenNames);
        takenNames.Add(name); // a batch dropping two "photo.jpg"s must not collide either
        var item = Add(name, TransferDirection.Upload);
        Enqueue(item, async it =>
        {
            var target = RemotePath.Combine(remoteDir, name);
            var files = isDir
                ? Directory.EnumerateFiles(localPath, "*", SearchOption.AllDirectories)
                    .Select(f => (Local: f, Remote: RemotePath.Combine(target, Path.GetRelativePath(localPath, f).Replace('\\', '/'))))
                    .ToList()
                : [(localPath, target)];
            SetOnUi(it, i => i.Total = (ulong)files.Sum(f => new FileInfo(f.Local).Length));

            if (isDir) await fs.CreateDirectoryAsync(target, it.Cts.Token);
            var createdDirs = new HashSet<string>(StringComparer.Ordinal) { target };
            ulong done = 0;
            foreach (var (local, remote) in files)
            {
                var parent = RemotePath.GetParent(remote)!;
                if (createdDirs.Add(parent)) await fs.CreateDirectoryAsync(parent, it.Cts.Token);

                var info = new FileInfo(local);
                await using (var stream = new FileStream(local, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true))
                {
                    var baseline = done;
                    await fs.UploadAsync(stream, remote, info.LastWriteTime, Throttled(it, baseline), it.Cts.Token);
                }
                done += (ulong)info.Length;
                SetOnUi(it, i => i.Transferred = done);

                if (MediaExtensions.Contains(Path.GetExtension(local).TrimStart('.').ToLowerInvariant()))
                    await fs.ScanMediaAsync(remote, it.Cts.Token);
            }
            _ui.TryEnqueue(() => UploadFinished?.Invoke(remoteDir));
        });
    }

    /// <summary>Downloads a phone file or folder into <paramref name="localDir"/>, keeping both on name clashes.</summary>
    public TransferItem Download(PhoneFileSystem fs, RemoteEntry entry, string localDir)
    {
        var item = Add(entry.Name, TransferDirection.Download);
        Enqueue(item, async it =>
        {
            Directory.CreateDirectory(localDir);
            var target = UniqueLocalPath(localDir, entry.Name);
            it.LocalPath = target;

            var files = new List<(RemoteEntry Remote, string Local)>();
            if (entry.IsDirectory)
            {
                Directory.CreateDirectory(target);
                await CollectAsync(fs, entry.Path, target, files, it.Cts.Token);
            }
            else
            {
                files.Add((entry, target));
            }
            SetOnUi(it, i => i.Total = (ulong)files.Sum(f => (long)f.Remote.Size));

            ulong done = 0;
            foreach (var (remote, local) in files)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(local)!);
                var partial = local + ".tandem-part";
                try
                {
                    await using (var stream = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
                        await fs.DownloadAsync(remote.Path, stream, Throttled(it, done), it.Cts.Token);
                    File.Move(partial, local, overwrite: true);
                    File.SetLastWriteTime(local, remote.Modified.LocalDateTime);
                }
                catch
                {
                    TryDelete(partial);
                    throw;
                }
                done += remote.Size;
                SetOnUi(it, i => i.Transferred = done);
            }
        });
        return item;
    }

    private static async Task CollectAsync(PhoneFileSystem fs, string remoteDir, string localDir,
        List<(RemoteEntry, string)> files, CancellationToken ct)
    {
        foreach (var e in await fs.ListAsync(remoteDir, ct))
        {
            var local = Path.Combine(localDir, SafeLocalName(e.Name));
            if (e.IsDirectory)
            {
                Directory.CreateDirectory(local);
                await CollectAsync(fs, e.Path, local, files, ct);
            }
            else
            {
                files.Add((e, local));
            }
        }
    }

    private TransferItem Add(string name, TransferDirection direction)
    {
        var item = new TransferItem(name, direction) { Status = TransferStatus.Queued };
        Items.Insert(0, item);
        return item;
    }

    private void Enqueue(TransferItem item, Func<TransferItem, Task> run) =>
        _queue.Writer.TryWrite((item, run));

    private async Task WorkerAsync()
    {
        await foreach (var (item, run) in _queue.Reader.ReadAllAsync())
        {
            if (item.Cts.IsCancellationRequested)
            {
                SetOnUi(item, i => i.Status = TransferStatus.Cancelled);
                continue;
            }
            SetOnUi(item, i => i.Status = TransferStatus.Running);
            try
            {
                await run(item);
                SetOnUi(item, i => { i.Transferred = i.Total; i.Status = TransferStatus.Done; });
            }
            catch (OperationCanceledException)
            {
                SetOnUi(item, i => i.Status = TransferStatus.Cancelled);
            }
            catch (Exception e)
            {
                SetOnUi(item, i => { i.Error = e.Message; i.Status = TransferStatus.Failed; });
            }
        }
    }

    /// <summary>
    /// Progress callbacks arrive per 64 KB chunk on a pool thread; forward to the UI thread
    /// at most ~10×/s (bindings must be updated on the UI thread).
    /// </summary>
    private IProgress<ulong> Throttled(TransferItem item, ulong baseline) => new ThrottledProgress(bytes =>
        _ui.TryEnqueue(() =>
        {
            if (item.Status == TransferStatus.Running) item.Transferred = baseline + bytes;
        }));

    private sealed class ThrottledProgress(Action<ulong> report) : IProgress<ulong>
    {
        private long _lastTicks;

        public void Report(ulong value)
        {
            var now = Environment.TickCount64;
            if (now - Interlocked.Read(ref _lastTicks) < 100) return;
            Interlocked.Exchange(ref _lastTicks, now);
            report(value);
        }
    }

    private void SetOnUi(TransferItem item, Action<TransferItem> update) => _ui.TryEnqueue(() => update(item));

    private static string UniqueLocalPath(string dir, string name)
    {
        name = SafeLocalName(name);
        var taken = new HashSet<string>(
            Directory.EnumerateFileSystemEntries(dir).Select(p => Path.GetFileName(p)!), StringComparer.OrdinalIgnoreCase);
        return Path.Combine(dir, RemotePath.UniqueName(name, taken));
    }

    /// <summary>Android allows names Windows doesn't (e.g. "a:b", trailing dots).</summary>
    private static string SafeLocalName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).TrimEnd('.', ' ');
        return clean.Length == 0 ? "_" : clean;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
