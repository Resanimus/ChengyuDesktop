using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ChengyuDock;
internal static class Desktop05Verification
{
    [DllImport("user32.dll")] static extern bool GetWindowDisplayAffinity(IntPtr hwnd, out uint value);
    internal static async Task Run(Dictionary<string, bool> checks, string root)
    {
        var app = App.Current;
        checks["widgets_capture_visible_by_default"] = app.Desktop.Windows.Values.All(w => GetWindowDisplayAffinity(new WindowInteropHelper(w).Handle, out uint value) && value == 0);
        checks["dock_capture_visible_by_default"] = GetWindowDisplayAffinity(new WindowInteropHelper(app.Dock).Handle, out uint affinity) && affinity == 0;
        var island = app.Desktop.Windows.GetValueOrDefault("island");
        checks["island_center_top_fixed"] = island != null && island.Top + 34 >= 6 - (island.Height - 68) - 1 && island.Top + 34 <= SystemParameters.WorkArea.Top + 9 && Math.Abs(island.Left + island.Width / 2 - SystemParameters.PrimaryScreenWidth / 2) < .01;
        var original = app.Prefs.Clone();
        try
        {
            var box = new ShelfBoxPreference { Title = "验证盒", Width = 420, Height = 260, IconSize = 60, ShowTitle = false };
            app.Apply(p => p.ShelfBoxes.Add(box));
            checks["multiple_boxes_created"] = app.Desktop.Windows.ContainsKey("shelf:" + box.Id);
            var window = app.Desktop.Windows["shelf:" + box.Id];
            checks["box_dimensions_and_hidden_title"] = Math.Abs(window.Width - 488) < .1 && Math.Abs(window.Height - 328) < .1 && window.Shelf?.ShowTitle == false;
            app.Apply(p => p.ShelfBoxes.First(b => b.Id == box.Id).IconSize = 44);
            checks["box_independent_icon_size"] = window.Shelf?.IconSize == 44;
            app.Manager.Navigate("overview"); app.Apply(p => p.ShelfBoxes.RemoveAll(b => b.Id == box.Id));
            checks["box_removal_releases_window"] = !app.Desktop.Windows.ContainsKey("shelf:" + box.Id);
            var before = WallpaperBackdrop.Sample(120, 160, 240, 160, 1);
            var fixture = new Window { Title = "澄屿 · 玻璃残影验证", Width = 500, Height = 320, Background = Brushes.Magenta, ShowActivated = false };
            try { fixture.Show(); fixture.WindowState = WindowState.Maximized; await Task.Delay(350); fixture.WindowState = WindowState.Minimized; await Task.Delay(350); }
            finally { fixture.Close(); }
            var after = WallpaperBackdrop.Sample(120, 160, 240, 160, 2);
            checks["wallpaper_source_ignores_maximize_minimize"] = before.Pixels.SequenceEqual(after.Pixels);
            checks["wallpaper_source_not_empty"] = after.Pixels.Where((_, i) => i % 4 != 3).Any(v => v > 0);
            checks["system_clock_fonts_loaded"] = RainmeterClock.FontsLoaded();
            bool clockSizes = true;
            foreach (var style in WidgetStyles.Clock)
            {
                app.Apply(p => p.ClockStyle = style.Key); var clock = app.Desktop.Windows.GetValueOrDefault("clock");
                if (clock == null) continue;
                Snapshot(clock, Path.Combine(root, "clock-" + style.Key + ".png"));
                var size = RainmeterClock.CanvasSize(style.Key);
                double scale = Math.Min(1, Math.Min(SystemParameters.WorkArea.Width / size.Width, SystemParameters.WorkArea.Height / size.Height));
                clockSizes &= Math.Abs(clock.Width - 68 - size.Width * scale) < .1 &&
                    Math.Abs(clock.Height - 68 - size.Height * scale) < .1;
            }
            checks["all_clock_styles_rendered"] = WidgetStyles.Clock.All(s => File.Exists(Path.Combine(root, "clock-" + s.Key + ".png")));
            checks["clock_windows_fit_style_dimensions"] = clockSizes;
            var hashes = new HashSet<string>();
            var amplitudes = Enumerable.Range(0, 128).Select(i => .12 + .8 * Math.Exp(-Math.Pow((i - 27) / 15.0, 2)) + .6 * Math.Exp(-Math.Pow((i - 88) / 13.0, 2))).ToArray();
            var spectrumFixture = new SpectrumVisual { VerificationAmplitudes = amplitudes };
            var spectrumWindow = new Window { Title = "澄屿 · 频谱样式验证（测试数据）", Width = 651, Height = 180, WindowStyle = WindowStyle.None, Content = spectrumFixture, Background = Ui.Brush("#141B28"), ShowActivated = false, ShowInTaskbar = false };
            try { spectrumWindow.Show(); foreach (var style in WidgetStyles.Spectrum) { app.Apply(p => p.Spectrum.Style = style.Key); spectrumFixture.InvalidateVisual(); string file = Path.Combine(root, "spectrum-" + style.Key + ".png"); Snapshot(spectrumWindow, file); hashes.Add(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file)))); } }
            finally { spectrumWindow.Close(); }
            checks["eight_distinct_spectrum_styles_rendered"] = hashes.Count == 8;

        }
        finally { app.Manager.Navigate("overview"); app.Apply(p => { p.ClockStyle = original.ClockStyle; p.Spectrum.Style = original.Spectrum.Style; p.ShelfBoxes = original.ShelfBoxes; }); }
    }
    internal static void Snapshot(Window window, string path)
    {
        window.UpdateLayout(); var image = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32); image.Render(window); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image)); using var file = File.Create(path); png.Save(file);
    }
}
