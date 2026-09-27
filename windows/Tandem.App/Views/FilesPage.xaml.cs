using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Tandem.App.Services;
using Tandem.App.ViewModels;
using Tandem.Core.Adb;
using Tandem.Core.Files;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;

namespace Tandem.App.Views;

public sealed partial class FilesPage : Page
{
    private bool _draggingOut;

    public FilesPage()
    {
        InitializeComponent();
        AddAccelerator(VirtualKey.F5, VirtualKeyModifiers.None, () => _ = Vm.RefreshAsync());
        AddAccelerator(VirtualKey.Left, VirtualKeyModifiers.Menu, () => _ = Vm.GoBackAsync());
        AddAccelerator(VirtualKey.Up, VirtualKeyModifiers.Menu, () => _ = Vm.GoUpAsync());
    }

    public FilesViewModel Vm => AppServices.Current.Files;
    public TransferService Transfers => AppServices.Current.Transfers;

    public Visibility TransfersVisibility(int count) => Ui.Show(count > 0);

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        Transfers.UploadFinished += OnUploadFinished;
        _ = Vm.AttachAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => Transfers.UploadFinished -= OnUploadFinished;

    private void OnUploadFinished(string remoteDir)
    {
        if (remoteDir == Vm.CurrentPath) _ = Vm.RefreshAsync();
    }

    private List<FileItem> Selected => FileList.SelectedItems.OfType<FileItem>().ToList();

    // ---- navigation ----

    private void Back_Click(object sender, RoutedEventArgs e) => _ = Vm.GoBackAsync();
    private void Up_Click(object sender, RoutedEventArgs e) => _ = Vm.GoUpAsync();
    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = Vm.RefreshAsync();

    private void Crumb_Clicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
    {
        if (args.Item is Crumb crumb) _ = Vm.NavigateAsync(crumb.Path);
    }

