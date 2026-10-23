# Mini Mouse Hub

A lightweight alternative to Logitech G HUB for checking battery and changing mouse settings.
It talks to Logitech devices directly over HID++ 2.0, through a Lightspeed receiver or a cable, and to keyboards running QMK with VIA (any brand) over VIA's raw HID protocol.

Status: early. The app and the `mmh` tool read battery, charging state and DPI, and set DPI (saved into the mouse's onboard profile).
Keyboards with VIA firmware get a lighting card (on/off, effect, brightness, speed, color), saved on the keyboard.
Tested target: PRO X Superlight 2 on a Lightspeed receiver. VIA support targets the Glorious GMMK Pro and is not yet tested on hardware.

## Layout

- `src/MiniMouseHub.Core`: the device library. `DeviceMonitor` runs one `DeviceProvider` per brand or protocol (`LogitechProvider` for HID++, `Via/ViaProvider` for QMK/VIA keyboards) and gives the app one brand-neutral `DeviceState` per device. To support a new brand, add a provider.
- `src/MiniMouseHub.Cli`: `mmh`, a command-line tool for testing the library
- `src/MiniMouseHub.App`: the Windows app (WPF): tiles for every connected device, a page per device with battery, DPI and lighting, settings, a tray icon, and notifications for low battery and DPI changes
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

Every push to `main` runs `.github/workflows/release.yml`, which tests, builds `MiniMouseHub.exe`, and publishes it as a GitHub release tagged `v0.3.<run number>`. The app checks the latest release at startup and every 4 hours, and when it's newer, downloads it, swaps it in next to the running copy, and restarts. Automatic updates can be turned off in Settings.

## Glorious GMMK Pro

On its stock firmware the GMMK Pro shows up with a note but can't be changed, because Glorious's protocol isn't documented. Flash QMK with the `via` keymap (from [VIA's QMK userspace](https://github.com/the-via/qmk_userspace_via)) to unlock lighting here; [Glorious's QMK guide](https://www.gloriousgaming.com/blogs/guides-resources/step-by-step-guide-to-configuring-your-gmmk-pro-using-qmk) covers flashing and going back to stock.

## Device pictures

Devices are drawn as simple illustrations by type. To use your own picture, put a PNG named after the device (for example `images/PRO X 2.png`) next to `MiniMouseHub.exe`.
