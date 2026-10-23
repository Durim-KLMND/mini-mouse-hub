# Mini Mouse Hub

A lightweight alternative to Logitech G HUB for checking battery and changing mouse settings.
It talks to Logitech devices directly over HID++ 2.0, through a Lightspeed receiver or a cable.

Status: early. The app and the `mmh` tool read battery, charging state and DPI, and set DPI (saved into the mouse's onboard profile).
Tested target: PRO X Superlight 2 on a Lightspeed receiver.

## Layout

- `src/MiniMouseHub.Core`: the HID++ library (device discovery, transport, battery, DPI)
- `src/MiniMouseHub.Cli`: `mmh`, a command-line tool for testing the library
- `src/MiniMouseHub.App`: the Windows app (WPF): tiles for every connected Logitech device, a page per device with battery and DPI, settings, a tray icon, and notifications for low battery and DPI changes
- `tests/MiniMouseHub.Tests`: unit tests

## Build and run

Needs the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0). Quit G HUB first, since it can hold on to the receiver.

```
dotnet run --project src/MiniMouseHub.Cli               # show devices, battery and DPI
dotnet run --project src/MiniMouseHub.Cli -- features   # also list HID++ features
dotnet run --project src/MiniMouseHub.Cli -- dpi 1600   # set DPI
dotnet run --project src/MiniMouseHub.Cli -- watch      # print reports the mouse sends on its own
```

Add `--debug` to any command to print raw HID++ traffic.

To build a single Windows .exe that runs without .NET installed:

```
dotnet publish src/MiniMouseHub.Cli -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o publish
```

To build the Windows app (about 0.5 MB; needs the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) on the PC that runs it):

```
dotnet publish src/MiniMouseHub.App -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

Run it with `--minimized` to start straight in the tray.

## Releases and auto-update

Every push to `main` runs `.github/workflows/release.yml`, which tests, builds `MiniMouseHub.exe`, and publishes it as a GitHub release tagged `v0.2.<run number>`. The app checks the latest release at startup and every 4 hours, and when it's newer, downloads it, swaps it in next to the running copy, and restarts. Automatic updates can be turned off in Settings.

## Device pictures

Devices are drawn as simple illustrations by type. To use your own picture, put a PNG named after the device (for example `images/PRO X 2.png`) next to `MiniMouseHub.exe`.
