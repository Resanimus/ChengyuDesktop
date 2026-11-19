using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;

namespace ChengyuDock;
internal static class ProcessMetrics
{
    [StructLayout(LayoutKind.Sequential)] struct Counters { public uint Size, Faults; public UIntPtr Peak, Working, PeakPaged, Paged, PeakNonpaged, Nonpaged, Pagefile, PeakPagefile, Private, PrivateWorking; public ulong SharedCommit; }
    [DllImport("kernel32.dll", EntryPoint = "K32GetProcessMemoryInfo")] static extern bool GetMemory(IntPtr process, ref Counters counters, uint size);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct Entry { public uint Size, Usage, Pid; public IntPtr Heap; public uint Module, Threads, Parent; public int Priority; public uint Flags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Exe; }
    [DllImport("kernel32.dll")] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32First(IntPtr snapshot, ref Entry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32Next(IntPtr snapshot, ref Entry entry);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    public static object Sample(string state)
    {
        var snapshot = CreateToolhelp32Snapshot(2, 0); var entries = new List<Entry>();
        if (snapshot != new IntPtr(-1)) try { var e = new Entry { Size = (uint)Marshal.SizeOf<Entry>(), Exe = "" }; if (Process32First(snapshot, ref e)) do { entries.Add(e); } while (Process32Next(snapshot, ref e)); } finally { CloseHandle(snapshot); }
        var family = new HashSet<uint> { (uint)Environment.ProcessId }; bool added; do { added = false; foreach (var e in entries) if (family.Contains(e.Parent)) added |= family.Add(e.Pid); } while (added);
        var list = new List<object>(); long ws = 0, privateBytes = 0; ulong privateWs = 0; bool available = true; int browsers = 0;
        foreach (uint id in family) try { using var p = Process.GetProcessById((int)id); long working = p.WorkingSet64, committed = p.PrivateMemorySize64; var c = new Counters { Size = (uint)Marshal.SizeOf<Counters>() }; bool ok = GetMemory(p.Handle, ref c, c.Size); available &= ok; if (ok) privateWs += c.PrivateWorking.ToUInt64(); ws += working; privateBytes += committed; if (p.ProcessName.Contains("webview", StringComparison.OrdinalIgnoreCase)) browsers++; list.Add(new { pid = id, name = p.ProcessName, workingSetMiB = Math.Round(working / 1048576.0, 2), privateMiB = Math.Round(committed / 1048576.0, 2), privateWorkingSetMiB = ok ? (double?)Math.Round(c.PrivateWorking.ToUInt64() / 1048576.0, 2) : null }); } catch { }
        return new { state, timestamp = DateTimeOffset.Now, workingSetMiB = Math.Round(ws / 1048576.0, 2), privateMiB = Math.Round(privateBytes / 1048576.0, 2), privateWorkingSetMiB = available ? (double?)Math.Round(privateWs / 1048576.0, 2) : null, managedHeapMiB = Math.Round(GC.GetTotalMemory(false) / 1048576.0, 2), browserProcesses = browsers, processCount = list.Count, processes = list };
    }
}
internal static class DesktopVerification
{
    public static async Task Run(bool exit)
    {
        var app = App.Current; string root = app.Store.Root; var samples = new List<object>(); var pages = new List<string>(); var checks = new Dictionary<string, bool>();
        try
        {
            await Task.Delay(1500); await app.Desktop.Ready;
            checks["enabled_component_count"] = app.Desktop.ComponentCount == app.Prefs.Widgets.Count(p => p.Key != "shelf" && p.Value.Enabled) + (app.Prefs.Widgets["shelf"].Enabled ? app.Prefs.ShelfBoxes.Count(b => b.Enabled) : 0);
            checks["native_glass_fixture_rendered"] = await ProbeGlass();
            checks["default_dock_not_topmost"] = !app.Dock.Topmost;
            await Desktop05Verification.Run(checks, root);
            await Desktop06Verification.Run(checks, root);
            await VerifyTodoEditor(checks, root);
            foreach (string page in new[] { "overview", "components", "dock", "island", "spectrum", "storage", "wallpaper", "performance", "content", "settings" })
            { app.Manager.Navigate(page); await Task.Delay(page == "dock" ? 1400 : 180); app.CaptureManager(Path.Combine(root, "manager-" + page + ".png")); pages.Add(page); }
            File.WriteAllText(Path.Combine(root, "gpu-readings.json"), JsonSerializer.Serialize(
                new { source = "Windows PDH + DXGI", adapters = app.Services.GpuReadings, selected = app.Services.SelectedGpu },
                new JsonSerializerOptions { WriteIndented = true }));
            samples.Add(ProcessMetrics.Sample("manager-all-pages-opened"));
            checks["desktop_icon_container_found"] = app.DesktopIcons.IconsFound;
            checks["physical_double_click_hook_installed"] = app.DesktopIcons.HookInstalled;
            var spectrum = app.Prefs.Spectrum;
            bool spectrumShown = app.Prefs.Widgets["spectrum"].Enabled;
            try
            {
                app.Apply(p => { p.Widgets["spectrum"].Enabled = true; p.Spectrum.LiveEnabled = true; p.Spectrum.Source = "system"; });
                await Task.Delay(500);
                checks["wasapi_system_capture_started"] = app.Spectrum.Active;
                app.Apply(p => p.Spectrum.LiveEnabled = false);
                checks["capture_released"] = !app.Spectrum.Active && !app.Store.Load().Spectrum.LiveEnabled;
            }
            finally
            {
                app.Apply(p => { p.Widgets["spectrum"].Enabled = spectrumShown; p.Spectrum = spectrum; });
            }
            var managerHandle = new WindowInteropHelper(app.Manager).Handle;
            checks["widgets_below_manager"] = app.Desktop.Windows.Values.All(w => DesktopLayer.IsBelow(new WindowInteropHelper(w).Handle, managerHandle));
            checks["widgets_nonactivating"] = app.Desktop.Windows.Values.All(w => (DesktopLayer.GetStyle(new WindowInteropHelper(w).Handle, -20).ToInt64() & Native.WS_EX_NOACTIVATE) != 0);
            var original = app.Prefs.Clone();
            try
            {
                app.Apply(p => { foreach (var item in p.Widgets.Values) item.Locked = true; });
                checks["lock_all_persisted"] = new SettingsStore(root).Load().Widgets.Values.All(w => w.Locked);
                checks["locked_widgets_below_manager"] = app.Desktop.Windows.Values.All(w => DesktopLayer.IsBelow(new WindowInteropHelper(w).Handle, managerHandle));
                app.Wallpaper.Select(Path.Combine(AppContext.BaseDirectory, "Assets", "midnight.png")); await Task.Delay(500);
                checks["wallpaper_attached_to_desktop"] = app.Wallpaper.Attached;
                app.Apply(p => p.Wallpaper.Kind = "none"); checks["wallpaper_released"] = !app.Wallpaper.Attached;
                string video = Path.Combine(root, "wallpaper-fixture.mp4");
                if (File.Exists(video))
                {
                    app.Wallpaper.Select(video); app.Apply(p => p.Wallpaper.AutoPause = false);
                    for (int wait = 0; wait < 50 && !app.Wallpaper.VideoDecoded; wait++) await Task.Delay(160);
                    await Task.Delay(400);
                    checks["video_decoded"] = app.Wallpaper.VideoDecoded;
                    var first = WallpaperBackdrop.Sample(500, 300, 240, 180, 1); await Task.Delay(900); var next = WallpaperBackdrop.Sample(500, 300, 240, 180, 2);
                    checks["video_glass_has_live_frames"] = !first.Pixels.SequenceEqual(next.Pixels);
                    app.Apply(p => p.Wallpaper.Paused = true); checks["video_pause"] = !app.Wallpaper.VideoPlaying;
                    app.Apply(p => p.Wallpaper.Kind = "none"); checks["video_released"] = !app.Wallpaper.Attached;
                }
            }
            finally { app.Apply(p => { p.Wallpaper = original.Wallpaper; foreach (var pair in original.Widgets) p.Widgets[pair.Key].Locked = pair.Value.Locked; }); }
            app.Manager.Hide(); await Task.Delay(3000); samples.Add(ProcessMetrics.Sample("manager-hidden-3s"));
            for (int i = 0; i < 5; i++) { await Task.Delay(6000); samples.Add(ProcessMetrics.Sample("idle-" + (i + 1) * 6 + "s")); }
            if (app.Prefs.Wallpaper.Kind != "web")
                checks["no_browser_process_for_non_web_wallpaper"] = samples.All(sample =>
                    JsonSerializer.SerializeToElement(sample).GetProperty("browserProcesses").GetInt32() == 0);
            File.WriteAllText(Path.Combine(root, "desktop-verification.json"), JsonSerializer.Serialize(new { success = checks.Values.All(x => x), checks, pages, memory = samples, version = "0.8.0", memoryMethod = "Process working set and private bytes, including descendants. No EmptyWorkingSet or SetProcessWorkingSetSize calls." }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(root, "desktop-verification.json"), JsonSerializer.Serialize(new { success = false, error = ex.ToString(), checks, memory = samples }, new JsonSerializerOptions { WriteIndented = true })); }
        finally { if (exit) app.Quit(); else app.ShowManager(); }
    }
    static async Task VerifyTodoEditor(Dictionary<string, bool> checks, string root)
    {
        var app = App.Current;
        if (!app.Desktop.Windows.TryGetValue("todayTodo", out var widget)) return;
        var original = app.Prefs.Todos.ToList();
        TodoEditorWindow? editor = null;
        app.Manager.Hide();
        try
        {
            var open = Descendants<System.Windows.Controls.Button>((DependencyObject)widget.Content).Single(b => Equals(b.Content, "＋"));
            open.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            await Task.Delay(250);
            editor = app.Windows.OfType<TodoEditorWindow>().Single();
            checks["todo_widget_opens_independent_editor"] = editor.IsVisible && editor.Owner == null && !app.Manager.IsVisible;
            Desktop05Verification.Snapshot(editor, Path.Combine(root, "todo-editor.png"));
            var save = Descendants<System.Windows.Controls.Button>((DependencyObject)editor.Content).Single(b => Equals(b.Content, "添加待办"));
            save.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            checks["todo_empty_input_keeps_editor_open"] = editor.IsVisible && app.Prefs.Todos.Count == original.Count;

            const string sample = "0.7 待办录入验证";
            var input = Descendants<System.Windows.Controls.TextBox>((DependencyObject)editor.Content).Single(t => System.Windows.Automation.AutomationProperties.GetName(t) == "待办内容");
            input.Text = sample;
            save.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            var persisted = new SettingsStore(root).Load().Todos;
            checks["todo_saved_for_today"] = persisted.Count == original.Count + 1 &&
                persisted.Last().Text == sample && persisted.Last().Date == DateTime.Today.ToString("yyyy-MM-dd");
            checks["todo_saved_without_opening_manager"] = !editor.IsVisible && !app.Manager.IsVisible;
            checks["todo_widget_refreshed_after_save"] = Descendants<System.Windows.Controls.CheckBox>((DependencyObject)widget.Content).Any(c => Equals(c.Content, sample));
        }
        finally
        {
            if (editor?.IsVisible == true) editor.Close();
            app.Apply(p => p.Todos = original);
            app.ShowManager();
        }
    }

    static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    static async Task<bool> ProbeGlass()
    {
        // A deterministic GPU fixture remains renderable when user apps cover every desktop component.
        var pixels = new byte[16 * 16 * 4]; for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = 90; pixels[i + 1] = 70; pixels[i + 2] = 45; pixels[i + 3] = 255; }
        var surface = new GlassSurface { Material = "liquid", Radius = 22, TestVisibility = true, Margin = new Thickness(34) };
        var window = new Window { Title = "澄屿 · 材质验证", Width = 340, Height = 220, WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = System.Windows.Media.Brushes.Transparent, ShowInTaskbar = false, ShowActivated = false, Content = surface };
        surface.InputProvider = () => { var p = surface.PointToScreen(new Point()); return new GlassInput(pixels, 16, 16, (int)p.X, (int)p.Y, 1); };
        window.SourceInitialized += (_, _) => GlassPump.AllowCapture(window);
        try { window.Show(); await Task.Delay(1000); return surface.RenderedFrames > 0; } finally { window.Close(); }
    }
}
