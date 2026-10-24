using System.Collections.Concurrent;
using HidSharp;

namespace MiniMouseHub.Core.Hidpp;

/// <summary>
/// A HID++ connection to one physical Logitech USB device: a receiver (Lightspeed, Unifying, Bolt)
/// or a mouse plugged in by cable. Windows exposes the short and long HID++ reports as separate
/// HID collections, so a channel can hold more than one stream; all are read, and requests go
/// out as long reports.
/// </summary>
public sealed class HidppChannel : IDisposable
{
    private readonly List<HidStream> _streams = new();
    private readonly HidStream _writeStream;
    private readonly HidStream? _shortWriteStream;
    private readonly object _requestLock = new();
    private readonly BlockingCollection<byte[]> _responses = new();
    private volatile bool _disposed;

    public string DevicePath { get; }
    public int ProductId { get; }
    public string ProductName { get; }

    /// <summary>Raised for every report that arrives, including notifications sent by the device on its own.</summary>
    public event Action<byte[]>? ReportReceived;

    /// <summary>Raised for every report written, for debugging.</summary>
    public event Action<byte[]>? ReportSent;

    internal HidppChannel(IReadOnlyList<HidDevice> collections)
    {
        var first = collections[0];
        DevicePath = first.DevicePath;
        ProductId = first.ProductID;
        ProductName = SafeProductName(first);

        HidStream? writeStream = null;
        foreach (var device in collections)
        {
            if (!device.TryOpen(out HidStream stream))
                continue;
            stream.ReadTimeout = Timeout.Infinite;
            _streams.Add(stream);
            if (writeStream == null && device.GetMaxOutputReportLength() >= HidppConstants.LongReportLength)
                writeStream = stream;
            else if (_shortWriteStream == null && device.GetMaxOutputReportLength() == HidppConstants.ShortReportLength)
                _shortWriteStream = stream;
        }

        if (writeStream == null)
        {
            foreach (var s in _streams) s.Dispose();
            throw new HidppException($"Could not open the HID++ long-report collection of {ProductName}. Is another app (like G HUB) holding it?");
        }
        _writeStream = writeStream;

        foreach (var stream in _streams)
        {
            var thread = new Thread(() => ReadLoop(stream)) { IsBackground = true, Name = "HID++ reader" };
            thread.Start();
        }
    }

    private static string SafeProductName(HidDevice device)
    {
        try { return device.GetProductName(); }
        catch { return $"Logitech device {device.ProductID:X4}"; }
    }

    private void ReadLoop(HidStream stream)
    {
        var buffer = new byte[Math.Max(64, stream.Device.GetMaxInputReportLength())];
        while (!_disposed)
        {
            int count;
            try { count = stream.Read(buffer, 0, buffer.Length); }
            catch (TimeoutException) { continue; }
            catch { return; } // stream closed or device unplugged

            if (count <= 0) continue;
            var report = buffer.AsSpan(0, count).ToArray();
            if (report[0] != HidppConstants.ShortReportId && report[0] != HidppConstants.LongReportId)
                continue;

            ReportReceived?.Invoke(report);
            try { _responses.Add(report); }
            catch (InvalidOperationException) { return; } // disposed
        }
    }

