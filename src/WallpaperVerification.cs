using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ChengyuDock;

internal static class WallpaperVerification
{
    internal static async Task Run()
    {
        var app = App.Current;
        var original = app.Prefs.Wallpaper;
        var checks = new Dictionary<string, bool>();
        var details = new Dictionary<string, object?>();
        try
        {
            string fixture = Path.Combine(app.Store.Root, "wallpaper-scan-fixture");
            Directory.CreateDirectory(fixture);
            string video = Path.Combine(fixture, "video");
            string scene = Path.Combine(fixture, "scene");
            string malformed = Path.Combine(fixture, "malformed");
            string outside = Path.Combine(fixture, "outside");
            foreach (string folder in new[] { video, scene, malformed, outside }) Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(video, "project.json"), """{"title":"电影样本","type":"Video","file":"sample.mp4","preview":"preview.jpg"}""");
            File.WriteAllText(Path.Combine(video, "sample.mp4"), "");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Assets", "midnight.png"), Path.Combine(video, "preview.jpg"), true);
            File.WriteAllText(Path.Combine(scene, "project.json"), """{"title":"场景样本","type":"scene","file":"scene.json","preview":"preview.jpg"}""");
            File.WriteAllText(Path.Combine(scene, "scene.pkg"), "");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Assets", "midnight.png"), Path.Combine(scene, "preview.jpg"), true);
            File.WriteAllText(Path.Combine(malformed, "project.json"), "{broken");
            File.WriteAllText(Path.Combine(outside, "project.json"), """{"title":"越界样本","type":"application","file":"../not-a-wallpaper.exe"}""");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Assets", "midnight.png"), Path.Combine(fixture, "loose.jpg"), true);
            File.WriteAllText(Path.Combine(fixture, "not-a-wallpaper.exe"), "");
            var result = WallpaperLibrary.Scan(new[] { fixture, video, Path.Combine(fixture, "missing") }, CancellationToken.None);
            checks["overlapping_sources_deduplicated"] = result.Items.Count == 3;
            checks["video_metadata_and_preview"] = result.Items.Single(x => x.Kind == "video").Title == "电影样本";
            checks["scene_pkg_payload_recognized"] = result.Items.Single(x => x.Kind == "scene").Available;
            checks["invalid_project_and_missing_source_reported"] = result.Issues.Count == 2;
            checks["raw_executable_and_preview_excluded"] = result.Items.All(x => x.Kind != "application" && x.Title != "preview");
            var before = app.Prefs.Wallpaper;
            try
            {
                app.Apply(p => { p.Wallpaper.LibrarySources = new() { fixture }; p.Wallpaper.PlaybackRate = 125; p.Wallpaper.Volume = 37; p.Wallpaper.AudioEnabled = true; });
                var loaded = app.Store.Load().Wallpaper;
                checks["library_speed_audio_roundtrip"] = loaded.LibrarySources.SequenceEqual(new[] { fixture }) &&
                    loaded.PlaybackRate == 125 && loaded.Volume == 37 && loaded.AudioEnabled;
            }
            finally { app.Apply(p => p.Wallpaper = before); }

            var detected = WallpaperLibrary.DiscoverSteam();
            details["detected_sources"] = detected.Sources;
            details["detected_engine"] = detected.Engine;
            var actual = await Task.Run(() => WallpaperLibrary.Scan(detected.Sources, CancellationToken.None));
            details["local_wallpaper_count"] = actual.Items.Count;
            details["local_types"] = actual.Items.GroupBy(x => x.Kind).ToDictionary(g => g.Key, g => g.Count());
            details["scan_issues"] = actual.Issues;
            app.Wallpaper.Library.Refresh(detected.Sources);
            for (int i = 0; i < 100 && app.Wallpaper.Library.Scanning; i++) await Task.Delay(100);
            checks["background_scan_published"] = !app.Wallpaper.Library.Scanning && app.Wallpaper.Library.Items.Count == actual.Items.Count;
            var movie = actual.Items.FirstOrDefault(x => x.Kind == "video" && x.Available);
            if (movie != null)
            {
                app.Apply(p => { p.Wallpaper.AudioEnabled = false; p.Wallpaper.AutoPause = false; });
                app.Wallpaper.Select(movie);
                for (int i = 0; i < 75 && !app.Wallpaper.VideoDecoded; i++) await Task.Delay(100);
                checks["video_wallpaper_decoded_and_attached"] = app.Wallpaper.VideoDecoded && app.Wallpaper.Attached;
                app.Apply(p => { p.Wallpaper.PlaybackRate = 150; p.Wallpaper.Paused = true; p.Wallpaper.AudioEnabled = true; p.Wallpaper.Volume = 37; });
                checks["video_speed_volume_and_audio_switch"] = app.Wallpaper.VideoSpeed == 1.5 &&
                    app.Wallpaper.VideoMuted == false && app.Wallpaper.VideoVolume is > .369 and < .371;
                app.Apply(p => p.Wallpaper.AudioEnabled = false);
                checks["video_mute_keeps_volume_preference"] = app.Wallpaper.VideoMuted == true && app.Prefs.Wallpaper.Volume == 37;
            }
            else checks["video_wallpaper_sample_found"] = false;

            if (movie != null && Environment.GetCommandLineArgs().Contains("--verify-wallpaper-performance"))
            {
                var screen = new Native.NativeRect { Right = 1920, Bottom = 1080 };
                var left = new Native.NativeRect { Right = 960, Bottom = 1080 };
                var right = new Native.NativeRect { Left = 960, Right = 1920, Bottom = 1080 };
                checks["coverage_does_not_double_count_overlap"] = Occlusion.Coverage(screen, new[] { left, left }) == .5;
                checks["coverage_combines_snapped_windows"] = Occlusion.Coverage(screen, new[] { left, right }) == 1;
                app.Apply(p => { p.Wallpaper.AutoPause = false; p.Wallpaper.Paused = false; });
                for (int i = 0; i < 60 && app.Wallpaper.VideoPosition.TotalMilliseconds < 300; i++) await Task.Delay(100);
                var warm = ProcessMetrics.Sample("video-playing");
                app.Apply(p => p.Wallpaper.AutoPause = true);
                app.Wallpaper.ProbeWallpaperCoverage(.55);
                checks["partial_cover_keeps_video_and_slows_glass"] = app.Wallpaper.VideoPlaying &&
                    app.Wallpaper.GlassSampleInterval == 250;
                app.Wallpaper.ProbeWallpaperCoverage(.99);
                var resumeAt = app.Wallpaper.VideoPosition;
                checks["near_full_cover_pauses_with_decoder_retained"] = !app.Wallpaper.VideoPlaying &&
                    app.Wallpaper.VideoDecoded && !app.Wallpaper.ResourcesReleased;
                app.Wallpaper.ProbeWallpaperCoverage(.99, 12200);
                await Task.Delay(250);
                checks["long_cover_disposes_video_but_keeps_frame"] = app.Wallpaper.ResourcesReleased &&
                    !app.Wallpaper.VideoDecoded && app.Wallpaper.BackdropVisual is System.Windows.Controls.Image;
                details["video_resource_samples"] = new[] { warm, ProcessMetrics.Sample("video-decoder-released") };
                app.Apply(p => p.Wallpaper.AutoPause = false);
                for (int i = 0; i < 60 && (!app.Wallpaper.VideoDecoded || app.Wallpaper.VideoPosition <= resumeAt); i++) await Task.Delay(100);
                checks["video_resumes_near_position_after_release"] = !app.Wallpaper.ResourcesReleased &&
                    app.Wallpaper.VideoDecoded && app.Wallpaper.VideoPosition >= resumeAt;
            }

            if (Environment.GetCommandLineArgs().Contains("--verify-report-videos"))
            {
                var samples = actual.Items.Where(x => x.Kind == "video" && x.Available &&
                    (x.Title is "鲸落" or "秋日智乃" || x.Title.StartsWith("【舰c】響", StringComparison.Ordinal) ||
                        x.Title.StartsWith("Arknights, 迷迭香", StringComparison.Ordinal))).ToArray();
                checks["reported_video_samples_found"] = samples.Length == 4;
                foreach (var sample in samples)
                {
                    app.Wallpaper.Select(sample);
                    for (int i = 0; i < 75 && !app.Wallpaper.VideoDecoded; i++) await Task.Delay(100);
                    var beforePosition = app.Wallpaper.VideoPosition;
                    await Task.Delay(350);
                    bool advanced = app.Wallpaper.VideoPosition != beforePosition;
                    bool decoded = app.Wallpaper.VideoDecoded && app.Wallpaper.NativeWallpaperVisible;
                    bool differentFrames = false;
                    if (decoded)
                    {
                        var firstFrame = WallpaperBackdrop.Sample(500, 280, 320, 180, WallpaperBackdrop.Revision);
                        await Task.Delay(400);
                        var nextFrame = WallpaperBackdrop.Sample(500, 280, 320, 180, WallpaperBackdrop.Revision);
                        differentFrames = !firstFrame.Pixels.SequenceEqual(nextFrame.Pixels);
                    }
                    checks["reported_video_" + Path.GetFileName(Path.GetDirectoryName(sample.Project))] = decoded && advanced && differentFrames;
                    details[sample.Title] = new { decoded, advanced, differentFrames, status = app.Wallpaper.Status };
                }
                checks["native_window_has_desktop_composition_style"] = app.Wallpaper.NativeWallpaperComposited;

                var startPaused = samples.FirstOrDefault(x => x.Title == "鲸落");
                if (startPaused != null)
                {
                    app.Apply(p =>
                    {
                        p.Wallpaper.Kind = "video";
                        p.Wallpaper.Path = startPaused.Path;
                        p.Wallpaper.Paused = true;
                    });
                    for (int i = 0; i < 60 && !app.Wallpaper.VideoDecoded; i++) await Task.Delay(100);
                    await Task.Delay(350);
                    var frame = WallpaperBackdrop.Sample(500, 280, 320, 180, WallpaperBackdrop.Revision);
                    bool visiblePixels = frame.Pixels.Where((_, index) => index % 4 != 3).Any(value => value > 20);
                    checks["video_displays_first_frame_when_started_paused"] = app.Wallpaper.VideoDecoded &&
                        !app.Wallpaper.VideoPlaying && visiblePixels;
                    var pausedPosition = app.Wallpaper.VideoPosition;
                    app.Apply(p => p.Wallpaper.Paused = false);
                    await Task.Delay(550);
                    checks["video_resumes_after_initial_pause"] = app.Wallpaper.VideoPlaying && app.Wallpaper.VideoPosition > pausedPosition;
                }
            }

            string webFolder = Path.Combine(app.Store.Root, "web-wallpaper-fixture");
            Directory.CreateDirectory(webFolder);
            string webProject = Path.Combine(webFolder, "project.json");
            File.WriteAllText(webProject, """{"title":"HTML test","type":"web","file":"壁纸.html","general":{"properties":{"color":{"type":"color","value":"test-color"}}}}""");
            File.WriteAllText(Path.Combine(webFolder, "壁纸.html"), """
                <!doctype html><meta charset="utf-8"><script src="local.js"></script><video id="media"></video>
                """);
            File.WriteAllText(Path.Combine(webFolder, "local.js"), """
                window.localResourceLoaded = true;
                window.wallpaperPropertyListener = { applyUserProperties: p => window.receivedColor = p.color.value };
                """);
            app.Apply(p => { p.Wallpaper.EnginePath = ""; p.Wallpaper.AutoPause = false; });
            app.Wallpaper.Select(webProject);
            for (int i = 0; i < 150 && app.Wallpaper.Web is { Ready: false, Error.Length: 0 }; i++) await Task.Delay(100);
            checks["web_wallpaper_loaded_without_engine"] = app.Prefs.Wallpaper.Kind == "web" &&
                app.Wallpaper.Attached && app.Wallpaper.Web?.Ready == true && app.Wallpaper.EngineHandle == IntPtr.Zero;
            details["web_native_status"] = app.Wallpaper.Status;
            if (app.Wallpaper.Web is { Ready: true } web)
            {
                app.Apply(p => { p.Wallpaper.PlaybackRate = 150; p.Wallpaper.Volume = 37; p.Wallpaper.AudioEnabled = true; });
                string state = await web.ReadScript("localResourceLoaded && receivedColor === 'test-color' && media.playbackRate === 1.5 && media.volume === 0.37 && !media.muted");
                checks["web_local_assets_properties_and_playback"] = state == "true";
                app.Apply(p => p.Wallpaper.AudioEnabled = false);
                checks["web_mute"] = await web.ReadScript("media.muted") == "true";
            }
            var workshopWeb = actual.Items.FirstOrDefault(x => x.NativeWeb && x.Title == "UI toggle wallpaper");
            if (workshopWeb != null)
            {
                app.Wallpaper.Select(workshopWeb);
                for (int i = 0; i < 150 && app.Wallpaper.Web is { Ready: false, Error.Length: 0 }; i++) await Task.Delay(100);
                checks["workshop_web_runs_without_engine"] = app.Wallpaper.Web?.Ready == true &&
                    await app.Wallpaper.Web.ReadScript("typeof p5 === 'function'") == "true";
                details["workshop_native_web_status"] = app.Wallpaper.Status;
            }
            if (movie != null)
            {
                var selectedVideo = actual.Items.FirstOrDefault(x => x.Kind == "video" && x.Available &&
                    x.Path.Equals(original.Path, StringComparison.OrdinalIgnoreCase)) ?? movie;
                app.Apply(p => p.Wallpaper.AutoPause = !Environment.GetCommandLineArgs().Contains("--verify-wallpaper-performance"));
                string picture = Path.Combine(AppContext.BaseDirectory, "Assets", "midnight.png");
                app.Wallpaper.Select(picture);
                await Task.Delay(250);
                IntPtr first = app.Wallpaper.NativeHandle;
                checks["native_image_visible"] = app.Wallpaper.NativeWallpaperVisible;
                for (int pass = 1; pass <= 3; pass++)
                {
                    app.Wallpaper.Select(selectedVideo);
                    for (int i = 0; i < 60 && !app.Wallpaper.VideoDecoded; i++) await Task.Delay(100);
                    var position = app.Wallpaper.VideoPosition;
                    await Task.Delay(300);
                    checks["native_video_visible_" + pass] = app.Wallpaper.VideoDecoded && app.Wallpaper.NativeWallpaperVisible &&
                        app.Wallpaper.VideoPosition != position;
                    app.Wallpaper.Select(webProject);
                    for (int i = 0; i < 70 && app.Wallpaper.Web is { Ready: false, Error.Length: 0 }; i++) await Task.Delay(100);
                    checks["native_web_visible_" + pass] = app.Wallpaper.Web?.Ready == true && app.Wallpaper.NativeWallpaperVisible;
                    app.Wallpaper.Select(picture);
                    await Task.Delay(150);
                    checks["native_image_visible_again_" + pass] = app.Wallpaper.NativeWallpaperVisible;
                    checks["native_desktop_host_reused_" + pass] = first != IntPtr.Zero && first == app.Wallpaper.NativeHandle;
                }
            }
            app.Apply(p => p.Wallpaper.Kind = "none");
            checks["web_wallpaper_released"] = app.Wallpaper.Web == null && !app.Wallpaper.Attached;
            app.Apply(p => { p.Wallpaper.Kind = "engine"; p.Wallpaper.Path = Path.Combine(scene, "project.json"); });
            checks["saved_engine_project_uses_preview_until_requested"] = app.Wallpaper.Attached &&
                app.Wallpaper.EngineHandle == IntPtr.Zero && !app.Wallpaper.Busy &&
                app.Wallpaper.Status.Contains("预览图", StringComparison.Ordinal);
            app.Apply(p => p.Wallpaper.Kind = "none");
            if (Environment.GetCommandLineArgs().Contains("--verify-native-scenes"))
            {
                app.Apply(p => { p.Wallpaper.EnginePath = ""; p.Wallpaper.AutoPause = false; });
                foreach (string id in new[] { "2000793269", "1120440003", "arsenal" })
                {
                    var sample = actual.Items.FirstOrDefault(x => x.NativeScene &&
                        string.Equals(Path.GetFileName(Path.GetDirectoryName(x.Project)), id, StringComparison.OrdinalIgnoreCase));
                    if (sample == null) { details["scene_" + id] = "sample unavailable"; continue; }
                    app.Wallpaper.Select(sample);
                    for (int i = 0; i < 220 && app.Wallpaper.Web is { Ready: false, Error.Length: 0 }; i++) await Task.Delay(100);
                    var renderer = app.Wallpaper.Web;
                    bool ready = renderer?.Ready == true && app.Prefs.Wallpaper.Kind == "scene" && app.Wallpaper.Attached;
                    int colors = ready ? await CountFrameColors(renderer!) : 0;
                    checks["native_scene_" + id] = ready && colors > 1;
                    details["scene_" + id] = new { sample.Title, ready, colors, app.Wallpaper.Status, diagnostics = renderer?.Diagnostics.ToArray() };
                    if (ready && id == "1120440003" && Environment.GetCommandLineArgs().Contains("--verify-wallpaper-performance"))
                        await VerifySceneCoverage(app, renderer!, checks, details);
                }
                app.Apply(p => p.Wallpaper.Kind = "none");
            }

            if (Environment.GetCommandLineArgs().Contains("--verify-wallpaper-engine"))
            {
                app.Apply(p => { p.Wallpaper.EnginePath = detected.Engine; p.Wallpaper.AudioEnabled = false; p.Wallpaper.PlaybackRate = 100; });
                foreach (string kind in new[] { "scene", "web", "application" })
                {
                    var item = actual.Items.FirstOrDefault(x => x.Kind == kind && x.Available &&
                        x.Project.Contains("defaultprojects", StringComparison.OrdinalIgnoreCase));
                    if (item == null) { details[kind + "_sample"] = "not available"; continue; }
                    app.Wallpaper.Select(item, engine: true);
                    for (int i = 0; i < 170 && app.Wallpaper.Busy; i++) await Task.Delay(100);
                    checks[kind + "_engine_attached"] = app.Prefs.Wallpaper.Kind == "engine" && app.Wallpaper.Attached && !app.Wallpaper.Busy;
                    details[kind + "_status"] = app.Wallpaper.Status;
                    IntPtr renderer = app.Wallpaper.EngineHandle;
                    app.Apply(p => p.Wallpaper.Kind = "none");
                    for (int i = 0; i < 30 && Native.IsWindow(renderer); i++) await Task.Delay(100);
                    checks[kind + "_released"] = !app.Wallpaper.Attached && !Native.IsWindow(renderer);
                }
            }
            app.Manager.Navigate("wallpaper");
            app.Manager.Show();
            app.Manager.UpdateLayout();
            await Task.Delay(300);
            app.CaptureManager(Path.Combine(app.Store.Root, "manager-wallpaper-library.png"));
        }
        catch (Exception ex) { checks["completed_without_exception"] = false; details["error"] = ex.ToString(); }
        finally
        {
            app.Apply(p => p.Wallpaper = original);
            File.WriteAllText(Path.Combine(app.Store.Root, "wallpaper-verification.json"),
                JsonSerializer.Serialize(new { success = checks.Values.All(x => x), checks, details }, new JsonSerializerOptions { WriteIndented = true }));
            if (Environment.GetCommandLineArgs().Contains("--exit-after-test")) app.Quit();
        }
    }

    static async Task<int> CountFrameColors(WebWallpaper renderer)
    {
        using var stream = new MemoryStream();
        await renderer.CaptureFrame(stream).WaitAsync(TimeSpan.FromSeconds(6));
        stream.Position = 0;
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        var small = new TransformedBitmap(frame, new ScaleTransform(24d / frame.PixelWidth, 16d / frame.PixelHeight));
        var pixels = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
        var data = new byte[pixels.PixelWidth * pixels.PixelHeight * 4];
        pixels.CopyPixels(data, pixels.PixelWidth * 4, 0);
        var colors = new HashSet<int>();
        for (int i = 0; i < data.Length; i += 4)
            colors.Add((data[i] >> 4) | (data[i + 1] & 0xf0) | ((data[i + 2] & 0xf0) << 4));
        return colors.Count;
    }

    static async Task VerifySceneCoverage(App app, WebWallpaper renderer, Dictionary<string, bool> checks,
        Dictionary<string, object?> details)
    {
        var baseline = ProcessMetrics.Sample("scene-playing");
        app.Apply(p => p.Wallpaper.AutoPause = true);
        app.Wallpaper.ProbeWallpaperCoverage(.55);
        checks["scene_partial_coverage_uses_20_fps_target"] =
            await renderer.ReadScript("playback.fps === 20 && !scene.paused") == "true";
        app.Wallpaper.ProbeWallpaperCoverage(.8);
        checks["scene_high_coverage_uses_12_fps_target"] =
            await renderer.ReadScript("playback.fps === 12 && !scene.paused") == "true";
        app.Wallpaper.ProbeWallpaperCoverage(.99);
        for (int i = 0; i < 6 && !renderer.Suspended; i++) await Task.Delay(50);
        checks["scene_near_full_cover_suspends_webview"] = renderer.Suspended;
        app.Wallpaper.ProbeWallpaperCoverage(.99, 12200);
        checks["scene_long_cover_disposes_webview"] = app.Wallpaper.ResourcesReleased && app.Wallpaper.Web == null;
        for (int i = 0; i < 8; i++)
        {
            await Task.Delay(200);
            app.Wallpaper.ProbeWallpaperCoverage(.99, 12200);
        }
        details["scene_resource_samples"] = new[] { baseline, ProcessMetrics.Sample("scene-webview-released") };
        app.Apply(p => p.Wallpaper.AutoPause = false);
        for (int i = 0; i < 220 && app.Wallpaper.Web is { Ready: false, Error.Length: 0 }; i++) await Task.Delay(100);
        checks["scene_renders_after_releasing_webview"] = app.Wallpaper.Web is { Ready: true } restarted &&
            !ReferenceEquals(renderer, restarted) && await CountFrameColors(restarted) > 1;
    }

}
