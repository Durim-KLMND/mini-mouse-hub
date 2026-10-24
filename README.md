<p align="center"><img src="docs/branding/logo.svg" width="96" alt="Mini mouse hub logo"></p>

# Mini mouse hub

Mini mouse hub is a small Windows app for checking and changing your gaming gear's settings without installing the manufacturer's software (such as Logitech G HUB). It talks to the devices directly, stays out of the way in the system tray, and changes are saved on the device itself, so they keep working on any PC.

## What it does

- Shows a tile for every connected device, with its battery level and whether it's charging
- Reads and changes the mouse's DPI, including the onboard DPI slots
- On Logi Options mice such as the MX Master: SmartShift, scroll and thumb wheel direction, and button assignments (including app actions such as shortcuts and media keys)
- Changes keyboard lighting on keyboards running QMK with VIA firmware
- Warns you when the battery runs low (20%, 10% and 5%) and when the DPI is changed with the mouse's button
- Runs in the tray, can start with Windows, and has light and dark themes
- Updates itself from this repository's GitHub releases (can be turned off in Settings)
- Keeps its settings in a single file, `%LOCALAPPDATA%\MiniMouseHub\settings.json`, which syncs to your GitHub account automatically once you sign in with GitHub in Settings
- Comes with `mmh`, a command-line tool for the same features

## Install

Download `MiniMouseHub-Setup.exe` from the [latest release](https://github.com/Durim-KLMND/mini-mouse-hub/releases/latest) and run it. It installs for your Windows account only, into `%LOCALAPPDATA%\Programs\Mini mouse hub`, so it doesn't need admin rights. It adds Mini mouse hub to the Start menu, can start it with Windows, and can be removed from Settings > Apps. The app needs the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0); the installer tells you if it's missing.

Once installed, the app keeps itself up to date, so you only run the installer once.

## Supported devices

| Device | Connection | What works | Status |
| --- | --- | --- | --- |
| Logitech PRO X Superlight 2 | Lightspeed receiver or cable | Battery, charging, DPI and DPI slots, notifications | Supported and tested |
| Logitech MX Master 3 / 3S and other Logi Options mice | Bolt receiver, Unifying receiver or Bluetooth | Battery, DPI, SmartShift, scroll direction, buttons | Being tested |
| Other Logitech mice that speak HID++ 2.0 | Lightspeed, Bolt or Unifying receiver, cable or Bluetooth | Battery and DPI, where the mouse offers them | Should work, not tested |
| Glorious GMMK Pro keyboard (with QMK/VIA firmware) | Cable | Lighting (on/off, effect, brightness, speed, color) | Supported, not tested on hardware yet |

## Not supported yet

These big gaming mouse brands don't work with Mini mouse hub so far:

- Razer
- SteelSeries
- Corsair
- Glorious (mice)
- HyperX
- ASUS ROG
- Zowie (BenQ)
- Roccat / Turtle Beach
- Cooler Master
- Pulsar
- Finalmouse
- Lamzu
- Endgame Gear
- Vaxee

## How it works

It talks to Logitech devices directly over HID++ 2.0, through a Lightspeed, Bolt or Unifying receiver, a cable or Bluetooth, and to keyboards running QMK with VIA (any brand) over VIA's raw HID protocol.
On gaming mice, DPI is saved into the mouse's onboard profile, and lighting is saved on the keyboard. Logi Options mice (such as the MX Master) have no onboard profiles and forget some settings when they reconnect, so the app remembers what you set and sets it again each time they connect. Close Logi Options+ while using Mini mouse hub, or the two will undo each other's settings.

If something doesn't work, Settings > Troubleshooting > Save diagnostic report writes a text file with your Logitech devices, their features and the app's recent HID++ traffic, which you can attach to an issue.

The project is early: the PRO X Superlight 2 is the only device tested so far.

## Layout

- `src/MiniMouseHub.Core`: the device library. `DeviceMonitor` runs one `DeviceProvider` per brand or protocol (`LogitechProvider` for HID++, `Via/ViaProvider` for QMK/VIA keyboards) and gives the app one brand-neutral `DeviceState` per device. To support a new brand, add a provider.
- `src/MiniMouseHub.Cli`: `mmh`, a command-line tool for testing the library
- `src/MiniMouseHub.App`: the Windows app (WPF): tiles for every connected device, a page per device with battery, DPI and lighting, settings, a tray icon, and notifications for low battery and DPI changes
- `installer/MiniMouseHub.iss`: the Inno Setup script for the per-user installer
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

Every push to `main` runs `.github/workflows/release.yml`, which tests, builds `MiniMouseHub.exe`, and publishes it as a GitHub release tagged `v0.3.<run number>`, together with `MiniMouseHub-Setup.exe`, the installer built from `installer/MiniMouseHub.iss` with [Inno Setup](https://jrsoftware.org/isinfo.php). The app checks the latest release at startup and every 4 hours, and when it's newer, downloads it, swaps it in next to the running copy, and restarts. Automatic updates can be turned off in Settings.

## Glorious GMMK Pro

On its stock firmware the GMMK Pro shows up with a note but can't be changed, because Glorious's protocol isn't documented. Flash QMK with the `via` keymap (from [VIA's QMK userspace](https://github.com/the-via/qmk_userspace_via)) to unlock lighting here; [Glorious's QMK guide](https://www.gloriousgaming.com/blogs/guides-resources/step-by-step-guide-to-configuring-your-gmmk-pro-using-qmk) covers flashing and going back to stock.

## Device pictures

Devices are drawn as simple illustrations by type. To use your own picture, put a PNG named after the device (for example `images/PRO X 2.png`) next to `MiniMouseHub.exe`.
