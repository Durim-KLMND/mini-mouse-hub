namespace MiniMouseHub.Core;

/// <summary>
/// The most recent things the app noticed and the HID++ messages it exchanged, kept in memory so
/// they can go into a diagnostic report that someone testing a device can send back.
/// </summary>
public static class DiagnosticLog
{
    private const int MaxLines = 3000;
    private static readonly LinkedList<string> Lines = new();
    private static readonly object Lock = new();

    public static void Add(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff}  {message}";
        lock (Lock)
        {
            Lines.AddLast(line);
            while (Lines.Count > MaxLines)
                Lines.RemoveFirst();
        }
    }

    /// <summary>Logs a raw report, such as "→ 11 01 00 1A ...", with the device it went to or came from.</summary>
    public static void Report(string direction, string channel, byte[] report) =>
        Add($"{direction} {channel}: {Convert.ToHexString(report).Chunk(2).Select(c => new string(c)).Aggregate((a, b) => a + " " + b)}");

    public static string[] Snapshot()
    {
        lock (Lock)
            return Lines.ToArray();
    }
}
