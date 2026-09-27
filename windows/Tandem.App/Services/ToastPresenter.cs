using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Tandem.Core.Companion;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace Tandem.App.Services;

/// <summary>
/// Shows phone notifications as Windows notifications (toasts) and reports clicks and replies.
///
/// Uses the Windows toast API directly rather than Windows App SDK's AppNotificationManager,
/// whose Register() fails in self-contained unpackaged apps on 2.5.1 (microsoft/WindowsAppSDK#6774).
/// The app identity is registered per user under HKCU\Software\Classes\AppUserModelId, with
/// <see cref="ToastActivator"/> as its COM activator for clicks and replies.
/// </summary>
public sealed class ToastPresenter
{
    public const string ReplyInput = "reply";
    private const string Aumid = Program.AppUserModelId;
    private static readonly string CacheDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tandem", "images");

    private ToastNotifier? _notifier;

    /// <summary>Raised (on a background thread) with the toast's arguments and any text the user typed.</summary>
    public event Action<IDictionary<string, string>, IDictionary<string, string>>? Invoked;

    public void Register()
    {
        if (_notifier is not null) return;
        // Clicks and replies arrive through the COM activator (see ToastActivator).
        ToastActivator.Activated += (args, input) => Invoked?.Invoke(ParseArgs(args), new Dictionary<string, string>(input));
        ToastActivator.Register(Environment.ProcessPath!);
        using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Classes\AppUserModelId\" + Aumid))
        {
            key.SetValue("DisplayName", "Tandem");
            key.SetValue("IconUri", Path.Combine(AppContext.BaseDirectory, "Assets", "tandem-256.png"));
            key.SetValue("IconBackgroundColor", "FF4F46E5");
            key.SetValue("CustomActivator", "{" + ToastActivator.Clsid + "}");
        }
        try
        {
            StartMenuShortcut.Ensure(Environment.ProcessPath!, Aumid, new Guid(ToastActivator.Clsid));
        }
        catch (Exception e)
        {
            CrashLog.Write("Toasts", e, "Couldn't create the Start menu shortcut; banners may not appear");
        }
        _notifier = ToastNotificationManager.CreateToastNotifier(Aumid);
        // Toasts from a previous run can't be answered any more; don't leave dead ones around.
        try { ToastNotificationManager.History.Clear(Aumid); } catch (Exception) { }
    }

    /// <summary>Short, stable id for a phone notification key (toast tags are limited to 64 chars).</summary>
    public static string IdFor(string key) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];

    public void Show(PhoneNotification n, string? appIconPath)
    {
        if (_notifier is null) return;
        var id = IdFor(n.Key);

        var binding = new XElement("binding", new XAttribute("template", "ToastGeneric"),
            new XElement("text", n.Title.Length > 0 ? n.Title : n.AppName),
            new XElement("text", n.Text),
            new XElement("text", new XAttribute("placement", "attribution"), n.AppName));

        // Sender photo (chat apps) in a circle, otherwise the app's icon.
        if (n.Image is { Length: > 0 } image && SaveImage(image, id) is { } avatar)
            binding.Add(Image(avatar, circle: true));
        else if (appIconPath is not null && File.Exists(appIconPath))
            binding.Add(Image(appIconPath, circle: false));

        var actions = new XElement("actions");
        if (n.ReplyAction is { } reply)
        {
            actions.Add(new XElement("input",
                new XAttribute("id", ReplyInput), new XAttribute("type", "text"), new XAttribute("placeHolderContent", "Reply")));
            actions.Add(new XElement("action",
                new XAttribute("content", "Send"),
                new XAttribute("arguments", Args("reply", id, reply.Index)),
                new XAttribute("hint-inputId", ReplyInput)));
        }
        foreach (var action in n.Actions.Where(a => !a.IsReply && a.Title.Length > 0).Take(2))
        {
            actions.Add(new XElement("action",
                new XAttribute("content", action.Title),
                new XAttribute("arguments", Args("act", id, action.Index))));
        }

        var toast = new XElement("toast",
            new XAttribute("launch", Args("open", id)),
            new XAttribute("displayTimestamp", n.When.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)),
            new XElement("visual", binding));
        if (actions.HasElements) toast.Add(actions);

        ShowXml(toast, tag: id, group: IdFor(n.Package));
    }

    /// <summary>A plain Tandem message (not from the phone).</summary>
    public void ShowInfo(string title, string text) =>
        ShowXml(new XElement("toast",
            new XElement("visual",
                new XElement("binding", new XAttribute("template", "ToastGeneric"),
                    new XElement("text", title),
                    new XElement("text", text)))), tag: null, group: null);

    public void Remove(string key, string package)
    {
        if (_notifier is null) return;
        try { ToastNotificationManager.History.Remove(IdFor(key), IdFor(package), Aumid); } catch (Exception) { }
    }

    public void RemoveAll()
    {
        if (_notifier is null) return;
        try { ToastNotificationManager.History.Clear(Aumid); } catch (Exception) { }
    }

    private void ShowXml(XElement toastXml, string? tag, string? group)
    {
        if (_notifier is null) return;
        var doc = new XmlDocument();
        doc.LoadXml(toastXml.ToString(SaveOptions.DisableFormatting));
        var toast = new ToastNotification(doc);
        if (tag is not null) toast.Tag = tag;
        if (group is not null) toast.Group = group;
        try
        {
            _notifier.Show(toast);
        }
        catch (Exception e)
        {
            CrashLog.Info("toast: couldn't show: " + e.Message);
        }
    }

    /// <summary>"action=reply;id=…;i=0" — every value is a word, hex id or number, so no escaping is needed.</summary>
    private static string Args(string action, string id, int? index = null) =>
        $"action={action};id={id}" + (index is { } i ? $";i={i.ToString(CultureInfo.InvariantCulture)}" : "");

    public static Dictionary<string, string> ParseArgs(string args) =>
        args.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0], p => p[1]);

    private static XElement Image(string path, bool circle)
    {
        var image = new XElement("image",
            new XAttribute("placement", "appLogoOverride"),
            new XAttribute("src", new Uri(path).AbsoluteUri));
        if (circle) image.Add(new XAttribute("hint-crop", "circle"));
        return image;
    }

    private static string? SaveImage(byte[] png, string id)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            var path = Path.Combine(CacheDir, id + ".png");
            File.WriteAllBytes(path, png);
            return path;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
