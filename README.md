# Mini Mouse Hub

A lightweight alternative to Logitech G HUB for checking battery and changing mouse settings.
It talks to Logitech devices directly over HID++ 2.0, through a Lightspeed receiver or a cable.

Status: early. The `mmh` command-line tool can read battery, charging state and DPI, and set DPI.
Tested target: PRO X Superlight 2 on a Lightspeed receiver.

## Layout

- `src/MiniMouseHub.Core`: the HID++ library (device discovery, transport, battery, DPI)
- `src/MiniMouseHub.Cli`: `mmh`, a command-line tool for testing the library

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
