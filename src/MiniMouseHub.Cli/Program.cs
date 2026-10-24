using MiniMouseHub.Core;
using MiniMouseHub.Core.Hidpp;

// Mini mouse hub test tool: finds Logitech HID++ devices and reads battery and DPI.
//
//   mmh                 show every device found, with battery and DPI
//   mmh features        also list every HID++ feature each device has
//   mmh dpi <value>     set the DPI of the first mouse found
//   mmh mode [host|onboard]  show or switch between onboard profile and host (software) mode
//   mmh profile         show the onboard profiles (read-only) and save a backup of their raw bytes
//   mmh profile restore <sector-XXXX.bin>   write a backed-up profile sector back to the mouse
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
catch (HidppException e)
{
    Console.WriteLine($"Error: {e.Message}");
    return 1;
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

            case "mode":
                return Mode(devices, positional.ElementAtOrDefault(1));

            case "profile":
            case "profiles":
                return positional.ElementAtOrDefault(1)?.ToLowerInvariant() == "restore"
                    ? RestoreProfile(devices, positional.ElementAtOrDefault(2))
                    : ShowProfiles(devices);

            case "watch":
                return Watch(channels);

            default:
                Console.WriteLine($"Unknown command '{command}'. Use: info, features, dpi <value>, mode [host|onboard], profile, watch.");
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
    var connection = device.Connection switch
    {
        ConnectionKind.Cable => "cable",
        ConnectionKind.Bluetooth => "Bluetooth",
        _ => $"receiver slot {device.DeviceIndex}",
    };
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

    if (device.HasAdjustableDpi)
    {
        try
        {
            var dpi = device.GetDpi();
            Console.WriteLine($"  DPI:      {dpi.Current} (default {dpi.Default}, allowed {dpi.Capabilities})");
        }
        catch (HidppException e)
        {
            Console.WriteLine($"  DPI:      unavailable ({e.Message})");
        }
    }
    else
    {
        Console.WriteLine("  DPI:      not adjustable");
    }

    if (device.HasFeature(FeatureId.OnboardProfiles))
    {
        var mode = TryGetMode(device);
        Console.WriteLine($"  Mode:     {(mode == OnboardMode.Host ? "host (software controls DPI)" : mode == OnboardMode.Onboard ? "onboard profile (settings stored on the mouse)" : "unknown")}");
    }

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
    var mouse = devices.FirstOrDefault(d => d.HasAdjustableDpi);
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

    if (mouse.HasFeature(FeatureId.OnboardProfiles) && TryGetMode(mouse) == OnboardMode.Onboard)
        return SetDpiInProfile(mouse, dpi, before.Current);

    try
    {
        mouse.SetDpi(dpi);
    }
    catch (HidppErrorException e)
    {
        Console.WriteLine($"The mouse refused the DPI change: {e.Message}.");
        return 1;
    }
    var after = mouse.GetDpi();
    Console.WriteLine($"DPI changed from {before.Current} to {after.Current}.");
    if (after.Current != dpi)
        Console.WriteLine("The mouse didn't keep the new value.");
    return after.Current == dpi ? 0 : 1;
}

int Mode(List<LogitechDevice> devices, string? value)
{
    var mouse = devices.FirstOrDefault(d => d.HasFeature(FeatureId.OnboardProfiles));
    if (mouse == null)
    {
        Console.WriteLine("No device with onboard profiles found.");
        return 1;
    }

    if (value != null)
    {
        var target = value.ToLowerInvariant() switch
        {
            "host" => OnboardMode.Host,
            "onboard" => OnboardMode.Onboard,
            _ => OnboardMode.Unknown,
        };
        if (target == OnboardMode.Unknown)
        {
            Console.WriteLine("Usage: mmh mode [host|onboard]");
            return 2;
        }
        mouse.SetOnboardMode(target);
    }

    var mode = mouse.GetOnboardMode();
    Console.WriteLine($"Mode: {mode}");
    return 0;
}

