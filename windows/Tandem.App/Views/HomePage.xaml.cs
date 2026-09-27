using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Tandem.App.Services;
using Tandem.Core.Mirroring;

namespace Tandem.App.Views;

public sealed partial class HomePage : Page
{
    private bool _loading;

    public HomePage()
    {
        InitializeComponent();
        var s = AppServices.Current.Settings.Current;
        _loading = true;
        AudioToggle.IsOn = s.MirrorAudio;
        ScreenOffToggle.IsOn = s.MirrorScreenOff;
        ClipboardToggle.IsOn = s.ShareClipboard;
        _loading = false;
    }

    public PhoneSession Session => AppServices.Current.Session;
    public MirrorService Mirror => AppServices.Current.Mirror;
    public ClipboardService Clipboard => AppServices.Current.Clipboard;

    public string MirrorLabel(bool mirroring) => mirroring ? "Stop mirroring" : "Mirror screen";
    public string MirrorGlyph(bool mirroring) => mirroring ? "\uE71A" : "\uE7F4";

    protected override void OnNavigatedTo(NavigationEventArgs e) => Mirror.ProblemDetected += OnMirrorProblem;
    protected override void OnNavigatedFrom(NavigationEventArgs e) => Mirror.ProblemDetected -= OnMirrorProblem;

    private void OnMirrorProblem(MirrorProblem problem)
    {
        switch (problem)
        {
            case MirrorProblem.InputBlocked:
                Show(InputBlockedBar);
                break;
            case MirrorProblem.DeviceGone:
                MirrorErrorBar.Title = "Mirroring stopped";
                MirrorErrorBar.Message = "The phone disconnected. Check that it's awake and on the same Wi-Fi.";
                Show(MirrorErrorBar);
                break;
        }
    }

    private static void Show(InfoBar bar)
    {
        bar.Visibility = Visibility.Visible;
        bar.IsOpen = true;
    }

    private static void Hide(InfoBar bar)
    {
        bar.IsOpen = false;
        bar.Visibility = Visibility.Collapsed;
    }

    private void InfoBar_Closed(InfoBar sender, InfoBarClosedEventArgs args) => sender.Visibility = Visibility.Collapsed;

    private async void Mirror_Click(object sender, RoutedEventArgs e)
    {
        if (Mirror.IsMirroring)
        {
            await Mirror.StopAsync();
            return;
        }
        if (Session.Phone is not { } phone) return;
        Hide(InputBlockedBar);
        Hide(MirrorErrorBar);
        try
        {
            Mirror.Start(phone);
        }
        catch (Exception ex)
        {
            MirrorErrorBar.Title = "Couldn't start mirroring";
            MirrorErrorBar.Message = ex.Message;
            Show(MirrorErrorBar);
        }
    }

    private void MirrorOption_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var s = AppServices.Current.Settings;
        s.Current.MirrorAudio = AudioToggle.IsOn;
        s.Current.MirrorScreenOff = ScreenOffToggle.IsOn;
        s.Save();
    }

    private void Clipboard_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var s = AppServices.Current.Settings;
        if (s.Current.ShareClipboard == ClipboardToggle.IsOn) return;
        s.Current.ShareClipboard = ClipboardToggle.IsOn;
        s.Save();
    }

    private async void AllowClipboard_Click(object sender, RoutedEventArgs e)
    {
        AllowClipboardButton.IsEnabled = false;
        try
        {
            await Clipboard.AllowPhoneCopyAsync();
        }
        catch (Exception ex)
        {
            MirrorErrorBar.Title = "Couldn't change the clipboard setting";
            MirrorErrorBar.Message = ex.Message;
            Show(MirrorErrorBar);
        }
        finally
        {
            AllowClipboardButton.IsEnabled = true;
        }
    }

    private void Files_Click(object sender, RoutedEventArgs e) =>
        (((App)Application.Current).Window as MainWindow)?.NavigateTo("files");
}