    /// <summary>
    /// Sends a HID++ 2.0 request and waits for its response. Returns the 16 parameter bytes of the response.
    /// </summary>
    public byte[] Request(byte deviceIndex, byte featureIndex, byte function, ReadOnlySpan<byte> parameters, int timeoutMs = 1000)
    {
        if (parameters.Length > HidppConstants.LongReportLength - 4)
            throw new ArgumentException("Too many parameters for a long report.", nameof(parameters));

        var request = new byte[HidppConstants.LongReportLength];
        request[0] = HidppConstants.LongReportId;
        request[1] = deviceIndex;
        request[2] = featureIndex;
        request[3] = (byte)((function << 4) | HidppConstants.SoftwareId);
        parameters.CopyTo(request.AsSpan(4));

        lock (_requestLock)
        {
            while (_responses.TryTake(out _)) { } // drop anything stale

            ReportSent?.Invoke(request);
            _writeStream.Write(request);

            var deadline = Environment.TickCount64 + timeoutMs;
            while (true)
            {
                var remaining = (int)(deadline - Environment.TickCount64);
                if (remaining <= 0 || !_responses.TryTake(out var report, remaining))
                    throw new HidppTimeoutException($"No answer from device index 0x{deviceIndex:X2} (feature index 0x{featureIndex:X2}, function {function}).");

                if (report.Length < 4 || report[1] != deviceIndex)
                    continue;

                // HID++ 2.0 error: [id, dev, 0xFF, featIdx, fn|sw, code]
                if (report[2] == HidppConstants.Hidpp20ErrorMarker && report[3] == featureIndex && report[4] == request[3])
                    throw new HidppErrorException(report[5], isHidpp10: false);

                // HID++ 1.0 error from the receiver: [0x10, dev, 0x8F, featIdx, fn|sw, code]
                if (report[2] == HidppConstants.Hidpp10ErrorMarker && report[3] == featureIndex && report[4] == request[3])
                    throw new HidppErrorException(report[5], isHidpp10: true);

                if (report[2] == featureIndex && report[3] == request[3])
                {
                    var result = new byte[HidppConstants.LongReportLength - 4];
                    report.AsSpan(4, Math.Min(result.Length, report.Length - 4)).CopyTo(result);
                    return result;
                }
                // Anything else is a notification or an unrelated report: keep waiting.
            }
        }
    }

    /// <summary>Reads a HID++ 1.0 short register (from a receiver). Returns its 3 bytes.</summary>
    public byte[] ReadRegister(byte deviceIndex, byte address, int timeoutMs = 1000) =>
        RegisterRequest(deviceIndex, HidppConstants.GetShortRegister, address, [0, 0, 0], timeoutMs);

    /// <summary>Writes a HID++ 1.0 short register (on a receiver).</summary>
    public void WriteRegister(byte deviceIndex, byte address, ReadOnlySpan<byte> value, int timeoutMs = 1000) =>
        RegisterRequest(deviceIndex, HidppConstants.SetShortRegister, address, value, timeoutMs);

    /// <summary>
    /// Sends a HID++ 1.0 register request as a short report. Windows only accepts short reports on
    /// the short collection, so they go there when the device has one.
    /// </summary>
    private byte[] RegisterRequest(byte deviceIndex, byte subId, byte address, ReadOnlySpan<byte> value, int timeoutMs)
    {
        var request = new byte[HidppConstants.ShortReportLength];
        request[0] = HidppConstants.ShortReportId;
        request[1] = deviceIndex;
        request[2] = subId;
        request[3] = address;
        value[..Math.Min(3, value.Length)].CopyTo(request.AsSpan(4));

        lock (_requestLock)
        {
            while (_responses.TryTake(out _)) { }

            ReportSent?.Invoke(request);
            (_shortWriteStream ?? _writeStream).Write(request);

            var deadline = Environment.TickCount64 + timeoutMs;
            while (true)
            {
                var remaining = (int)(deadline - Environment.TickCount64);
                if (remaining <= 0 || !_responses.TryTake(out var report, remaining))
                    throw new HidppTimeoutException($"No answer from device index 0x{deviceIndex:X2} (register 0x{address:X2}).");

                if (report.Length < 7 || report[1] != deviceIndex)
                    continue;

                // HID++ 1.0 error: [0x10, dev, 0x8F, subId, address, code]
                if (report[2] == HidppConstants.Hidpp10ErrorMarker && report[3] == subId && report[4] == address)
                    throw new HidppErrorException(report[5], isHidpp10: true);

                if (report[2] == subId && report[3] == address)
                    return report.AsSpan(4, 3).ToArray();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _responses.CompleteAdding();
        foreach (var stream in _streams)
        {
            try { stream.Dispose(); } catch { }
        }
    }
}
