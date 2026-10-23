using MiniMouseHub.Core;
using MiniMouseHub.Core.Hidpp;

// Mini Mouse Hub test tool: finds Logitech HID++ devices and reads battery and DPI.
//
//   mmh                 show every device found, with battery and DPI
//   mmh features        also list every HID++ feature each device has
//   mmh dpi <value>     set the DPI of the first mouse found
//   mmh watch           print reports the devices send on their own (Ctrl+C to stop)
//   add --debug to any command to print raw HID++ traffic

var debug = args.Contains("--debug");
var positional = args.Where(a => !a.StartsWith("--")).ToArray();
var command = positional.FirstOrDefault()?.ToLowerInvariant() ?? "info";
var launchedByDoubleClick = args.Length == 0 && !Console.IsInputRedirected;

try
{
    return Run();
}
finally
{
    if (launchedByDoubleClick)
    {
        Console.WriteLine();
        Console.WriteLine("Press Enter to close.");
        Console.ReadLine();
    }
}

int Run()
{
    var channels = HidppDiscovery.OpenAll();
    if (channels.Count == 0)
    {
        Console.WriteLine("No Logitech HID++ receiver or device found.");
        Console.WriteLine("Check that the Lightspeed receiver is plugged in and that G HUB is fully closed (including its tray icon).");
        return 1;
    }

    try
    {
        if (debug)
        {
            foreach (var channel in channels)
            {
                channel.ReportSent += r => Console.WriteLine($"  >> {Hex(r)}");
                channel.ReportReceived += r => Console.WriteLine($"  << {Hex(r)}");
            }
        }

        var devices = new List<LogitechDevice>();
        foreach (var channel in channels)
        {
            Console.WriteLine($"Found {channel.ProductName} (USB product ID {channel.ProductId:X4})");
            var found = LogitechDevice.Enumerate(channel);
            if (found.Count == 0)
                Console.WriteLine("  No HID++ 2.0 device answered. If your mouse is paired with this receiver, move it to wake it up and try again.");
            devices.AddRange(found);
        }
        Console.WriteLine();

        switch (command)
        {
            case "info":
            case "features":
                foreach (var device in devices)
                    PrintDevice(device, includeFeatures: command == "features");
                return devices.Count > 0 ? 0 : 1;

            case "dpi":
                return SetDpi(devices, positional.ElementAtOrDefault(1));

            case "watch":
                return Watch(channels);

            default:
                Console.WriteLine($"Unknown command '{command}'. Use: info, features, dpi <value>, watch.");
                return 2;
        }
    }
    finally
    {
        foreach (var channel in channels)
            channel.Dispose();
    }
}

void PrintDevice(LogitechDevice device, bool includeFeatures)
{
    var name = TryGet(device.GetName) ?? "(name unavailable)";
    var connection = device.IsWired ? "cable" : $"receiver slot {device.DeviceIndex}";
    Console.WriteLine($"{name}  [{connection}, HID++ {device.ProtocolVersion}]");

    try
    {
        var battery = device.GetBattery();
        var percent = battery.Percent is { } p ? $"{p}%" : "level only";
        var voltage = battery.VoltageMillivolts is { } mv ? $", {mv} mV" : "";
        Console.WriteLine($"  Battery:  {percent} ({battery.Level}{voltage}), {Describe(battery.Charging)}  [from {battery.Source}]");
    }
    catch (HidppException e)
    {
        Console.WriteLine($"  Battery:  unavailable ({e.Message})");
    }

    if (device.HasFeature(FeatureId.AdjustableDpi))
    {
        try
        {
            var sensors = device.GetDpiSensorCount();
            for (byte s = 0; s < sensors; s++)
            {
                var dpi = device.GetDpi(s);
                var label = sensors > 1 ? $"DPI (sensor {s})" : "DPI";
                Console.WriteLine($"  {label}:      {dpi.Current} (default {dpi.Default}, allowed {dpi.Capabilities})");
            }
        }
        catch (HidppException e)
        {
            Console.WriteLine($"  DPI:      unavailable ({e.Message})");
        }
    }
    else if (device.HasFeature(FeatureId.ExtendedAdjustableDpi))
    {
        Console.WriteLine("  DPI:      uses Extended adjustable DPI (0x2202), not supported yet");
    }
    else
    {
        Console.WriteLine("  DPI:      not adjustable");
    }

    if (device.HasFeature(FeatureId.OnboardProfiles))
        Console.WriteLine("  Has onboard profiles (needed later for button remapping)");

    if (includeFeatures)
    {
        Console.WriteLine("  Features:");
        foreach (var f in device.GetFeatures())
        {
            var flags = (f.IsHidden ? " hidden" : "") + (f.IsEngineering ? " engineering" : "");
            Console.WriteLine($"    [{f.Index,2}] 0x{f.Id:X4} v{f.Version}  {f.Name}{flags}");
        }
    }
    Console.WriteLine();
}

int SetDpi(List<LogitechDevice> devices, string? value)
{
    if (!int.TryParse(value, out var dpi))
    {
        Console.WriteLine("Usage: mmh dpi <value>, for example: mmh dpi 1600");
        return 2;
    }
    var mouse = devices.FirstOrDefault(d => d.HasFeature(FeatureId.AdjustableDpi));
    if (mouse == null)
    {
        Console.WriteLine("No device with adjustable DPI found.");
        return 1;
    }

    var before = mouse.GetDpi();
    if (!before.Capabilities.Accepts(dpi))
    {
        Console.WriteLine($"{dpi} isn't an allowed value. Allowed: {before.Capabilities}");
        return 2;
    }

    mouse.SetDpi(dpi);
    var after = mouse.GetDpi();
    Console.WriteLine($"DPI changed from {before.Current} to {after.Current}.");
    if (after.Current != dpi)
        Console.WriteLine("The mouse didn't keep the new value. It may be using its onboard profile; this is expected to be handled in a later step.");
    return after.Current == dpi ? 0 : 1;
}

int Watch(List<HidppChannel> channels)
{
    Console.WriteLine("Watching for reports. Try pressing the DPI button, plugging in the charger, or turning the mouse off and on. Ctrl+C to stop.");
    foreach (var channel in channels)
        channel.ReportReceived += r => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  {Hex(r)}");

    var stop = new ManualResetEventSlim();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); };
    stop.Wait();
    return 0;
}

static string Describe(ChargingState state) => state switch
{
    ChargingState.Discharging => "not charging",
    ChargingState.Charging => "charging",
    ChargingState.ChargingSlow => "charging slowly",
    ChargingState.Full => "fully charged",
    ChargingState.NotCharging => "plugged in, not charging",
    ChargingState.Error => "charging error",
    _ => "charging state unknown",
};

static string? TryGet(Func<string> read)
{
    try { return read(); }
    catch (HidppException) { return null; }
}

static string Hex(byte[] bytes) => Convert.ToHexString(bytes).Chunk(2).Select(c => new string(c)).Aggregate((a, b) => a + " " + b);
