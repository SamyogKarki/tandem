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
        Nav.SelectedItem = PhoneItem;
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
