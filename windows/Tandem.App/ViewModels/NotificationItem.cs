using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Tandem.Core.Companion;

namespace Tandem.App.ViewModels;

/// <summary>An app whose notifications stay on the phone.</summary>
public sealed record MutedApp(string Package, string Name)
{
    public override string ToString() => Name;
}

/// <summary>One phone notification on the Notifications page. Updated in place when the app re-posts it.</summary>
public sealed partial class NotificationItem : ObservableObject
{
    public NotificationItem(PhoneNotification source, string? appIconPath, string? avatarPath) =>
        Update(source, appIconPath, avatarPath);

    public PhoneNotification Source { get; private set; } = null!;
    public string Key => Source.Key;
    public string Package => Source.Package;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MuteLabel))]
    public partial string AppName { get; private set; } = "";

    public string MuteLabel => $"Don't show {AppName} notifications on this PC";
    [ObservableProperty] public partial string Title { get; private set; } = "";
    [ObservableProperty] public partial string Text { get; private set; } = "";
    [ObservableProperty] public partial string TimeText { get; private set; } = "";
    [ObservableProperty] public partial ImageSource? Picture { get; private set; }
    [ObservableProperty] public partial ImageSource? AppIcon { get; private set; }
    [ObservableProperty] public partial bool HasReply { get; private set; }
    [ObservableProperty] public partial bool IsIncomingCall { get; private set; }
    [ObservableProperty] public partial IReadOnlyList<NotificationAction> Buttons { get; private set; } = [];

    /// <summary>What the user is typing in the inline reply box (kept across updates).</summary>
    [ObservableProperty] public partial string ReplyText { get; set; } = "";

    public override string ToString() => $"{AppName}: {Title}. {Text}";

    public void Update(PhoneNotification source, string? appIconPath, string? avatarPath)
    {
        Source = source;
        AppName = source.AppName;
        Title = source.Title.Length > 0 ? source.Title : source.AppName;
        Text = source.Text;
        TimeText = FormatTime(source.When);
        AppIcon = Load(appIconPath);
        Picture = Load(avatarPath) ?? AppIcon;
        HasReply = source.ReplyAction is not null;
        IsIncomingCall = source.IsIncomingCall;
        Buttons = source.Actions.Where(a => !a.IsReply && a.Title.Length > 0).Take(3).ToList();
    }

    private static ImageSource? Load(string? path) =>
        path is not null && File.Exists(path) ? new BitmapImage(new Uri(path)) : null;

    private static string FormatTime(DateTimeOffset when)
    {
        var local = when.LocalDateTime;
        var age = DateTime.Now - local;
        if (age < TimeSpan.FromMinutes(1)) return "now";
        if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes} min";
        if (local.Date == DateTime.Today) return local.ToString("t", CultureInfo.CurrentCulture);
        if (local.Date == DateTime.Today.AddDays(-1)) return "Yesterday";
        return local.ToString("d MMM", CultureInfo.CurrentCulture);
    }
}