int ShowProfiles(List<LogitechDevice> devices)
{
    var mouse = devices.FirstOrDefault(d => d.HasFeature(FeatureId.OnboardProfiles));
    if (mouse == null)
    {
        Console.WriteLine("No device with onboard profiles found.");
        return 1;
    }

    var reader = new OnboardProfileStore(mouse);
    var description = reader.GetDescription();
    Console.WriteLine($"Profile memory: format {description.ProfileFormat}, macro format {description.MacroFormat}, memory model {description.MemoryModel}");
    Console.WriteLine($"  {description.ProfileCount} profiles ({description.ProfileCountOutOfBox} out of the box), {description.ButtonCount} buttons, " +
                      $"{description.SectorCount} sectors of {description.SectorSize} bytes, layout 0x{description.MechanicalLayout:X2}, info 0x{description.VariousInfo:X2}");

    var current = reader.GetCurrentProfile();
    Console.WriteLine($"  Active profile: {(current >= 0x100 ? $"factory profile {current & 0xFF}" : current.ToString())}, " +
                      $"DPI slot {reader.GetCurrentDpiIndex() + 1}");
    Console.WriteLine();

    var backupDir = NewBackupDir();
    File.WriteAllBytes(Path.Combine(backupDir, "description.bin"), mouse.Call(FeatureId.OnboardProfiles, 0));
    File.WriteAllBytes(Path.Combine(backupDir, "sector-0000.bin"), reader.ReadSector(0x0000, description.SectorSize));

    foreach (var header in reader.GetProfileHeaders(description))
    {
        var profile = reader.ReadProfile(header, description);
        File.WriteAllBytes(Path.Combine(backupDir, $"sector-{header.Sector:X4}.bin"), profile.Raw);

        var where = header.IsReadOnly ? "factory, read-only" : "writable";
        Console.WriteLine($"Profile {header.Number}  [sector 0x{header.Sector:X4}, {where}, {(header.Enabled ? "enabled" : "disabled")}, " +
                          $"checksum {(profile.CrcIsValid ? "OK" : "MISMATCH")}]");
        var slots = profile.DpiSlots.Select((dpi, i) =>
            (dpi is 0 or 0xFFFF ? "-" : dpi.ToString())).ToArray();
        Console.WriteLine($"  DPI slots:   {string.Join("  ", slots)}");
        Console.WriteLine($"  Header:      report rate code 0x{profile.ReportRate:X2}, byte 1 = 0x{profile.DefaultDpiIndex:X2}, byte 2 = 0x{profile.ShiftDpiIndex:X2}");
        for (var i = 0; i < profile.Buttons.Length; i++)
            Console.WriteLine($"  Button {i + 1}:    {profile.Buttons[i]}");
        for (var i = 0; i < profile.GShiftButtons.Length; i++)
            Console.WriteLine($"  G-Shift {i + 1}:   {profile.GShiftButtons[i]}");
        if (debug)
            Console.WriteLine($"  Raw: {Hex(profile.Raw)}");
        Console.WriteLine();
    }

    Console.WriteLine($"Backup saved to {backupDir}");
    return 0;
}

int SetDpiInProfile(LogitechDevice mouse, int dpi, int previous)
{
    var store = new OnboardProfileStore(mouse);
    var slot = store.GetCurrentDpiIndex();
    var backupDir = NewBackupDir();
    try
    {
        var (before, after) = store.SetDpiInActiveProfile(slot, dpi);
        File.WriteAllBytes(Path.Combine(backupDir, $"sector-{after.Header.Sector:X4}.bin"), before);
        Console.WriteLine($"Saved {dpi} DPI into slot {slot + 1} of onboard profile {after.Header.Number} (was {previous}).");
        Console.WriteLine($"DPI slots are now: {string.Join("  ", after.DpiSlots)}");
        Console.WriteLine($"The profile as it was before is backed up in {backupDir}");
    }
    catch (HidppException e)
    {
        Console.WriteLine($"Couldn't save the DPI to the onboard profile: {e.Message}");
        return 1;
    }

    var now = mouse.GetDpi().Current;
    Console.WriteLine($"The mouse now reports {now} DPI.");
    return now == dpi ? 0 : 1;
}

int RestoreProfile(List<LogitechDevice> devices, string? file)
{
    var mouse = devices.FirstOrDefault(d => d.HasFeature(FeatureId.OnboardProfiles));
    if (mouse == null || file == null || !File.Exists(file))
    {
        Console.WriteLine("Usage: mmh profile restore <path to sector-XXXX.bin from a backup folder>");
        return 2;
    }
    var name = Path.GetFileNameWithoutExtension(file);
    if (!name.StartsWith("sector-") || !int.TryParse(name["sector-".Length..], System.Globalization.NumberStyles.HexNumber, null, out var sector) || sector == 0)
    {
        Console.WriteLine("Pick a profile sector file such as sector-0001.bin.");
        return 2;
    }

    var store = new OnboardProfileStore(mouse);
    var data = File.ReadAllBytes(file);
    if (data.Length != store.GetDescription().SectorSize)
    {
        Console.WriteLine("That file isn't the size of a profile sector on this mouse.");
        return 2;
    }
    store.WriteSector(sector, data);
    var current = store.GetCurrentProfile();
    if (current < 0x100)
        store.SetCurrentProfile(current);
    Console.WriteLine($"Restored sector 0x{sector:X4} from {file}.");
    return 0;
}

static string NewBackupDir()
{
    var dir = Path.Combine(AppContext.BaseDirectory, "profile-backups", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
    Directory.CreateDirectory(dir);
    return dir;
}

static OnboardMode TryGetMode(LogitechDevice device)
{
    try { return device.GetOnboardMode(); }
    catch (HidppException) { return OnboardMode.Unknown; }
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
