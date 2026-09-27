// Smoke tests Tandem.Core against a real, connected phone.
//
//   dotnet run --project windows\Tandem.DeviceCheck -- files
//   dotnet run --project windows\Tandem.DeviceCheck -- clip-set "some text"
//   dotnet run --project windows\Tandem.DeviceCheck -- clip-watch 20
//
// "files" works only inside Download/.tandem-check-<random> and deletes it afterwards.

using System.Diagnostics;
using System.Security.Cryptography;
using Tandem.Core;
using Tandem.Core.Adb;
using Tandem.Core.Clipboard;
using Tandem.Core.Companion;
using Tandem.Core.Devices;
using Tandem.Core.Files;

var vendor = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "vendor", $"scrcpy-win64-v{ToolPaths.ScrcpyVersion}"));
var tools = new ToolPaths(Path.Combine(vendor, "adb.exe"), Path.Combine(vendor, "scrcpy.exe"), Path.Combine(vendor, "scrcpy-server"));
var adb = new AdbHost(tools);
await adb.StartServerAsync();
var tracker = new DeviceTracker(adb);
await tracker.RefreshAsync();
var phone = tracker.Current.Phones.FirstOrDefault() ?? throw new InvalidOperationException("No phone connected.");
Console.WriteLine($"Phone: {phone.Info.Name} ({phone.Info.Manufacturer} {phone.Info.Model}), Android {phone.Info.AndroidVersion} / SDK {phone.Info.Sdk}, {phone.Info.Transport}, serial {phone.Serial}");

switch (args.FirstOrDefault())
{
    case "files":
        await CheckFilesAsync(phone);
        break;
    case "clip-set":
        await using (var bridge = new ClipboardBridge(phone, tools) { LogLevel = "verbose" })
        {
            await bridge.StartAsync();
            await bridge.SetPhoneClipboardAsync(args[1]);
            await Task.Delay(1500); // let the message reach the phone before the socket closes
            Console.WriteLine($"Sent \"{args[1]}\" to the phone clipboard");
            PrintServerLog(bridge);
        }
        break;
    case "companion-status":
        Console.WriteLine(await CompanionInstaller.GetStatusAsync(phone));
        break;
    case "companion-install":
    {
        var secret = CompanionProtocol.NewSecret();
        await CompanionInstaller.InstallAsync(phone, Path.GetFullPath(args[1]), secret,
            new Progress<string>(s => Console.WriteLine("  " + s)));
        new CompanionSecrets().Set(phone.Info.HardwareSerial, secret);
        Console.WriteLine("Installed and paired: " + await CompanionInstaller.GetStatusAsync(phone));
        break;
    }
    case "notif-watch":
    {
        var secret = new CompanionSecrets().Get(phone.Info.HardwareSerial) ?? throw new InvalidOperationException("Run companion-install first.");
        await using var link = await CompanionConnection.ConnectAsync(phone, secret);
        Console.WriteLine($"Connected to companion {link.AppVersion}");
        link.Snapshot += items =>
        {
            Console.WriteLine($"SNAPSHOT: {items.Count} notification(s)");
            foreach (var n in items) Console.WriteLine($"  [{n.AppName}] {n.Title}: {Trim(n.Text)}");
        };
        link.Posted += n => Console.WriteLine($"POSTED [{n.AppName}] {n.Title}: {Trim(n.Text)} (actions: {string.Join(", ", n.Actions.Select(a => a.Title + (a.IsReply ? "*" : "")))}; image {(n.Image is null ? "no" : n.Image.Length + " B")})");
        link.Removed += key => Console.WriteLine($"REMOVED {key}");
        link.AppIcon += (pkg, png) => Console.WriteLine($"ICON {pkg} {png.Length} B");
        link.PhoneError += e => Console.WriteLine($"PHONE ERROR {e}");
        PhoneNotification? test = null;
        link.Posted += n => { if (n.Title == "Tandem test") test = n; };
        link.Start();
        if (args.Contains("--test"))
        {
            await Task.Delay(1000);
            await link.SendTestNotificationAsync();
            await Task.Delay(3000);
            if (test?.ReplyAction is { } reply)
            {
                Console.WriteLine("Replying to the test notification...");
                await link.ReplyAsync(test.Key, reply.Index, "hello from the PC");
            }
        }
        await Task.Delay(TimeSpan.FromSeconds(args.Length > 1 && int.TryParse(args[1], out var s) ? s : 15));
        break;
    }
    case "clip-access":
        Console.WriteLine("HyperOS blocks phone -> PC clipboard: " + await HyperOsClipboardAccess.IsBlockedAsync(phone));
        break;
    case "clip-get":
        await using (var bridge = new ClipboardBridge(phone, tools) { LogLevel = "verbose" })
        {
            var got = new TaskCompletionSource<string>();
            bridge.PhoneClipboardChanged += text => got.TrySetResult(text);
            await bridge.StartAsync();
            await bridge.RequestPhoneClipboardAsync();
            var winner = await Task.WhenAny(got.Task, Task.Delay(3000));
            Console.WriteLine(winner == got.Task ? $"Phone clipboard: \"{got.Task.Result}\"" : "No answer within 3 s");
            PrintServerLog(bridge);
        }
        break;
    case "clip-watch":
        await using (var bridge = new ClipboardBridge(phone, tools) { LogLevel = "verbose" })
        {
            bridge.PhoneClipboardChanged += text => Console.WriteLine($"PHONE CLIPBOARD: {text}");
            await bridge.StartAsync();
            Console.WriteLine("Watching phone clipboard...");
            await Task.Delay(TimeSpan.FromSeconds(args.Length > 1 ? int.Parse(args[1]) : 20));
            PrintServerLog(bridge);
        }
        break;
    default:
        Console.WriteLine("Commands: files | clip-set <text> | clip-watch [seconds]");
        break;
}