    private void Place_Click(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is Place place) _ = Vm.NavigateAsync(place.Path);
    }

    private void FileList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is FileItem item) Open(item);
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is [var item, ..]) Open(item);
    }

    private void EnterKey_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (Selected is [var item]) Open(item);
    }

    private void BackKey_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        _ = Vm.GoUpAsync();
    }

    /// <summary>Folders open in place; files are fetched to a temp folder and opened with their default app.</summary>
    private async void Open(FileItem item)
    {
        if (item.Entry.IsDirectory)
        {
            await Vm.NavigateAsync(item.Entry.Path);
            return;
        }
        if (Vm.FileSystem is not { } fs) return;
        var dir = Path.Combine(Path.GetTempPath(), "Tandem", "Open", Guid.NewGuid().ToString("N")[..8]);
        var transfer = Transfers.Download(fs, item.Entry, dir);
        if (await transfer.WhenFinished && transfer.LocalPath is { } path)
        {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception ex) { ShowToast("Couldn't open the file", ex.Message, InfoBarSeverity.Error); }
        }
    }

    // ---- selection & context menu ----

    private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var count = FileList.SelectedItems.Count;
        DownloadButton.IsEnabled = count > 0;
        DeleteButton.IsEnabled = count > 0;
        RenameButton.IsEnabled = count == 1;
    }

    private void FileList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        // Right-clicking an unselected row acts on that row, like Explorer.
        if ((e.OriginalSource as FrameworkElement)?.DataContext is FileItem item && !FileList.SelectedItems.Contains(item))
        {
            FileList.SelectedItems.Clear();
            FileList.SelectedItems.Add(item);
        }
    }

    // ---- file operations ----

    private async void NewFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Vm.FileSystem is not { } fs) return;
        var name = await PromptAsync("New folder", "Folder name", RemotePath.UniqueName("New folder", Vm.NamesInCurrentFolder), "Create");
        if (name is null) return;
        try
        {
            await fs.CreateDirectoryAsync(RemotePath.Combine(Vm.CurrentPath, name));
            await Vm.RefreshAsync();
        }
        catch (Exception ex) { ShowToast("Couldn't create the folder", ex.Message, InfoBarSeverity.Error); }
    }

    private void RenameKey_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        Rename_Click(sender, new RoutedEventArgs());
    }

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (Vm.FileSystem is not { } fs || Selected is not [var item]) return;
        var name = await PromptAsync("Rename", "New name", item.Name, "Rename");
        if (name is null || name == item.Name) return;
        try
        {
            await fs.MoveAsync(item.Entry.Path, RemotePath.Combine(Vm.CurrentPath, name));
            await Vm.RefreshAsync();
        }
        catch (Exception ex) { ShowToast("Couldn't rename", ex.Message, InfoBarSeverity.Error); }
    }

    private void DeleteKey_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        Delete_Click(sender, new RoutedEventArgs());
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Vm.FileSystem is not { } fs || Selected is not { Count: > 0 } items) return;
        var what = items.Count == 1 ? $"\"{items[0].Name}\"" : $"these {items.Count} items";
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Delete from your phone?",
            Content = $"This permanently deletes {what} from your phone. It can't be undone.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            await fs.DeleteAsync(items.Select(i => i.Entry.Path));
        }
        catch (Exception ex) { ShowToast("Couldn't delete everything", ex.Message, InfoBarSeverity.Error); }
        await Vm.RefreshAsync();
    }

    // ---- phone → PC ----

    private void Download_Click(object sender, RoutedEventArgs e) =>
        DownloadSelected(AppServices.Current.Settings.Current.DownloadFolder);

    private async void DownloadTo_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        InitPicker(picker);
        if (await picker.PickSingleFolderAsync() is { } folder) DownloadSelected(folder.Path);
    }

    private void DownloadSelected(string folder)
    {
        if (Vm.FileSystem is not { } fs || Selected is not { Count: > 0 } items) return;
        foreach (var item in items) Transfers.Download(fs, item.Entry, folder);
        ShowToast(items.Count == 1 ? $"Saving \"{items[0].Name}\"" : $"Saving {items.Count} items",
            "to " + folder, InfoBarSeverity.Informational, folder);
    }

    private void OpenDownloads_Click(object sender, RoutedEventArgs e)
    {
        var folder = AppServices.Current.Settings.Current.DownloadFolder;
        Directory.CreateDirectory(folder);
        Process.Start("explorer.exe", $"\"{folder}\"");
    }

    private void FileList_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (Vm.FileSystem is not { } fs)
        {
            e.Cancel = true;
            return;
        }
        var entries = e.Items.OfType<FileItem>().Select(i => i.Entry).ToList();
        _draggingOut = true;
        e.Data.RequestedOperation = DataPackageOperation.Copy;
        // Delay-rendered: nothing is fetched from the phone unless the drag ends on a drop target.
        e.Data.SetDataProvider(StandardDataFormats.StorageItems, request => ProvideDraggedItems(request, fs, entries));
    }

    private void FileList_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args) => _draggingOut = false;

    private async void ProvideDraggedItems(DataProviderRequest request, PhoneFileSystem fs, List<RemoteEntry> entries)
    {
        var deferral = request.GetDeferral();
        try
        {
            var items = new List<IStorageItem>();
            foreach (var entry in entries)
            {
                if (entry.IsDirectory)
                {
                    // Folders can't be streamed on demand; fetch them into a temp folder first.
                    var dir = Path.Combine(Path.GetTempPath(), "Tandem", "Drag", Guid.NewGuid().ToString("N")[..8]);
                    var transfer = await OnUiAsync(() => Transfers.Download(fs, entry, dir));
                    if (await transfer.WhenFinished && transfer.LocalPath is { } path)
                        items.Add(await StorageFolder.GetFolderFromPathAsync(path));
                }
                else
                {
                    // A virtual file: Explorer pulls the bytes from the phone when it copies.
                    items.Add(await StorageFile.CreateStreamedFileAsync(SafeFileName(entry.Name),
                        stream => StreamFromPhone(stream, fs, entry), null));
                }
            }
            request.SetData(items);
        }
        catch (Exception)
        {
            // Leaving the request without data makes the drop a no-op.
        }
        finally
        {
            deferral.Complete();
        }
    }

    private static async void StreamFromPhone(StreamedFileDataRequest request, PhoneFileSystem fs, RemoteEntry entry)
    {
        try
        {
            await using var stream = request.AsStreamForWrite();
            await fs.DownloadAsync(entry.Path, stream, null);
            await stream.FlushAsync();
        }
        catch (Exception)
        {
            try { request.FailAndClose(StreamedFileFailureMode.Failed); } catch (ObjectDisposedException) { }
        }
    }

    // ---- PC → phone ----

    private async void UploadFiles_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add("*");
        InitPicker(picker);
        var files = await picker.PickMultipleFilesAsync();
        Upload(files.Select(f => f.Path));
    }

    private async void UploadFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        InitPicker(picker);
        if (await picker.PickSingleFolderAsync() is { } folder) Upload([folder.Path]);
    }

    private void DropArea_DragOver(object sender, DragEventArgs e)
    {
        if (_draggingOut || Vm.FileSystem is null || !e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "Copy to " + Vm.FolderName;
        DropText.Text = "Drop to copy to " + Vm.FolderName;
        DropOverlay.Visibility = Visibility.Visible;
    }

    private void DropArea_DragLeave(object sender, DragEventArgs e) => DropOverlay.Visibility = Visibility.Collapsed;

    private async void DropArea_Drop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        if (_draggingOut || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            Upload(items.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)));
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void Upload(IEnumerable<string> localPaths)
    {
        if (Vm.FileSystem is not { } fs) return;
        var taken = Vm.NamesInCurrentFolder;
        foreach (var path in localPaths) Transfers.Upload(fs, path, Vm.CurrentPath, taken);
    }

    private void ClearTransfers_Click(object sender, RoutedEventArgs e) => Transfers.ClearFinished();

    // ---- helpers ----

    private async Task<string?> PromptAsync(string title, string header, string initial, string action)
    {
        var box = new TextBox { Header = header, Text = initial };
        box.Loaded += (_, _) =>
        {
            box.Focus(FocusState.Programmatic);
            var dot = initial.LastIndexOf('.');
            box.Select(0, dot > 0 ? dot : initial.Length); // like Explorer: keep the extension
        };
        var error = new TextBlock { Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"], TextWrapping = TextWrapping.Wrap };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = new StackPanel { Spacing = 8, Children = { box, error } },
            PrimaryButtonText = action,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            var name = box.Text.Trim();
            if (!RemotePath.IsValidName(name))
            {
                error.Text = "A name can't be empty or contain \\ / : * ? \" < > |";
                args.Cancel = true;
            }
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? box.Text.Trim() : null;
    }

    private void ShowToast(string title, string message, InfoBarSeverity severity, string? folder = null)
    {
        Toast.Title = title;
        Toast.Message = message;
        Toast.Severity = severity;
        Toast.ActionButton = folder is null ? null : new Button
        {
            Content = "Open folder",
            Command = new CommunityToolkit.Mvvm.Input.RelayCommand(() =>
            {
                Directory.CreateDirectory(folder);
                Process.Start("explorer.exe", $"\"{folder}\"");
            }),
        };
        Toast.IsOpen = true;
    }

    private void InitPicker(object picker)
    {
        var window = ((App)Application.Current).Window!;
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
    }

    private Task<T> OnUiAsync<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>();
        if (!DispatcherQueue.TryEnqueue(() =>
            {
                try { tcs.SetResult(func()); }
                catch (Exception ex) { tcs.SetException(ex); }
            }))
            tcs.SetException(new InvalidOperationException("UI thread unavailable"));
        return tcs.Task;
    }

    private void AddAccelerator(VirtualKey key, VirtualKeyModifiers modifiers, Action action)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += (_, args) =>
        {
            args.Handled = true;
            action();
        };
        KeyboardAccelerators.Add(accelerator);
    }

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).TrimEnd('.', ' ');
        return clean.Length == 0 ? "_" : clean;
    }
}
