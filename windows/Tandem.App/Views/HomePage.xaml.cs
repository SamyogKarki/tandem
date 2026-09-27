using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Tandem.App.Services;
using Tandem.Core.Devices;
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

    /// <summary>
    /// Explains what will happen, and on Xiaomi phones first walks through "Install via USB":
    /// the Install button stays disabled until Tandem sees the switch turned on.
    /// </summary>
    private async void SetUpNotifications_Click(object sender, RoutedEventArgs e)
    {
        if (Session.Phone is not { } phone) return;
        var content = new StackPanel { Spacing = 16 };
        content.Children.Add(RichText.Block(
            $"Tandem will install its small companion app (about 50 KB) on **{phone.Info.Name}** and let it read notifications, " +
            "so they appear on this PC and you can reply from here. Notifications only travel over your paired, encrypted connection. " +
            "You can remove the app from the phone at any time."));

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Get your phone's notifications here",
            Content = content,
            PrimaryButtonText = "Install",
            CloseButtonText = "Not now",
            DefaultButton = ContentDialogButton.Primary,
        };

        using var watch = new CancellationTokenSource();
        bool canInstall;
        try { canInstall = await PhoneChecks.CanInstallAppsAsync(phone); }
        catch (Exception) { canInstall = true; } // can't tell; the install itself will explain
        if (!canInstall)
        {
            dialog.IsPrimaryButtonEnabled = false;
            var status = new TextBlock { Text = "Waiting for the switch…", VerticalAlignment = VerticalAlignment.Center };
            var ring = new ProgressRing { IsActive = true, Width = 16, Height = 16 };
            var open = new Button { Content = "Open Developer options on my phone" };
            open.Click += async (_, _) => await OpenDeveloperOptionsAsync();
            var step = new StackPanel { Spacing = 10 };
            step.Children.Add(new TextBlock { Text = "First, one switch on your phone", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
            step.Children.Add(RichText.Block(
                "Xiaomi, Redmi and POCO phones only accept apps from a PC after you turn on **Install via USB** in Developer options. " +
                "Xiaomi may ask you to sign in to your Mi account."));
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            row.Children.Add(open);
            row.Children.Add(ring);
            row.Children.Add(status);
            step.Children.Add(row);
            content.Children.Add(new Border
            {
                Padding = new Thickness(14),
                CornerRadius = new CornerRadius(8),
                Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"],
                Child = step,
            });
            _ = WatchInstallSwitchAsync(phone, watch.Token, () =>
            {
                ring.IsActive = false;
                ring.Visibility = Visibility.Collapsed;
                status.Text = "✓ Done. You can install now.";
                dialog.IsPrimaryButtonEnabled = true;
            });
        }
        content.Children.Add(RichText.Block("If your phone asks whether to install **Tandem**, tap **Install**.",
            (Style)Application.Current.Resources["SecondaryText"]));

        var result = await dialog.ShowAsync();
        watch.Cancel();
        if (result != ContentDialogResult.Primary) return;
        await Notifications.SetUpAsync();
        _loading = true;
        NotificationsToggle.IsOn = AppServices.Current.Settings.Current.ShowNotifications;
        _loading = false;
    }

    private async Task WatchInstallSwitchAsync(Tandem.Core.Devices.PhoneConnection phone, CancellationToken ct, Action onAllowed)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (await PhoneChecks.CanInstallAppsAsync(phone, ct))
                {
                    DispatcherQueue.TryEnqueue(() => { if (!ct.IsCancellationRequested) onAllowed(); });
                    return;
                }
                await Task.Delay(1500, ct);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception) { return; }
        }
    }

    private async void OpenDevOptions_Click(object sender, RoutedEventArgs e) => await OpenDeveloperOptionsAsync();

    /// <summary>Opens Developer options on the phone's own screen, so nobody hunts through menus.</summary>
    private async Task OpenDeveloperOptionsAsync()
    {
        if (Session.Phone is not { } phone) return;
        try
        {
            await PhoneChecks.OpenDeveloperOptionsAsync(phone);
        }
        catch (Exception ex)
        {
            MirrorErrorBar.Title = "Couldn't open it on your phone";
            MirrorErrorBar.Message = "Open Settings → Developer options on the phone yourself. " + ex.Message;
            Show(MirrorErrorBar);
        }
    }

    private void OnControlUnlocked()
    {
        Show(SetupDoneBar);
        _ = Task.Delay(TimeSpan.FromSeconds(8)).ContinueWith(_ => DispatcherQueue.TryEnqueue(() => Hide(SetupDoneBar)),
            TaskScheduler.Default);
    }

    private async void TestNotification_Click(object sender, RoutedEventArgs e) => await Notifications.SendTestAsync();

    /// <summary>Reinstalls the bundled companion over the older one (keeps notification access).</summary>
    private async void UpdateCompanion_Click(object sender, RoutedEventArgs e) => await Notifications.SetUpAsync();

    private void NotificationsToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var s = AppServices.Current.Settings;
        if (s.Current.ShowNotifications == NotificationsToggle.IsOn) return;
        s.Current.ShowNotifications = NotificationsToggle.IsOn;
        s.Save();
    }

#if DEBUG
    /// <summary>Debug builds only: `Tandem.exe --show-setup` shows the setup guide even with a phone connected.</summary>
    private static readonly bool PreviewSetup = Environment.GetCommandLineArgs().Contains("--show-setup");
#else
    private const bool PreviewSetup = false;
#endif

    public Visibility GuideVisibility(bool connected) => Ui.Show(!connected || PreviewSetup);
    public Visibility DashboardVisibility(bool connected) => Ui.Show(connected && !PreviewSetup);

    public string MirrorLabel(bool mirroring) => mirroring ? "Stop mirroring" : "Mirror screen";
    public string MirrorGlyph(bool mirroring) => mirroring ? "\uE71A" : "\uE7F4";

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        Mirror.ProblemDetected += OnMirrorProblem;
        Session.ControlUnlocked += OnControlUnlocked;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        Mirror.ProblemDetected -= OnMirrorProblem;
        Session.ControlUnlocked -= OnControlUnlocked;
    }

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
