using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Tandem.App.Services;
using Tandem.Core.Adb;
using Tandem.Core.Files;

namespace Tandem.App.ViewModels;

public sealed record Crumb(string Label, string Path)
{
    /// <summary>BreadcrumbBar's default template displays this.</summary>
    public override string ToString() => Label;
}

// ToString() is what screen readers (UI Automation) announce for list items.
public sealed record Place(string Label, string Glyph, string Path)
{
    public override string ToString() => Label;
}

public sealed class FileItem(RemoteEntry entry)
{
    public override string ToString() => Entry.IsDirectory ? Name + ", folder" : $"{Name}, {SizeText}";

    public RemoteEntry Entry { get; } = entry;
    public string Name => Entry.Name;
    public string Glyph => FileIcons.GlyphFor(Entry);
    public string ModifiedText => Format.Date(Entry.Modified);
    public string SizeText => Entry.IsDirectory ? "" : Format.Bytes(Entry.Size);
    public double IconOpacity => Entry.IsHidden ? 0.5 : 1;
}

/// <summary>State for the Files page: the folder being shown, history, and the phone's quick places.</summary>
public sealed partial class FilesViewModel : ObservableObject
{
    private static readonly (string Label, string Glyph, string Relative)[] PlaceCandidates =
    [
        ("Camera", "\uE722", "DCIM/Camera"),
        ("Screenshots", "\uE7B5", "DCIM/Screenshots"),
        ("Screenshots", "\uE7B5", "Pictures/Screenshots"),
        ("Downloads", "\uE896", "Download"),
        ("Documents", "\uE8A5", "Documents"),
        ("Pictures", "\uE91B", "Pictures"),
        ("Music", "\uE8D6", "Music"),
        ("Movies", "\uE714", "Movies"),
        ("WhatsApp", "\uE8BD", "Android/media/com.whatsapp/WhatsApp/Media"),
    ];

    private readonly PhoneSession _session;
    private readonly SettingsStore _settings;
    private readonly Stack<string> _history = new();
    private readonly List<RemoteEntry> _all = [];
    private CancellationTokenSource? _loadCts;
    private IReadOnlyList<(string Name, string Path)> _volumes = [("Internal storage", RemotePath.InternalStorage)];

    public FilesViewModel(PhoneSession session, SettingsStore settings)
    {
        _session = session;
        _settings = settings;
        _session.PropertyChanged += OnSessionChanged;
    }

    public ObservableCollection<FileItem> Items { get; } = [];
    public ObservableCollection<Crumb> Crumbs { get; } = [];
    public ObservableCollection<Place> Places { get; } = [];

