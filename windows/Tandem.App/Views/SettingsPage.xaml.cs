using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Tandem.App.Services;
using Tandem.Core;
using Windows.Storage.Pickers;

namespace Tandem.App.Views;

public sealed partial class SettingsPage : Page
{
    private readonly bool _ready;

    public SettingsPage()
    {
        InitializeComponent();
        var s = Store.Current;
        Select(SizeBox, s.MirrorMaxSize);
        Select(BitRateBox, s.MirrorBitRateMbps);
        Select(FpsBox, s.MirrorMaxFps);
        OnTopToggle.IsOn = s.MirrorAlwaysOnTop;
        StartupToggle.IsOn = s.StartWithWindows;
        DownloadFolderText.Text = s.DownloadFolder;
        var version = typeof(App).Assembly.GetName().Version;
        VersionText.Text = $"Tandem {version?.ToString(3)} · scrcpy {ToolPaths.ScrcpyVersion}";
        _ready = true;
    }

    private static SettingsStore Store => AppServices.Current.Settings;

    private void Setting_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        var s = Store.Current;
        s.MirrorMaxSize = SelectedValue(SizeBox, s.MirrorMaxSize);
        s.MirrorBitRateMbps = SelectedValue(BitRateBox, s.MirrorBitRateMbps);
        s.MirrorMaxFps = SelectedValue(FpsBox, s.MirrorMaxFps);
        s.MirrorAlwaysOnTop = OnTopToggle.IsOn;
        Store.Save();
    }

    private void Startup_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        Store.Current.StartWithWindows = StartupToggle.IsOn;
        Store.Save();
        StartupRegistration.Apply(StartupToggle.IsOn);
    }

    private async void ChangeFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        var window = ((App)Application.Current).Window!;
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
        if (await picker.PickSingleFolderAsync() is not { } folder) return;
        Store.Current.DownloadFolder = folder.Path;
        Store.Save();
        DownloadFolderText.Text = folder.Path;
    }

    private static void Select(ComboBox box, int value)
    {
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == value.ToString(CultureInfo.InvariantCulture))
                           ?? box.Items[0];
    }

    private static int SelectedValue(ComboBox box, int fallback) =>
        box.SelectedItem is ComboBoxItem { Tag: string tag } && int.TryParse(tag, CultureInfo.InvariantCulture, out var v) ? v : fallback;
}
