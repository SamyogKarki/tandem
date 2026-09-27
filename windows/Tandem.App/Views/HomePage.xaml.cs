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
        NotificationsToggle.IsOn = s.ShowNotifications;
        _loading = false;
    }

    public PhoneSession Session => AppServices.Current.Session;
    public MirrorService Mirror => AppServices.Current.Mirror;
    public ClipboardService Clipboard => AppServices.Current.Clipboard;
    public NotificationService Notifications => AppServices.Current.Notifications;

    private async void SetUpNotifications_Click(object sender, RoutedEventArgs e)
    {
        var phone = Session.Name.Length > 0 ? Session.Name : "your phone";
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Get your phone's notifications here",
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = $"Tandem will install its small companion app (about 50 KB) on {phone} and allow it to read notifications, " +
                       "so they can appear on this PC and you can reply from here.\n\n" +
                       "Notifications only travel over your paired, encrypted connection to this PC. " +
                       "You can remove the app from the phone at any time.",
            },
            PrimaryButtonText = "Install",
            CloseButtonText = "Not now",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        await Notifications.SetUpAsync();
        _loading = true;
        NotificationsToggle.IsOn = AppServices.Current.Settings.Current.ShowNotifications;
        _loading = false;
    }

    private async void TestNotification_Click(object sender, RoutedEventArgs e) => await Notifications.SendTestAsync();

    private void NotificationsToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var s = AppServices.Current.Settings;
        if (s.Current.ShowNotifications == NotificationsToggle.IsOn) return;
        s.Current.ShowNotifications = NotificationsToggle.IsOn;
        s.Save();
    }

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
