# Tandem

**Your Android phone, on your Windows PC.** Mirror and control the screen, browse the phone's files and drag them in and out, share one clipboard, and get your phone's notifications and incoming calls on the PC, with reply. Everything except notifications and calls works with nothing installed on the phone. For those, Tandem installs a 70 KB companion app in one click.

Tandem connects over **Wireless debugging**, which is built into Android 11 and newer. You scan a QR code once, and after that the phone and PC find each other on your Wi-Fi. The connection is Android's own adb protocol, encrypted and paired to this PC.

## Download

**[Download TandemSetup.exe](https://github.com/SamyogKarki/tandem/releases/latest/download/TandemSetup.exe)** for Windows 10 (2004 or newer) or Windows 11, 64-bit. Open it, and Tandem installs in a few seconds and walks you through connecting your phone. It updates itself after that.

Tandem isn't code-signed yet, so Windows may say "Windows protected your PC": click **More info**, then **Run anyway**. See the [release notes](https://github.com/SamyogKarki/tandem/releases/latest) for details.

## Features

| | Status |
|---|---|
| **Guided setup**: pick your phone brand and get step-by-step instructions with the exact names on your phone's screen and a drawn phone showing where to tap. Tandem checks Xiaomi's extra switches itself and opens the right settings screen on the phone | ✅ |
| **Pair by QR code**: scan once from Developer options. A pairing code or a USB cable also works | ✅ |
| **Screen mirroring & control**: mouse, keyboard and phone audio, with the phone's screen optionally off | ✅ (via [scrcpy](https://github.com/Genymobile/scrcpy)) |
| **Phone files**: browse storage and SD card, open, rename, delete, new folder | ✅ |
| **Drag & drop**: drop PC files and folders onto the phone, drag phone files out to Explorer | ✅ |
| **Shared clipboard**: copy on one device, paste on the other, even with the mirror window closed | ✅ |
| **Phone notifications on the PC**: Windows notifications with the sender's photo; reply right from the notification; click to open that app in its own window on the PC; dismissing on the phone clears it on the PC | ✅ (companion app) |
| **Lives in the tray**: starts with Windows, closing the window keeps the phone connected, one instance only | ✅ |
| **Notifications page**: everything in the phone's notification shade, with reply, action buttons, dismiss, and Clear all. Turn off any app with one click, or pause pop-ups for an hour | ✅ (companion app) |
| **Call alerts**: incoming calls ring on the PC with Answer and Decline. You talk on the phone. Calls ring even while pop-ups are paused. Try it with "Try a pretend call" in Settings | ✅ (companion app) |
| **Reconnect by itself**: after the phone restarts or Wi-Fi drops, the phone switches Wireless debugging back on, on Wi-Fi where you've used Tandem. Switch it off yourself and it stays off | ✅ (companion app) |
| Calls through the PC's mic and speakers (Bluetooth hands-free) | Phase 3 (feasibility spike first) |
| Phone in File Explorer's sidebar | Later |

## How it works

```
Windows PC                                            Android phone
┌──────────────────────────────┐   Wi-Fi (adb, TLS)   ┌─────────────────────────────┐
│ Tandem (C#, WinUI 3)         │◄────────────────────►│ adbd (built into Android)   │
│  ├─ pairing: QR → mDNS → pair│                      │  ├─ sync service: files      │
│  ├─ files: adb sync protocol │                      │  ├─ shell                    │
│  ├─ clipboard bridge ────────┼── scrcpy control ───►│  ├─ scrcpy-server (as shell) │
│  ├─ mirroring: scrcpy window │   protocol           │  │   clipboard, video, input │
│  └─ notifications ───────────┼── adb forward ──────►│  └─ Tandem companion (Kotlin)│
│     → Windows toasts         │   JSON frames        │      NotificationListener    │
└──────────────────────────────┘                      └─────────────────────────────┘
```

- **Pairing:** Tandem shows `WIFI:T:ADB;S:<name>;P:<password>;;` as a QR code, which is the same handshake Android Studio uses. The phone advertises a `_adb-tls-pairing._tcp` mDNS service, and Tandem finds it and runs `adb pair`. After that, adb reconnects on its own whenever the phone is on the same network.
- **Clipboard:** Android only lets the foreground app or the keyboard read the clipboard, but adb's shell user is exempt. Tandem runs scrcpy's server in control-only mode (no video, screen untouched) and uses its clipboard messages. A loop guard stops copies from bouncing back and forth. Text marked as sensitive by Windows password managers is never sent.
- **Files:** adb's sync protocol (`LIS2`/`STA2`/`SND2`/`RCV2`), streamed with no temp copies. Name clashes keep both files ("photo (2).jpg"), and nothing is overwritten.
- **Notifications:** The companion (`android/`) is a `NotificationListenerService`, which Android starts at boot and keeps running. It listens on an abstract Unix socket that the PC reaches through `adb forward`, so notifications travel over the same encrypted, paired connection.
  - **Who can connect:** the companion only accepts connections from adb (peer uid 2000).
  - **Proving it's the companion:** the PC checks an HMAC-SHA256 proof over a random nonce, using a secret handed over at setup. The secret goes through `am broadcast` to a receiver guarded by the `DUMP` permission, which only the adb shell holds. So no other app on the phone can pose as the companion.
  - **One-click setup:** runs `adb install -g`, `cmd notification allow_listener`, and the battery and autostart exemptions. The user never sideloads an APK or digs through settings.
  - **What reaches the PC:** ongoing notifications (music, downloads), silent ones, group summaries and "local only" ones are skipped. Replies fill the app's own `RemoteInput`, exactly as the phone's notification shade does.
  - **Calls:** a ringing call is a `CATEGORY_CALL` notification with `CallStyle`'s "incoming" call type (or, for apps without `CallStyle`, an Answer/Accept button). The PC shows it as an `incomingCall` toast with looping ringtone and green/red buttons, which press the phone app's own Answer and Decline. Once the call is answered it becomes an ongoing notification, and the phone withdraws it from the PC so the ringing stops.
  - **Staying reachable on Xiaomi.** HyperOS's `GreezeManager` freezes idle background apps within seconds, and a frozen app can't answer its socket. Tandem handles that in three ways:
    - **Wake before connecting:** before each connection, the PC sends a DUMP-guarded wake broadcast, which thaws the app.
    - **Stay awake while connected:** a `connectedDevice` foreground service keeps the app unfrozen while the PC is connected, and for 2 minutes after. Its silent "Connected to your PC" notification is the one Android requires.
    - **Keep Autostart on:** Xiaomi's Security app switches Autostart off after every install or update, and HyperOS then refuses to start the listener. Tandem re-enables it (MIUI app-op 10008) and checks it stuck.
  - **Self-healing:**
    - connections are independent, so a stuck write can never block a new one;
    - the PC pings every 20 s and drops silent links;
    - port forwards left by a crashed PC session are cleaned up at startup;
    - a companion that stops answering is restarted over adb.

    Tested by killing and relaunching the PC app repeatedly: 8/8 reconnects in about 1.5 s, and 1.7 s after the phone app had been frozen.
- **Reconnecting after a restart:** Android switches Wireless debugging off at every boot and whenever Wi-Fi drops. At setup the PC grants the companion `WRITE_SECURE_SETTINGS` (a "development" permission the adb shell may grant), and the companion uses it for one thing: setting `adb_wifi_enabled` back to 1. It does that only on networks where the PC has connected before. Without location access it can't read the network name, so it recognises a network by a hash of its router, DHCP server, subnet and DNS. Android adds its own check: on a network the user never allowed, it asks instead of switching on. Nothing keeps running for this:
  - **after a restart:** the notification listener (which Android starts at boot) or `BOOT_COMPLETED` switches it on;
  - **when it goes off:** a `JobScheduler` job triggered by changes to the `adb_wifi_enabled` setting wakes the companion. If Wi-Fi is down, Wi-Fi dropped, so it waits for Wi-Fi with a one-shot `registerNetworkCallback(PendingIntent)`. (Android fires those once and then drops them, so they can't be left armed while Wi-Fi is up.) If Wi-Fi is still up, the user switched it off, so it stays off on that network until the PC connects again.

  Tested on a Xiaomi Pad 7 (HyperOS 3): back on the PC 2 s after Wi-Fi returned, and 61 s after a restart (notifications and clipboard included). It can be switched off in the phone app.
- **Windows notifications:** Windows App SDK 2.5.1's `AppNotificationManager` is broken in self-contained unpackaged apps ([WindowsAppSDK#6774](https://github.com/microsoft/WindowsAppSDK/issues/6774)). Tandem uses the Windows toast API directly instead:
  - a per-user AUMID registration;
  - a COM toast activator, `INotificationActivationCallback`, for clicks and replies;
  - a Start menu shortcut stamped with the AUMID and activator CLSID. Without it, Windows files the notifications silently and never shows a banner.

## Build

Requirements: Windows 10 2004+ / Windows 11, the [.NET 10 SDK](https://dotnet.microsoft.com/download), and, for the companion app, JDK 17+ and the Android SDK (platform 36, build-tools 36). The companion build uses the Gradle wrapper (Gradle 9.8, AGP 9.4 with built-in Kotlin, no AndroidX).

```powershell
tools\fetch-vendor.ps1                      # downloads scrcpy 4.1 (+ adb), verifies SHA-256
tools\build-companion.ps1                   # builds the Android companion APK (bundled into the app)
dotnet build windows\Tandem.App -p:Platform=x64
dotnet test windows\Tandem.Core.Tests
```

If the companion APK isn't built, the app still builds and works; only notifications setup is unavailable.

The app is unpackaged and self-contained, so the output runs without the Windows App Runtime or .NET installed.

**Releases** are built with [Velopack](https://velopack.io): `tools\make-release.ps1` publishes the app, packs `TandemSetup.exe` plus the update feed into `artifacts\releases`, and with `-Upload` creates a draft GitHub release (release notes come from `docs\release-notes\<version>.md`; the version from `Tandem.App.csproj`). Installed copies check GitHub Releases every few hours, download updates in the background (deltas are usually a few hundred KB), and finish on the next restart. Setting `TANDEM_UPDATE_SOURCE` to a local folder of releases lets an installed copy try an update before it's published.

**Testing against a real phone:** `Tandem.DeviceCheck` exercises the core library on whatever phone is connected. Its file test works only inside a temporary `Download/.tandem-check-*` folder and deletes it afterwards.

```powershell
dotnet run --project windows\Tandem.DeviceCheck -- files          # mkdir, 32 MB up/down + SHA-256, rename, cleanup
dotnet run --project windows\Tandem.DeviceCheck -- clip-watch 20  # print phone clipboard changes
dotnet run --project windows\Tandem.DeviceCheck -- clip-set "hi"  # set the phone clipboard
dotnet run --project windows\Tandem.DeviceCheck -- clip-access    # is HyperOS blocking phone -> PC?
dotnet run --project windows\Tandem.DeviceCheck -- companion-install android\app\build\outputs\apk\release\app-release.apk
dotnet run --project windows\Tandem.DeviceCheck -- notif-watch 30 --test   # print notifications, send + reply to a test one
```

Errors and connection events are logged to `%LOCALAPPDATA%\Tandem\logs`. Clipboard and file contents are never logged.

## Phone setup (one time)

Tandem walks through all of this itself on first launch; this list is for reference.

1. **Developer options:** Settings → About phone → tap *Build number* 7 times. On Xiaomi/HyperOS, tap *OS version*.
2. In Developer options, turn on **Wireless debugging**. Xiaomi/Redmi/POCO also need **USB debugging (Security settings)**, or the phone blocks mouse and keyboard control.
3. In Tandem, scan the QR code with *Wireless debugging → Pair device with QR code*.
4. For notifications, click **Set up** on the Phone notifications card. On Xiaomi/Redmi/POCO, turn on **Install via USB** in Developer options first. Otherwise the phone refuses the install, and Tandem tells you so.

**Also using KDE Connect or Phone Link?** Turn off their notification sync, or you'll get every notification twice.

**Xiaomi / HyperOS clipboard:** HyperOS has its own clipboard-privacy switch, which can hide phone-side copies from adb's shell user. PC → phone still works when it's on. Tandem detects this and shows an **Allow** button on the clipboard card. The button runs `appops set com.android.shell 10053 allow`, and `… ignore` undoes it. This was verified on a Xiaomi Pad 7 (HyperOS 3.0, Android 16) and matches [scrcpy#5961](https://github.com/Genymobile/scrcpy/issues/5961).

## Project layout

```
windows/Tandem.Core         adb host, pairing, devices, files, mirroring, clipboard + companion protocols (no UI)
windows/Tandem.Core.Tests   xUnit tests for parsers and both wire protocols
windows/Tandem.DeviceCheck  CLI smoke tests against a real, connected phone
windows/Tandem.App          WinUI 3 app (tray, toasts, pages)
android/                    Kotlin companion app (notifications)
tools/                      vendor fetch, companion build, icon generator
```

## Credits

[scrcpy](https://github.com/Genymobile/scrcpy) and adb (Apache 2.0), [AdvancedSharpAdbClient](https://github.com/SharpAdb/AdvancedSharpAdbClient) (Apache 2.0), [QRCoder](https://github.com/codebude/QRCoder) (MIT), [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) (MIT).

## License

MIT © 2026 Samyog Karki
