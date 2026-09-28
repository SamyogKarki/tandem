using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Tandem.App.Services;
using Tandem.App.ViewModels;
using Tandem.Core.Companion;
using Windows.System;

namespace Tandem.App.Views;

/// <summary>The phone's notification shade on the PC: read, reply, press buttons, dismiss, mute apps, pause pop-ups.</summary>
public sealed partial class NotificationsPage : Page
{
    public NotificationsPage()
    {
        InitializeComponent();
    }

    public NotificationService Notifications => AppServices.Current.Notifications;

    public string CountText(int count, bool connected) => !connected
        ? "Not connected to the Tandem app on your phone."
        : count switch { 0 => "Nothing from your phone right now.", 1 => "1 notification from your phone", _ => $"{count} notifications from your phone" };

    public bool HasItems(int count) => count > 0;
    public Visibility EmptyVisibility(int count) => Ui.Show(count == 0);
    public string EmptyTitle(bool connected) => connected ? "You're all caught up" : "Notifications aren't connected";
    public string EmptyText(bool connected) => connected
        ? "New notifications from your phone show up here and pop up on this PC."
        : "Set up phone notifications on the Phone page, and keep your phone nearby.";
    public string PauseLabel(bool paused) => paused ? "Resume" : "Pause for 1 hour";
    public string PauseGlyph(bool paused) => paused ? "" : "";
    public Visibility HasMutedVisibility(int count) => Ui.Show(count > 0);
    public string MutedHeader(int count) => count == 1 ? "1 app turned off" : $"{count} apps turned off";

    private static NotificationItem? ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as NotificationItem;

    private void Item_Click(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is NotificationItem item) Notifications.Open(item);
    }

    private void OpenItem_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) Notifications.Open(item);
    }

    private async void Dismiss_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) await Notifications.DismissAsync(item);
    }

    private async void Action_Click(object sender, RoutedEventArgs e)
    {
        // The button's DataContext is the action; walk up to the notification it belongs to.
        if ((sender as FrameworkElement)?.DataContext is not NotificationAction action) return;
        var item = FindItem(sender as DependencyObject);
        if (item is not null) await Notifications.InvokeAsync(item, action);
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) await SendReplyAsync(item);
    }

    private async void ReplyBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter || ItemOf(sender) is not { } item) return;
        e.Handled = true;
        await SendReplyAsync(item);
    }

    private async Task SendReplyAsync(NotificationItem item)
    {
        var text = item.ReplyText.Trim();
        if (text.Length == 0) return;
        item.ReplyText = "";
        await Notifications.ReplyAsync(item, text);
    }

    private void Mute_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) Notifications.MuteApp(item.Package, item.AppName);
    }

    private void Unmute_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is MutedApp app) Notifications.UnmuteApp(app.Package);
    }

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (Notifications.IsPaused) Notifications.Resume();
        else Notifications.PauseFor(TimeSpan.FromHours(1));
    }

    private async void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Clear all notifications?",
            Content = "This clears them on your phone too, like Clear all in the phone's notification shade.",
            PrimaryButtonText = "Clear all",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) await Notifications.DismissAllAsync();
    }

    private static NotificationItem? FindItem(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is FrameworkElement { DataContext: NotificationItem item }) return item;
            element = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(element);
        }
        return null;
    }
}