static async Task CheckFilesAsync(PhoneConnection phone)
{
    var fs = new PhoneFileSystem(phone);
    var dir = RemotePath.Combine(RemotePath.InternalStorage, "Download/.tandem-check-" + Guid.NewGuid().ToString("N")[..6]);
    const string name = "héllo wörld's (1).bin";
    var remote = RemotePath.Combine(dir, name);
    var data = RandomNumberGenerator.GetBytes(32 * 1024 * 1024);
    try
    {
        await fs.CreateDirectoryAsync(dir);
        Check("mkdir", (await fs.StatAsync(dir))?.IsDirectory == true);

        var sw = Stopwatch.StartNew();
        await fs.UploadAsync(new MemoryStream(data), remote, DateTimeOffset.Now, null);
        Console.WriteLine($"  upload 32 MB: {sw.Elapsed.TotalSeconds:0.00}s ({32 / sw.Elapsed.TotalSeconds:0.0} MB/s)");

        var listed = await fs.ListAsync(dir);
        Check("list shows file with unicode + quote name", listed.Count == 1 && listed[0].Name == name && listed[0].Size == (ulong)data.Length);

        sw.Restart();
        var back = new MemoryStream();
        ulong lastProgress = 0;
        await fs.DownloadAsync(remote, back, new Progress<ulong>(b => lastProgress = b));
        Console.WriteLine($"  download 32 MB: {sw.Elapsed.TotalSeconds:0.00}s ({32 / sw.Elapsed.TotalSeconds:0.0} MB/s)");
        Check("downloaded bytes identical (SHA-256)", SHA256.HashData(back.ToArray()).SequenceEqual(SHA256.HashData(data)));

        var renamed = RemotePath.Combine(dir, "renamed.bin");
        await fs.MoveAsync(remote, renamed);
        Check("rename", await fs.StatAsync(renamed) is not null && await fs.StatAsync(remote) is null);

        await fs.CreateDirectoryAsync(RemotePath.Combine(dir, "sub"));
        try
        {
            await fs.MoveAsync(RemotePath.Combine(dir, "sub"), renamed);
            Check("rename refuses to overwrite", false);
        }
        catch (AdbCommandException) { Check("rename refuses to overwrite", true); }

        var volumes = await fs.GetVolumesAsync();
        Console.WriteLine("  volumes: " + string.Join(", ", volumes.Select(v => $"{v.Name}={v.Path}")));

        var battery = await phone.GetBatteryAsync();
        Console.WriteLine($"  battery: {battery}");
    }
    finally
    {
        await fs.DeleteAsync([dir]);
        Check("cleanup", await fs.StatAsync(dir) is null);
    }
}

static string Trim(string s) => s.Length > 70 ? s[..70] + "…" : s.Replace('\n', ' ');

static void PrintServerLog(ClipboardBridge bridge)
{
    Console.WriteLine("--- scrcpy-server log on the phone:");
    foreach (var line in bridge.ServerLog) Console.WriteLine("  " + line);
}

static void Check(string what, bool ok)
{
    Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}");
    if (!ok) Environment.ExitCode = 1;
}
