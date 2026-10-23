using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using MiniMouseHub.Core;

namespace MiniMouseHub.App;

/// <summary>The system tray icon: shows the battery level, opens the window, and shows notifications.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private Icon? _currentIcon;
    private string _iconKey = "";

    public TrayIcon(Action open, Action quit)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Mini mouse hub", null, (_, _) => open());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => quit());

        _icon = new NotifyIcon
        {
            Text = "Mini mouse hub",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) open();
        };
        _icon.BalloonTipClicked += (_, _) => open();
        SetIcon(null, false);
    }

    /// <summary>Shows the battery of the first mouse (or else the first device with a battery) and lists all devices in the tooltip.</summary>
    public void Update(IReadOnlyList<DeviceState> devices)
    {
        var main = devices.Where(d => d.Battery != null).OrderBy(d => d.Kind == DeviceKind.Mouse ? 0 : 1).FirstOrDefault();
        var percent = main is { Awake: true } ? main.Battery?.Percent : null;
        SetIcon(percent, main?.Battery?.IsCharging == true);

        var lines = devices.Count == 0
            ? ["No devices found"]
            : devices.Select(d => d.Awake
                ? $"{d.DisplayName}: {(d.Battery?.Percent is { } p ? $"{p}%" : "connected")}{(d.Battery?.IsCharging == true ? " (charging)" : "")}"
                : $"{d.DisplayName}: asleep");
        var tooltip = string.Join("\n", lines);
        _icon.Text = tooltip.Length > 63 ? tooltip[..63] : tooltip; // Windows limit
    }

    public void Notify(string title, string text, bool warning = false) =>
        _icon.ShowBalloonTip(5000, title, text, warning ? ToolTipIcon.Warning : ToolTipIcon.Info);

    /// <summary>Draws the battery percentage into the tray icon.</summary>
    private void SetIcon(int? percent, bool charging)
    {
        var key = $"{percent}|{charging}";
        if (key == _iconKey) return;
        _iconKey = key;

        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            var color = percent switch
            {
                null => Color.FromArgb(0x5F, 0x62, 0x5F),
                <= 10 => Color.FromArgb(0xB4, 0x23, 0x18),
                <= 20 => Color.FromArgb(0xB4, 0x53, 0x09),
                _ => charging ? Color.FromArgb(0x00, 0x4B, 0x4A) : Color.FromArgb(0x15, 0x80, 0x3D),
            };
            using var background = new SolidBrush(color);
            using var path = RoundedRect(new RectangleF(1, 1, 30, 30), 7);
            g.FillPath(background, path);

            var text = percent?.ToString() ?? "?";
            using var font = new Font("Segoe UI", text.Length switch { 1 => 18, 2 => 15, _ => 11 }, FontStyle.Bold, GraphicsUnit.Pixel);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(text, font, Brushes.White, new RectangleF(0, 1, 32, 32), format);
        }

        var handle = bitmap.GetHicon();
        var icon = (Icon)Icon.FromHandle(handle).Clone();
        DestroyIcon(handle);
        _icon.Icon = icon;
        _currentIcon?.Dispose();
        _currentIcon = icon;
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _currentIcon?.Dispose();
    }
}