    public PhoneFileSystem? FileSystem { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoUp), nameof(FolderName))]
    public partial string CurrentPath { get; private set; } = RemotePath.InternalStorage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    public partial string? Error { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    public partial int Count { get; private set; }

    [ObservableProperty]
    public partial bool CanGoBack { get; private set; }

    public bool ShowEmpty => !IsLoading && Error is null && Count == 0;
    public bool CanGoUp => !_volumes.Any(v => v.Path == CurrentPath) && RemotePath.GetParent(CurrentPath) is not null;
    public string FolderName => Crumbs.Count > 0 ? Crumbs[^1].Label : "";

    public bool ShowHidden
    {
        get => _settings.Current.ShowHiddenFiles;
        set
        {
            if (_settings.Current.ShowHiddenFiles == value) return;
            _settings.Current.ShowHiddenFiles = value;
            _settings.Save();
            OnPropertyChanged();
            ApplyFilter();
        }
    }

    public HashSet<string> NamesInCurrentFolder =>
        _all.Select(e => e.Name).ToHashSet(StringComparer.Ordinal);

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PhoneSession.Phone)) _ = AttachAsync();
    }

    /// <summary>Points the page at the current phone (called on first show and whenever the phone changes).</summary>
    public async Task AttachAsync()
    {
        var phone = _session.Phone;
        if (phone is null)
        {
            FileSystem = null;
            Items.Clear();
            _all.Clear();
            Places.Clear();
            return;
        }
        if (FileSystem?.Phone.Serial == phone.Serial) return;

        FileSystem = new PhoneFileSystem(phone);
        _history.Clear();
        CanGoBack = false;
        await NavigateAsync(RemotePath.InternalStorage, remember: false);
        await LoadPlacesAsync(FileSystem);
    }

    private async Task LoadPlacesAsync(PhoneFileSystem fs)
    {
        try
        {
            _volumes = await fs.GetVolumesAsync();
            var places = new List<Place>();
            foreach (var (name, path) in _volumes)
                places.Add(new Place(name, name == "Internal storage" ? "\uEDA2" : "\uE7F1", path));
            var seen = new HashSet<string>();
            foreach (var (label, glyph, relative) in PlaceCandidates)
            {
                if (seen.Contains(label)) continue;
                var path = RemotePath.Combine(RemotePath.InternalStorage, relative);
                if (await fs.StatAsync(path) is { IsDirectory: true })
                {
                    places.Add(new Place(label, glyph, path));
                    seen.Add(label);
                }
            }
            if (fs != FileSystem) return;
            Places.Clear();
            foreach (var p in places) Places.Add(p);
            OnPropertyChanged(nameof(CanGoUp));
        }
        catch (Exception)
        {
            // Quick places are a convenience; browsing still works without them.
        }
    }

    public async Task NavigateAsync(string path, bool remember = true)
    {
        if (FileSystem is not { } fs) return;
        if (remember && path != CurrentPath)
        {
            _history.Push(CurrentPath);
            CanGoBack = true;
        }
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();

        CurrentPath = path;
        BuildCrumbs(path);
        IsLoading = true;
        Error = null;
        try
        {
            var entries = await fs.ListAsync(path, cts.Token);
            if (cts.IsCancellationRequested) return;
            _all.Clear();
            _all.AddRange(entries);
            ApplyFilter();
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            if (cts.IsCancellationRequested) return;
            _all.Clear();
            Items.Clear();
            Count = 0;
            Error = e.Message.Contains("ermission", StringComparison.Ordinal)
                ? "Android doesn't allow opening this folder."
                : "Couldn't open this folder: " + e.Message;
        }
        finally
        {
            if (!cts.IsCancellationRequested) IsLoading = false;
        }
    }

    public Task RefreshAsync() => NavigateAsync(CurrentPath, remember: false);

    public Task GoUpAsync() =>
        RemotePath.GetParent(CurrentPath) is { } parent && CanGoUp ? NavigateAsync(parent) : Task.CompletedTask;

    public Task GoBackAsync()
    {
        if (_history.Count == 0) return Task.CompletedTask;
        var target = _history.Pop();
        CanGoBack = _history.Count > 0;
        return NavigateAsync(target, remember: false);
    }

    private void ApplyFilter()
    {
        Items.Clear();
        foreach (var e in _all.Where(e => ShowHidden || !e.IsHidden))
            Items.Add(new FileItem(e));
        Count = Items.Count;
    }

    private void BuildCrumbs(string path)
    {
        Crumbs.Clear();
        var volume = _volumes.Where(v => path == v.Path || path.StartsWith(v.Path + "/", StringComparison.Ordinal))
                             .OrderByDescending(v => v.Path.Length)
                             .FirstOrDefault();
        if (volume.Path is null)
        {
            Crumbs.Add(new Crumb("/", "/"));
            volume = ("/", "/");
        }
        else
        {
            Crumbs.Add(new Crumb(volume.Name, volume.Path));
        }

        var rest = path.Length > volume.Path.Length ? path[volume.Path.Length..].Trim('/') : "";
        var current = volume.Path;
        foreach (var segment in rest.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = RemotePath.Combine(current, segment);
            Crumbs.Add(new Crumb(segment, current));
        }
        OnPropertyChanged(nameof(FolderName));
    }
}
