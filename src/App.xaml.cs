using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ChengyuDock;

public partial class App : System.Windows.Application
{
    public static new App Current => (App)System.Windows.Application.Current;
    internal bool InteractionMode => Environment.GetCommandLineArgs().Contains("--interaction-fixture");
    public Preferences Prefs { get; private set; } = null!;
    public SettingsStore Store { get; private set; } = null!;
    public ManagerWindow Manager { get; private set; } = null!;
    public DockWindow Dock { get; private set; } = null!;
    public DesktopSceneWindow Desktop { get; private set; } = null!;
    public DesktopServices Services { get; private set; } = null!;
    public SystemMediaService Media { get; private set; } = null!;
    public SpectrumService Spectrum { get; private set; } = null!;
    public DesktopIconsService DesktopIcons { get; private set; } = null!;
    public TaskbarGuardService TaskbarGuard { get; private set; } = null!;
    public StartupRegistration WindowsStartup { get; internal set; } = null!;
    public bool TaskbarBusy { get; private set; }
    bool _quitting;
    WindowEventMonitor? _windowEvents;
    public WallpaperService Wallpaper { get; private set; } = null!;
    bool _openingLaunchpad;
    readonly Dictionary<string, DateTime> _backgroundErrors = new();
    internal HashSet<string> RunningPaths { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<WindowEntry> OpenWindows { get; private set; } = new();
    public event Action? Changed;
    public event Action? WindowsChanged;
    public event Action<string>? Notice;
    DispatcherTimer? _poll; Forms.NotifyIcon? _tray; LaunchpadWindow? _launchpad; Mutex? _mutex;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        int taskbarFixture = Array.IndexOf(e.Args, "--taskbar-host-fixture");
        if (taskbarFixture >= 0 && e.Args.Length > taskbarFixture + 2) { SystemIntegrationVerification.RunHostFixture(new IntPtr(long.Parse(e.Args[taskbarFixture + 1])), e.Args[taskbarFixture + 2]); return; }
        int mediaFixture = Array.IndexOf(e.Args, "--media-fixture"); if (mediaFixture >= 0 && e.Args.Length > mediaFixture + 1) { MediaFixture.Run(e.Args[mediaFixture + 1]); return; }
        if (e.Args.Contains("--fixture"))
        {
            var fixture = new Window { Title = "澄屿 Dock · 窗口验证样本", Width = 440, Height = 270, Background = new SolidColorBrush(Color.FromRgb(24, 30, 43)), Content = new TextBlock { Text = "Dock 窗口状态验证\n最小化 · 缩略图 · 恢复", FontSize = 22, Foreground = System.Windows.Media.Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center } };
            fixture.Closed += (_, _) => Shutdown(); fixture.Show(); return;
        }
        string root = Path.Combine(AppContext.BaseDirectory, "Data");
        int at = Array.IndexOf(e.Args, "--data-dir"); if (at >= 0 && e.Args.Length > at + 1) root = Path.GetFullPath(e.Args[at + 1]);
        Store = new SettingsStore(root);
        if (e.Args.Contains("--self-test")) { SelfTest.Run(root); Shutdown(); return; }
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant())))[..12];
        _mutex = new Mutex(true, "Local\\ChengyuDock-" + hash, out bool created);
        if (!created) { if (!e.Args.Contains("--autostart")) MessageBox.Show("澄屿 Dock 已在运行。请通过 Dock 图标或系统托盘打开管理界面。", "澄屿 Dock"); Shutdown(); return; }
        DispatcherUnhandledException += (_, args) =>
        {
            try { File.AppendAllText(Path.Combine(Store.Root, "error.log"), DateTimeOffset.Now + "\n" + args.Exception + "\n"); } catch { }
            Notice?.Invoke("操作未完成：" + args.Exception.Message); args.Handled = true;
        };
        Prefs = Store.Load();
        WindowsStartup = new StartupRegistration(Environment.ProcessPath!, Store.Root);
        TaskbarGuard = new TaskbarGuardService();
        TaskbarGuard.Failed += text => { Notify(text); _ = SetTaskbarEnabledAsync(false); };
        Spectrum = new SpectrumService(); Services = new DesktopServices(); Media = new SystemMediaService(); Manager = new ManagerWindow(); MainWindow = Manager;
        Dock = new DockWindow();
        Desktop = new DesktopSceneWindow();
        Wallpaper = new WallpaperService();
        Wallpaper.Updated += Manager.WallpaperUpdated;
        DesktopIcons = new DesktopIconsService();
        _windowEvents = new WindowEventMonitor(RefreshWindows);
        RefreshWindows();
        _tray = new Forms.NotifyIcon { Text = "澄屿桌面", Visible = true, Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Application };
        _tray.MouseUp += (_, args) => { if (args.Button != Forms.MouseButtons.Right) return; Dispatcher.Invoke(() => { var menu = AppMenu.Create("澄屿桌面"); AppMenu.Action(menu, "打开管理界面", ShowManager, "⚙"); AppMenu.Action(menu, "桌面组件", ShowComponentManager, "▦"); AppMenu.Action(menu, "显示 / 隐藏 Dock", () => Apply(p => p.Enabled = !p.Enabled), selected: Prefs.Enabled); AppMenu.Action(menu, "显示 / 隐藏桌面图标", () => DesktopIcons.Toggle(), "◇"); AppMenu.Divider(menu); AppMenu.Action(menu, "退出澄屿", Quit, "×"); AppMenu.Show(menu, Dock); }); };
        _tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowManager);
        Wallpaper.InitializeLibrary(); Wallpaper.Sync(); Desktop.Show(); if (!e.Args.Contains("--autostart")) Manager.Show(); Dock.Sync();
        SyncSpectrumCapture();
        if (InteractionMode) { Manager.Hide(); new InteractionFixtureWindow().Show(); }
        _poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) }; _poll.Tick += (_, _) => RefreshWindows(); _poll.Start();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += DisplayChanged;
        if (Store.LoadNotice.Length > 0) Dispatcher.BeginInvoke(() => Notice?.Invoke(Store.LoadNotice));
        if (Prefs.TaskbarGuardEnabled) Dispatcher.BeginInvoke(async () => await SetTaskbarEnabledAsync(true));
        if (e.Args.Contains("--system-test")) Dispatcher.BeginInvoke(async () => await SystemIntegrationVerification.Run());
        if (e.Args.Contains("--integration-test")) Dispatcher.BeginInvoke(async () => await IntegrationTest());
        if (e.Args.Contains("--desktop-test")) Dispatcher.BeginInvoke(async () => await DesktopVerification.Run(e.Args.Contains("--exit-after-test")));
        if (e.Args.Contains("--wallpaper-test")) Dispatcher.BeginInvoke(async () => await WallpaperVerification.Run());
        int capture = Array.IndexOf(e.Args, "--capture");
        if (capture >= 0 && e.Args.Length > capture + 1) Dispatcher.BeginInvoke(async () => { await Task.Delay(1200); CaptureManager(e.Args[capture + 1]); });
    }
    public void CaptureManager(string path)
    {
        Manager.UpdateLayout(); var bitmap = new RenderTargetBitmap((int)Math.Ceiling(Manager.ActualWidth), (int)Math.Ceiling(Manager.ActualHeight), 96, 96, PixelFormats.Pbgra32); bitmap.Render(Manager);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(path); encoder.Save(file);
    }
    async Task IntegrationTest()
    {
        Process? fixture = null;
        try
        {
            fixture = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--fixture") { UseShellExecute = false });
            if (fixture == null) throw new InvalidOperationException("测试窗口未创建");
            IntPtr handle = IntPtr.Zero;
            for (int i = 0; i < 30 && handle == IntPtr.Zero; i++) { await Task.Delay(150); fixture.Refresh(); handle = fixture.MainWindowHandle; }
            if (handle == IntPtr.Zero) throw new InvalidOperationException("测试窗口未就绪");
            Native.ShowWindow(handle, 6); await Task.Delay(300); RefreshWindows();
            var entry = OpenWindows.FirstOrDefault(x => x.Handle == handle);
            bool enumerated = entry?.Minimized == true, visible = Dock.ContainsWindow(handle);
            int thumbnail = entry == null ? -1 : Dock.ProbePreview(entry); await Task.Delay(350); Dock.EndPreview();
            Native.Activate(handle, false); await Task.Delay(250); bool restored = !Native.IsIconic(handle);
            RefreshWindows(); await Task.Delay(700);
            double alignment = Dock.AlignmentError; bool divider = Dock.DividerInGap;
            var launchpad = new LaunchpadWindow(); launchpad.Show();
            int searchMissing = launchpad.ProbeSearch("qa-no-match-" + Guid.NewGuid().ToString("N"));
            int searchAll = launchpad.ProbeSearch(""); launchpad.Close();
            var chatGptPin = Prefs.Pins.FirstOrDefault(p => PackagedApps.TryGetId(p.Path, out var id) &&
                id.Equals(PackagedApps.ChatGptAppId, StringComparison.OrdinalIgnoreCase));
            var chatGptWindow = chatGptPin == null ? null : OpenWindows.FirstOrDefault(w => DockItems.Matches(chatGptPin, w));
            bool? packagedIcon = chatGptPin == null ? null : Icons.Get(chatGptPin.Path, chatGptPin.Name) is BitmapSource;
            bool? packagedGrouping = null;
            if (chatGptWindow != null)
            {
                var matches = DockItems.Build(Prefs, OpenWindows).Where(e => e.Windows.Any(w => w.Handle == chatGptWindow.Handle)).ToArray();
                packagedGrouping = matches.Length == 1 && matches[0].Pin?.Id == chatGptPin?.Id && Dock.ContainsWindow(chatGptWindow.Handle);
            }
            bool? packagedActivation = null;
            if (chatGptPin != null && Environment.GetCommandLineArgs().Contains("--verify-packaged-activation"))
            {
                uint processId = PackagedApps.Activate(PackagedApps.ChatGptAppId);
                using var process = Process.GetProcessById((int)processId);
                packagedActivation = process.ProcessName.Equals("ChatGPT", StringComparison.OrdinalIgnoreCase);
            }
            bool spectrumExpected = Prefs.Widgets["spectrum"].Enabled && Prefs.Spectrum.LiveEnabled;
            bool result = enumerated && visible && restored && thumbnail == 0 && alignment < .01 && divider &&
                searchMissing == 0 && searchAll == Prefs.Pins.Count && packagedIcon != false && packagedGrouping != false && packagedActivation != false &&
                Spectrum.Active == spectrumExpected;
            File.WriteAllText(Path.Combine(Store.Root, "integration-test-result.json"), JsonSerializer.Serialize(new { success = result, minimized_window_enumerated = enumerated, dock_item_visible = visible, dwm_thumbnail_hresult = thumbnail, restored, icon_center_error_dip = alignment, divider_between_sections = divider, launchpad_empty_search_count = searchMissing, launchpad_all_count = searchAll, chatgpt_shell_icon = packagedIcon, chatgpt_native_app_id = chatGptWindow?.AppId, chatgpt_window_grouped_at_pin = packagedGrouping, chatgpt_activation_process_verified = packagedActivation, spectrum_capture_expected = spectrumExpected, spectrum_capture_active = Spectrum.Active, spectrum_capture_status = Spectrum.Status, version = "0.8.0" }, new JsonSerializerOptions { WriteIndented = true }));
            Notice?.Invoke(result ? "窗口状态、缩略图与恢复验证通过" : "窗口验证有未通过项，详见测试记录");
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(Store.Root, "integration-test-result.json"), JsonSerializer.Serialize(new { success = false, error = ex.ToString() }, new JsonSerializerOptions { WriteIndented = true })); }
        finally { if (fixture != null && !fixture.HasExited) fixture.CloseMainWindow(); fixture?.Dispose(); if (Environment.GetCommandLineArgs().Contains("--exit-after-test")) Quit(); else Manager.Activate(); }
    }
    void DisplayChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() => { Dock.Sync(); Desktop.Sync(); });
    void SyncSpectrumCapture()
    {
        if (Prefs.Widgets["spectrum"].Enabled && Prefs.Spectrum.LiveEnabled)
            Spectrum.Start(Prefs.Spectrum.Source);
        else if (Spectrum.Active)
            Spectrum.Stop();
    }

    public bool Apply(Action<Preferences> change)
    {
        var next = Prefs.Clone(); change(next);
        try { Store.Save(next); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or System.IO.InvalidDataException)
        { Changed?.Invoke(); Notice?.Invoke("保存失败，已保留原设置：" + ex.Message); return false; }
        bool spectrumChanged = next.Widgets["spectrum"].Enabled != Prefs.Widgets["spectrum"].Enabled ||
            next.Spectrum.LiveEnabled != Prefs.Spectrum.LiveEnabled ||
            next.Spectrum.Source != Prefs.Spectrum.Source;
        Prefs = next;
        Dock.Sync();
        Changed?.Invoke();
        Notice?.Invoke("设置已保存");
        if (spectrumChanged) SyncSpectrumCapture();
        return true;
    }
    public async Task SetTaskbarEnabledAsync(bool enabled)
    {
        if (TaskbarBusy || _quitting) return;
        TaskbarBusy = true; Changed?.Invoke();
        bool previous = TaskbarGuard.Running;
        try
        {
            await TaskbarGuard.SetEnabledAsync(enabled);
            if (!Apply(p => p.TaskbarGuardEnabled = enabled)) await TaskbarGuard.SetEnabledAsync(previous);
            else Notify(enabled ? TaskbarGuard.Status : "任务栏遮挡已关闭，任务栏已恢复");
        }
        catch (Exception ex)
        {
            if (Prefs.TaskbarGuardEnabled != TaskbarGuard.Running) Apply(p => p.TaskbarGuardEnabled = TaskbarGuard.Running);
            Notify("任务栏设置未完成：" + ex.Message);
        }
        finally { TaskbarBusy = false; Changed?.Invoke(); }
    }
    public void SetStartupEnabled(bool enabled)
    {
        try { WindowsStartup.SetEnabled(enabled); Notify(enabled ? "已开启：登录后在托盘运行澄屿" : "已关闭开机自启动"); }
        catch (Exception ex) { Notify("自启动设置未完成：" + ex.Message); }
        Changed?.Invoke();
    }
    TodoEditorWindow? _todoEditor;
    internal void AddTodo(Window? owner = null)
    {
        if (_todoEditor != null) { _todoEditor.Activate(); return; }
        var editor = new TodoEditorWindow();
        if (owner?.IsVisible == true)
        {
            editor.Owner = owner;
            editor.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        _todoEditor = editor;
        editor.Closed += (_, _) => _todoEditor = null;
        editor.Show();
        editor.Activate();
    }
    public void ShowManager() { Manager.Show(); if (Manager.WindowState == WindowState.Minimized) Manager.WindowState = WindowState.Normal; Manager.Activate(); }
    public async void ShowLaunchpad()
    {
        if (_launchpad?.IsVisible == true) { _launchpad.Close(); return; }
        if (_openingLaunchpad) return; _openingLaunchpad = true;
        try { var scene = await GlassPump.CaptureVisibleScene(Dock); _launchpad = new LaunchpadWindow(scene); _launchpad.Show(); _launchpad.Activate(); }
        catch (Exception ex) { ReportBackgroundError("启动台", ex); }
        finally { _openingLaunchpad = false; }
    }
    public void ShowComponentManager() { ShowManager(); Manager.Navigate("components"); }
    public void Notify(string text) => Notice?.Invoke(text);
    public void ReportBackgroundError(string source, Exception error)
    {
        if (_backgroundErrors.TryGetValue(source, out var last) && DateTime.UtcNow - last < TimeSpan.FromMinutes(1)) return;
        _backgroundErrors[source] = DateTime.UtcNow;
        try { File.AppendAllText(Path.Combine(Store.Root, "error.log"), DateTimeOffset.Now + " " + source + "\n" + error + "\n"); } catch { }
    }
    public void RefreshWindows()
    {
        OpenWindows = Native.EnumerateWindows(); RunningPaths = DockItems.RunningPaths(); WindowsChanged?.Invoke();
    }
    public WindowEntry? Match(Pin pin)
    {
        if (Directory.Exists(pin.Path)) return null;
        return OpenWindows.FirstOrDefault(w => DockItems.Matches(pin, w));
    }
    public void Launch(Pin pin)
    {
        RefreshWindows(); var match = Match(pin);
        if (match != null) { Native.Activate(match.Handle, true); return; }
        try
        {
            string target = PackagedApps.UpgradeLegacyTarget(pin.Path);
            if (PackagedApps.TryGetId(target, out var appId)) PackagedApps.Activate(appId);
            else Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            string message = "无法打开“" + pin.Name + "”：" + ex.Message;
            Notify(message);
            _tray?.ShowBalloonTip(4000, "澄屿桌面", message, Forms.ToolTipIcon.Warning);
        }
    }
    internal void PinRunningApp(string path, string name)
    {
        if (!PackagedApps.TryGetId(path, out var id))
        {
            AddPaths(new[] { path });
            return;
        }
        if (id.Equals(PackagedApps.ChatGptAppId, StringComparison.OrdinalIgnoreCase)) name = "ChatGPT";
        AddPins(new[] { new Pin { Path = path, Name = name } });
    }

    public void AddPaths(string[] paths)
    {
        AddPins(paths.Where(path => File.Exists(path) || Directory.Exists(path)).Select(path => new Pin
        {
            Path = PackagedApps.UpgradeLegacyTarget(path),
            Name = Directory.Exists(path) ? new DirectoryInfo(path).Name : Path.GetFileNameWithoutExtension(path),
        }).ToArray());
    }

    void AddPins(Pin[] pins)
    {
        if (pins.Length == 0) return;
        Apply(p =>
        {
            foreach (var pin in pins)
            {
                string target = DockItems.Target(pin.Path);
                bool packaged = PackagedApps.TryGetId(target, out _);
                if (p.Pins.Any(existing => string.Equals(existing.Path, pin.Path, StringComparison.OrdinalIgnoreCase) ||
                    packaged && string.Equals(DockItems.Target(existing.Path), target, StringComparison.OrdinalIgnoreCase))) continue;
                if (p.Pins.Count >= 32) { Notice?.Invoke("最多固定 32 个项目"); break; }
                p.Pins.Add(pin);
            }
        });
    }
    public void ReorderPins(string[] ids) => Apply(p => p.Pins = ids.Select(id => p.Pins.FirstOrDefault(x => x.Id == id)).Where(x => x != null).Cast<Pin>().ToList());
    public async void Quit()
    {
        if (_quitting) return; _quitting = true;
        if (TaskbarGuard != null)
        {
            try { await TaskbarGuard.SetEnabledAsync(false); }
            catch (Exception ex) { ReportBackgroundError("任务栏恢复", ex); }
            TaskbarGuard.Dispose();
        }
        _poll?.Stop(); _windowEvents?.Dispose(); DesktopIcons?.Dispose(); Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= DisplayChanged;
        Manager?.AllowClose(); _todoEditor?.Close(); _launchpad?.Close(); Wallpaper?.Dispose(); Desktop?.Dispose(); Spectrum?.Dispose(); Media?.Dispose(); Services?.Dispose(); GlassPump.Stop(); Dock?.Dispose(); _tray?.Dispose(); _mutex?.Dispose(); Shutdown();
    }
}
