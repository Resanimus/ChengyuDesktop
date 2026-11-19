using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace ChengyuDock;
internal static class Desktop06Verification
{
    static RenderTargetBitmap Render(FrameworkElement element, int w, int h)
    {
        element.Measure(new Size(w, h)); element.Arrange(new Rect(0, 0, w, h)); element.UpdateLayout();
        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32); bitmap.Render(element); return bitmap;
    }
    static byte[] Pixels(BitmapSource b) { var bytes = new byte[b.PixelWidth * b.PixelHeight * 4]; b.CopyPixels(bytes, b.PixelWidth * 4, 0); return bytes; }
    static void Save(BitmapSource b, string path) { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(b)); using var file = File.Create(path); encoder.Save(file); }
    internal static async Task Run(Dictionary<string, bool> checks, string root)
    {
        var app = App.Current; var original = app.Prefs.Clone();
        try
        {
            var icon = new DrawingVisual(); using (var d = icon.RenderOpen()) d.DrawImage(Icons.Brand(true), new Rect(0, 0, 64, 64));
            var iconBitmap = new RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32); iconBitmap.Render(icon); var iconPixels = Pixels(iconBitmap);
            checks["launchpad_icon_has_no_background"] = iconPixels[3] == 0 && iconPixels[(32 * 64 + 2) * 4 + 3] == 0;
            var visited = new bool[4096]; int components = 0;
            for (int i = 0; i < visited.Length; i++) if (!visited[i] && iconPixels[i * 4 + 3] > 128) { components++; var queue = new Queue<int>(); queue.Enqueue(i); visited[i] = true; while (queue.Count > 0) { int at = queue.Dequeue(); foreach (int next in new[] { at - 64, at + 64, at % 64 > 0 ? at - 1 : -1, at % 64 < 63 ? at + 1 : -1 }) if (next >= 0 && next < 4096 && !visited[next] && iconPixels[next * 4 + 3] > 128) { visited[next] = true; queue.Enqueue(next); } } }
            checks["launchpad_icon_exactly_nine_dots"] = components == 9; Save(iconBitmap, Path.Combine(root, "launchpad-nine-dots.png"));
            app.Apply(p => { p.Spectrum.Color = "white"; p.Spectrum.HideWhenSilent = true; });
            bool white = true;
            foreach (var style in WidgetStyles.Spectrum)
            {
                app.Apply(p => p.Spectrum.Style = style.Key); var visual = new SpectrumVisual { VerificationAmplitudes = Enumerable.Range(0, 128).Select(i => .12 + .72 * Math.Exp(-Math.Pow((i - 44) / 28.0, 2))).ToArray() };
                var bitmap = Render(visual, 651, 180); var pixels = Pixels(bitmap); white &= pixels.Where((_, i) => i % 4 == 3).Any(x => x > 0);
                for (int i = 0; i < pixels.Length; i += 4) if (pixels[i] != pixels[i + 1] || pixels[i + 1] != pixels[i + 2]) { white = false; break; }
                Save(bitmap, Path.Combine(root, "spectrum-white-" + style.Key + ".png"));
            }
            checks["eight_styles_render_in_white"] = white;
            app.Apply(p => p.Spectrum.Style = "blocks");
            var silent = new SpectrumVisual(); checks["silent_spectrum_has_no_flatline"] = Pixels(Render(silent, 651, 180)).All(x => x == 0);
            app.Apply(p => p.Spectrum.HideWhenSilent = false); silent.InvalidateVisual(); checks["flatline_can_be_restored"] = Pixels(Render(silent, 651, 180)).Any(x => x != 0);
            var island = app.Desktop.Windows.GetValueOrDefault("island");
            if (island != null)
            {
                var ih = new WindowInteropHelper(island).Handle;
                checks["island_above_all_desktop_widgets"] = app.Desktop.Windows.Values.Where(w => w != island).All(w => DesktopLayer.IsBelow(new WindowInteropHelper(w).Handle, ih));
                checks["island_stays_below_manager"] = DesktopLayer.IsBelow(ih, new WindowInteropHelper(app.Manager).Handle);
            }
            var box = new ShelfBoxPreference { Title = "独立标题与横线", Material = "none" };
            app.Apply(p => p.ShelfBoxes.Add(box));
            foreach (bool title in new[] { false, true }) foreach (bool divider in new[] { false, true })
            {
                app.Apply(p => { var b = p.ShelfBoxes.First(x => x.Id == box.Id); b.ShowTitle = title; b.ShowDivider = divider; });
                Desktop05Verification.Snapshot(app.Desktop.Windows["shelf:" + box.Id], Path.Combine(root, $"shelf-title-{title}-divider-{divider}.png"));
            }
            checks["four_shelf_header_combinations_rendered"] = true;
            if (island != null) { island.Sync(); checks["island_above_newly_created_box"] = DesktopLayer.IsBelow(new WindowInteropHelper(app.Desktop.Windows["shelf:" + box.Id]).Handle, new WindowInteropHelper(island).Handle); }
            app.Apply(p => { p.ClockStyle = "editorial"; p.Widgets["clock"].Material = "none"; });
            var clock = app.Desktop.Windows.GetValueOrDefault("clock");
            if (clock != null)
            {
                var bitmap = new RenderTargetBitmap((int)clock.Width, (int)clock.Height, 96, 96, PixelFormats.Pbgra32); clock.UpdateLayout(); bitmap.Render(clock); var pixels = Pixels(bitmap); bool centered = true;
                foreach (var band in new[] { (41, 102), (112, 147), (154, 179) }) { int left = bitmap.PixelWidth, right = -1; for (int y = band.Item1; y < band.Item2; y++) for (int x = 0; x < bitmap.PixelWidth; x++) if (pixels[(y * bitmap.PixelWidth + x) * 4 + 3] > 128) { left = Math.Min(left, x); right = Math.Max(right, x); } centered &= right >= left && Math.Abs((left + right + 1) / 2.0 - bitmap.PixelWidth / 2.0) <= 1.5; }
                checks["editorial_clock_all_three_lines_ink_centered"] = centered; Save(bitmap, Path.Combine(root, "clock-centered.png"));
            }
            // Real, low-volume playback fixture: metadata and commands use Windows SMTC.
            string wave = Path.Combine(root, "media-fixture.wav"); WriteTone(wave);
            var start = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false }; start.ArgumentList.Add("--media-fixture"); start.ArgumentList.Add(wave);
            using var player = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("媒体测试子进程未启动");
            try
            {
                bool detected = false; for (int i = 0; i < 50; i++) { await Task.Delay(150); if (app.Media.Current.Title == "澄屿 系统媒体验证" && app.Media.Current.Playing) { detected = true; break; } }
                File.WriteAllText(Path.Combine(root, "media-observed.json"), System.Text.Json.JsonSerializer.Serialize(new { sessionManager = app.Media.SessionManagerAvailable, media = new { app.Media.Current.Title, app.Media.Current.Artist, app.Media.Current.Playing, app.Media.Current.External }, audible = app.Media.Audible, levels = app.Media.Levels }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                checks["system_media_session_manager_available"] = app.Media.SessionManagerAvailable;
                checks["system_media_title_and_playback_detected"] = detected;
                checks["system_output_meter_reacts_to_real_playback"] = app.Media.Audible && app.Media.Levels.Any(v => v > .005);
                if (detected)
                {
                    await Task.Delay(300);
                    if (island != null) { island.UpdateData(true); checks["system_media_reaches_island_presentation"] = island.PresentedMedia.Title == "澄屿 系统媒体验证" && island.PresentedMedia.Playing && island.PresentedMedia.External; Desktop05Verification.Snapshot(island, Path.Combine(root, "island-system-playing.png")); }
                    app.Media.Command(0); bool paused = false; for (int i = 0; i < 25; i++) { await Task.Delay(150); if (app.Media.Current.Title == "澄屿 系统媒体验证" && !app.Media.Current.Playing) { paused = true; break; } }
                    checks["island_system_pause_command_works"] = paused;
                    if (paused && island != null)
                    {
                        string systemTone = Path.Combine(root, "system-output-only.wav");
                        WriteTone(systemTone, 450);
                        using var sound = new System.Media.SoundPlayer(systemTone);
                        sound.PlayLooping();
                        try
                        {
                            for (int i = 0; i < 30 && !app.Media.Audible; i++) await Task.Delay(100);
                            island.UpdateData(true);
                            checks["raw_system_audio_does_not_show_as_playing"] =
                                app.Media.Audible && !app.Media.Current.Playing && !island.PresentedMedia.Playing;
                        }
                        finally { sound.Stop(); }
                    }
                }
            }
            finally { if (!player.HasExited) { Native.ShowWindow(player.MainWindowHandle, 8); player.CloseMainWindow(); await Task.Delay(250); } }

        }
        finally { app.Manager.Navigate("overview"); app.Apply(p => { p.ShowLaunchpad = original.ShowLaunchpad; p.Spectrum = original.Spectrum; p.ShelfBoxes = original.ShelfBoxes; p.ClockStyle = original.ClockStyle; p.Widgets["clock"].Material = original.Widgets["clock"].Material; }); }
    }
    static void WriteTone(string path, int amplitude = 10000)
    {
        const int rate = 48000, samples = rate * 3; using var writer = new BinaryWriter(File.Create(path)); writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + samples * 2); writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(rate); writer.Write(rate * 2); writer.Write((short)2); writer.Write((short)16); writer.Write(System.Text.Encoding.ASCII.GetBytes("data")); writer.Write(samples * 2); for (int i = 0; i < samples; i++) writer.Write((short)(Math.Sin(2 * Math.PI * 440 * i / rate) * amplitude));
    }
}
