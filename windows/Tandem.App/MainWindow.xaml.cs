using System.ComponentModel;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Tandem.App.Services;
using Tandem.App.Views;

namespace Tandem.App;

public sealed partial class MainWindow : Window
{
    public MainWindow(string? startupError)
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "tandem.ico"));
        AppWindow.Resize(new Windows.Graphics.SizeInt32(Scale(1120), Scale(760)));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = Scale(820);
            presenter.PreferredMinimumHeight = Scale(560);
        }

        if (startupError is not null)
        {
            ShowFatalError(startupError);
            Nav.IsEnabled = false;
            return;
        }

        var session = AppServices.Current.Session;
        session.PropertyChanged += OnSessionChanged;
        FilesItem.IsEnabled = session.IsConnected;
        AppServices.Current.Notifications.Items.CollectionChanged += (_, _) => UpdateBadge();
        var updates = AppServices.Current.Updates;
        updates.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(UpdateService.State)) ShowUpdateBar();
        };
        ShowUpdateBar();
        Nav.SelectedItem = PhoneItem;
    }

    private void ShowUpdateBar()
    {
        var updates = AppServices.Current.Updates;
        UpdateBar.Message = updates.StatusText;
        UpdateBar.IsOpen = updates.IsReady;
    }

    private async void RestartToUpdate_Click(object sender, RoutedEventArgs e) =>
        await ((App)Application.Current).RestartToUpdateAsync();

    /// <summary>How many notifications are waiting on the phone, on the nav item.</summary>
    private void UpdateBadge()
    {
        var count = AppServices.Current.Notifications.Items.Count;
        NotificationsBadge.Value = count;
        NotificationsBadge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public void ShowFatalError(string message)
    {
        FatalBar.Message = message;
        FatalBar.IsClosable = false;
        FatalBar.IsOpen = true;
    }

    /// <summary>A dismissible error banner for non-fatal problems.</summary>
    public void ShowError(string message)
    {
        FatalBar.Message = message;
        FatalBar.IsClosable = true;
        FatalBar.IsOpen = true;
    }

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PhoneSession.IsConnected)) return;
        var connected = AppServices.Current.Session.IsConnected;
        FilesItem.IsEnabled = connected;
        if (!connected && ReferenceEquals(Nav.SelectedItem, FilesItem)) Nav.SelectedItem = PhoneItem;
    }

    public void NavigateTo(string tag)
    {
        Nav.SelectedItem = tag switch
        {
            "files" => FilesItem,
            "notifications" => NotificationsItem,
            "settings" => Nav.SettingsItem,
            _ => PhoneItem,
        };
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var page = args.IsSettingsSelected
            ? typeof(SettingsPage)
            : (args.SelectedItem as NavigationViewItem)?.Tag switch
            {
                "files" => typeof(FilesPage),
                "notifications" => typeof(NotificationsPage),
                _ => typeof(HomePage),
            };
        if (ContentFrame.CurrentSourcePageType != page)
            ContentFrame.Navigate(page, null, new Microsoft.UI.Xaml.Media.Animation.EntranceNavigationTransitionInfo());
    }

    private int Scale(int dip) => (int)Math.Round(dip * (Content?.XamlRoot?.RasterizationScale ?? GetDpiScale()));

    private double GetDpiScale()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        return GetDpiForWindow(hwnd) / 96.0;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
