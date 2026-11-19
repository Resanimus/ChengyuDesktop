using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace ChengyuDock;
// Glass samples the wallpaper layer, never the composited screen that contains itself.
// Crops are at physical-pixel resolution so refraction coordinates stay unchanged.
internal static class WallpaperBackdrop
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool SystemParametersInfo(uint action, uint param, StringBuilder value, uint flags);
    static string _systemPath = "", _signature = "", _style = "10";
    static DateTime _nextPoll;
    static BitmapSource? _systemImage;
    static Brush _background = Brushes.Black;
    static ulong _revision = 1;
    internal static ulong Revision
    {
        get
        {
            if (DateTime.UtcNow >= _nextPoll)
            {
                _nextPoll = DateTime.UtcNow.AddSeconds(2);
                var path = new StringBuilder(32768); SystemParametersInfo(0x73, (uint)path.Capacity, path, 0);
                using var desktop = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
                using var colors = Registry.CurrentUser.OpenSubKey(@"Control Panel\Colors");
                string color = colors?.GetValue("Background")?.ToString() ?? "0 0 0";
                string style = desktop?.GetValue("TileWallpaper")?.ToString() == "1" ? "tile" : desktop?.GetValue("WallpaperStyle")?.ToString() ?? "10";
                string key = path + "|" + style + "|" + color + "|" + (File.Exists(path.ToString()) ? File.GetLastWriteTimeUtc(path.ToString()).Ticks : 0);
                if (_signature != key)
                {
                    _signature = key; _systemPath = path.ToString(); _style = style; _systemImage = null;
                    try { _systemImage = WallpaperService.Thumbnail(_systemPath, (int)(SystemParameters.PrimaryScreenWidth * 1.5)); } catch { }
                    var parts = color.Split(' '); if (parts.Length == 3 && byte.TryParse(parts[0], out var r) && byte.TryParse(parts[1], out var g) && byte.TryParse(parts[2], out var b)) { _background = new SolidColorBrush(Color.FromRgb(r, g, b)); _background.Freeze(); }
                    _revision++;
                }
            }
            var p = App.Current.Prefs.Wallpaper;
            var wallpaper = App.Current.Wallpaper;
            ulong frame = wallpaper?.VideoPlaying == true ? (ulong)(Environment.TickCount64 / wallpaper.GlassSampleInterval) : 0;
            return _revision + (ulong)HashCode.Combine(p.Kind, p.Path, p.Fit, wallpaper?.ContentRevision) * 100000 + frame;
        }
    }
    internal static GlassInput Sample(int x, int y, int width, int height, ulong revision)
    {
        var source = App.Current.Wallpaper?.BackdropVisual;
        var dpi = Application.Current.MainWindow == null ? new DpiScale(1, 1) : VisualTreeHelper.GetDpi(Application.Current.MainWindow);
        double screenW = SystemParameters.PrimaryScreenWidth * dpi.DpiScaleX, screenH = SystemParameters.PrimaryScreenHeight * dpi.DpiScaleY;
        var drawing = new DrawingVisual();
        using (var d = drawing.RenderOpen())
        {
            d.DrawRectangle(_background, null, new Rect(0, 0, width, height));
            d.PushTransform(new TranslateTransform(-x, -y));
            if (source is FrameworkElement visual && visual.ActualWidth > 0)
                d.DrawRectangle(new VisualBrush(visual) { Stretch = Stretch.Fill }, null, new Rect(0, 0, screenW, screenH));
            else if (_systemImage is { } image)
            {
                var brush = new ImageBrush(image) { Stretch = _style is "6" ? Stretch.Uniform : _style is "2" ? Stretch.Fill : Stretch.UniformToFill };
                if (_style == "tile") { brush.TileMode = TileMode.Tile; brush.ViewportUnits = BrushMappingMode.Absolute; brush.Viewport = new Rect(0, 0, image.PixelWidth, image.PixelHeight); brush.Stretch = Stretch.Fill; }
                if (_style == "0") { double iw = image.PixelWidth, ih = image.PixelHeight; d.DrawImage(image, new Rect((screenW - iw) / 2, (screenH - ih) / 2, iw, ih)); }
                else d.DrawRectangle(brush, null, new Rect(0, 0, screenW, screenH));
            }
            d.Pop();
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(drawing);
        var pixels = new byte[width * height * 4]; bitmap.CopyPixels(pixels, width * 4, 0);
        return new GlassInput(pixels, width, height, x, y, revision);
    }
}
