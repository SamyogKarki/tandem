# Tandem

**Your Android phone, on your Windows PC.** Mirror and control the screen, browse the phone's files and drag them in and out, and share one clipboard. Nothing to install on the phone.

Tandem connects over **Wireless debugging**, which is built into Android 11 and newer. You scan a QR code once, and after that the phone and PC find each other on your Wi-Fi. The connection is Android's own adb protocol, encrypted and paired to this PC.

## Features

| | Status |
|---|---|
| **Pair by QR code**: scan once from Developer options. A pairing code or a USB cable also works | ✅ |
| **Screen mirroring & control**: mouse, keyboard and phone audio, with the phone's screen optionally off | ✅ (via [scrcpy](https://github.com/Genymobile/scrcpy)) |
| **Phone files**: browse storage and SD card, open, rename, delete, new folder | ✅ |
| **Drag & drop**: drop PC files and folders onto the phone, drag phone files out to Explorer | ✅ |
| **Shared clipboard**: copy on one device, paste on the other, even with the mirror window closed | ✅ |
| Notifications on the PC, with reply | Phase 2 (companion app) |
| Calls through the PC's mic and speakers (Bluetooth hands-free) | Phase 3 (feasibility spike first) |
| Phone in File Explorer's sidebar | Later |

## How it works

```
Windows PC                                            Android phone
┌──────────────────────────────┐   Wi-Fi (adb, TLS)   ┌─────────────────────────────┐
│ Tandem (C#, WinUI 3)         │◄────────────────────►│ adbd (built into Android)   │
│  ├─ pairing: QR → mDNS → pair│                      │  ├─ sync service: files      │
│  ├─ files: adb sync protocol │                      │  ├─ shell                    │
│  ├─ clipboard bridge ────────┼── scrcpy control ───►│  └─ scrcpy-server (as shell) │
│  └─ mirroring: scrcpy window │   protocol           │      clipboard, video, input │
└──────────────────────────────┘                      └─────────────────────────────┘
```

- **Pairing:** Tandem shows `WIFI:T:ADB;S:<name>;P:<password>;;` as a QR code, which is the same handshake Android Studio uses. The phone advertises a `_adb-tls-pairing._tcp` mDNS service, and Tandem finds it and runs `adb pair`. After that, adb reconnects on its own whenever the phone is on the same network.
- **Clipboard:** Android only lets the foreground app or the keyboard read the clipboard, but adb's shell user is exempt. Tandem runs scrcpy's server in control-only mode (no video, screen untouched) and uses its clipboard messages. A loop guard stops copies from bouncing back and forth. Text marked as sensitive by Windows password managers is never sent.
- **Files:** adb's sync protocol (`LIS2`/`STA2`/`SND2`/`RCV2`), streamed with no temp copies. Name clashes keep both files ("photo (2).jpg"), and nothing is overwritten.

## Build

Requirements: Windows 10 1903+ / Windows 11, and the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
tools\fetch-vendor.ps1                      # downloads scrcpy 4.1 (+ adb), verifies SHA-256
dotnet build windows\Tandem.App -p:Platform=x64
dotnet test windows\Tandem.Core.Tests
```

The app is unpackaged and self-contained, so the output runs without the Windows App Runtime or .NET installed.

**Testing against a real phone:** `Tandem.DeviceCheck` exercises the core library on whatever phone is connected. Its file test works only inside a temporary `Download/.tandem-check-*` folder and deletes it afterwards.

```powershell
dotnet run --project windows\Tandem.DeviceCheck -- files          # mkdir, 32 MB up/down + SHA-256, rename, cleanup
dotnet run --project windows\Tandem.DeviceCheck -- clip-watch 20  # print phone clipboard changes
dotnet run --project windows\Tandem.DeviceCheck -- clip-set "hi"  # set the phone clipboard
dotnet run --project windows\Tandem.DeviceCheck -- clip-access    # is HyperOS blocking phone -> PC?
```

Errors and connection events are logged to `%LOCALAPPDATA%\Tandem\logs`. Clipboard and file contents are never logged.

## Phone setup (one time)

1. **Developer options:** Settings → About phone → tap *Build number* 7 times. On Xiaomi/HyperOS, tap *OS version*.
2. In Developer options, turn on **Wireless debugging**. Xiaomi/Redmi/POCO also need **USB debugging (Security settings)**, or the phone blocks mouse and keyboard control.
3. In Tandem, scan the QR code with *Wireless debugging → Pair device with QR code*.

**Xiaomi / HyperOS clipboard:** HyperOS has its own clipboard-privacy switch, which can hide phone-side copies from adb's shell user. PC → phone still works when it's on. Tandem detects this and shows an **Allow** button on the clipboard card. The button runs `appops set com.android.shell 10053 allow`, and `… ignore` undoes it. This was verified on a Xiaomi Pad 7 (HyperOS 3.0, Android 16) and matches [scrcpy#5961](https://github.com/Genymobile/scrcpy/issues/5961).

## Project layout

```
windows/Tandem.Core         adb host, pairing, device tracking, files, mirroring, clipboard protocol (no UI)
windows/Tandem.Core.Tests   xUnit tests for parsers and the scrcpy wire protocol
windows/Tandem.DeviceCheck  CLI smoke tests against a real, connected phone
windows/Tandem.App          WinUI 3 app
tools/                      vendor fetch + icon generator
```

## Credits

[scrcpy](https://github.com/Genymobile/scrcpy) and adb (Apache 2.0), [AdvancedSharpAdbClient](https://github.com/SharpAdb/AdvancedSharpAdbClient) (Apache 2.0), [QRCoder](https://github.com/codebude/QRCoder) (MIT), [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) (MIT).

## License

MIT © 2026 Samyog Karki
