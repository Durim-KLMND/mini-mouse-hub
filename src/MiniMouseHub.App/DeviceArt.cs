
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using MiniMouseHub.Core;

namespace MiniMouseHub.App;

/// <summary>
/// Pictures of devices. Uses a PNG from the "images" folder next to the app when there is one
/// (named after the device, for example "images\PRO X 2.png"); otherwise draws an illustration
/// of the device type.
/// </summary>
public static class DeviceArt
{
    public static FrameworkElement For(DeviceState device)
    {
        var custom = LoadCustomImage(device.Name);
        if (custom != null)
            return new Image { Source = custom, Stretch = Stretch.Uniform };

        var canvas = device.Kind switch
        {
            DeviceKind.Keyboard or DeviceKind.Numpad => Keyboard(),
            DeviceKind.Headset => Headset(),
            DeviceKind.Mouse or DeviceKind.Trackball or DeviceKind.Trackpad => Mouse(),
            _ => device.HasDpi ? Mouse() : Generic(),
        };
        return new Viewbox { Child = canvas, Stretch = Stretch.Uniform };
    }

    private static ImageSource? LoadCustomImage(string name)
    {
        try
        {
            var folder = System.IO.Path.Combine(AppContext.BaseDirectory, "images");
            var safe = string.Concat(name.Where(c => !System.IO.Path.GetInvalidFileNameChars().Contains(c)));
            var file = System.IO.Path.Combine(folder, safe + ".png");
            if (!System.IO.File.Exists(file)) return null;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad; // don't keep the file locked
            image.UriSource = new Uri(file);
            image.EndInit();
            return image;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Brush BodyBrush => new LinearGradientBrush(
        Color.FromRgb(0x3A, 0x3E, 0x47), Color.FromRgb(0x1E, 0x21, 0x27), new Point(0, 0), new Point(1, 1));

    private static Brush EdgeBrush => new SolidColorBrush(Color.FromRgb(0x55, 0x5B, 0x66));

    private static Brush AccentBrush => new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35));

    private static Canvas Mouse()
    {
        var canvas = new Canvas { Width = 120, Height = 210 };
        canvas.Children.Add(Shape("M 60,5 C 95,5 108,35 108,70 L 108,135 C 108,180 88,205 60,205 C 32,205 12,180 12,135 L 12,70 C 12,35 25,5 60,5 Z", BodyBrush, EdgeBrush, 2));
        canvas.Children.Add(Shape("M 60,6 L 60,78", null, EdgeBrush, 2));
        canvas.Children.Add(Shape("M 13,74 C 35,84 85,84 107,74", null, EdgeBrush, 2));
        canvas.Children.Add(Shape("M 55,28 A 5,5 0 0 1 65,28 L 65,52 A 5,5 0 0 1 55,52 Z", new SolidColorBrush(Color.FromRgb(0x14, 0x16, 0x1A)), EdgeBrush, 1.5));
        canvas.Children.Add(Shape("M 50,180 L 70,180", null, AccentBrush, 3));
        return canvas;
    }

    private static Canvas Keyboard()
    {
        var canvas = new Canvas { Width = 260, Height = 100 };
        canvas.Children.Add(new Rectangle { Width = 256, Height = 96, RadiusX = 10, RadiusY = 10, Fill = BodyBrush, Stroke = EdgeBrush, StrokeThickness = 2 });
        var keyBrush = new SolidColorBrush(Color.FromRgb(0x14, 0x16, 0x1A));
        for (var row = 0; row < 4; row++)
        {
            for (var col = 0; col < 14; col++)
            {
                var key = new Rectangle { Width = 14, Height = 14, RadiusX = 2, RadiusY = 2, Fill = keyBrush };
                Canvas.SetLeft(key, 12 + col * 17);
                Canvas.SetTop(key, 12 + row * 18);
                canvas.Children.Add(key);
            }
        }
        var space = new Rectangle { Width = 100, Height = 10, RadiusX = 2, RadiusY = 2, Fill = keyBrush };
        Canvas.SetLeft(space, 78);
        Canvas.SetTop(space, 84);
        canvas.Children.Add(space);
        canvas.Children.Add(Shape("M 236,10 L 246,10", null, AccentBrush, 3));
        return canvas;
    }

    private static Canvas Headset()
    {
        var canvas = new Canvas { Width = 180, Height = 180 };
        canvas.Children.Add(Shape("M 30,110 L 30,90 A 60,60 0 0 1 150,90 L 150,110", null, BodyBrush, 14));
        canvas.Children.Add(new Rectangle { Width = 36, Height = 64, RadiusX = 14, RadiusY = 14, Fill = BodyBrush, Stroke = EdgeBrush, StrokeThickness = 2 }.At(12, 100));
        canvas.Children.Add(new Rectangle { Width = 36, Height = 64, RadiusX = 14, RadiusY = 14, Fill = BodyBrush, Stroke = EdgeBrush, StrokeThickness = 2 }.At(132, 100));
        return canvas;
    }

    private static Canvas Generic()
    {
        var canvas = new Canvas { Width = 140, Height = 140 };
        canvas.Children.Add(new Rectangle { Width = 136, Height = 136, RadiusX = 24, RadiusY = 24, Fill = BodyBrush, Stroke = EdgeBrush, StrokeThickness = 2 });
        canvas.Children.Add(Shape("M 75,30 L 45,78 L 70,78 L 64,110 L 95,60 L 70,60 Z", AccentBrush, null, 0));
        return canvas;
    }

    private static System.Windows.Shapes.Path Shape(string data, Brush? fill, Brush? stroke, double thickness) => new()
    {
        Data = Geometry.Parse(data),
        Fill = fill,
        Stroke = stroke,
        StrokeThickness = thickness,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
    };

    private static T At<T>(this T element, double left, double top) where T : UIElement
    {
        Canvas.SetLeft(element, left);
        Canvas.SetTop(element, top);
        return element;
    }
}
