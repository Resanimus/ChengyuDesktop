using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace ChengyuDock;
public sealed class WallpaperService : IDisposable
{
    WallpaperWindow? _window;
    EngineWallpaper? _engine;
    CancellationTokenSource? _opening;
    bool _disposed;
    string? _requestedEngineProject;
    readonly Image _enginePreview = new() { Stretch = Stretch.UniformToFill };
    internal WallpaperLibrary Library { get; } = new();
    public string Status { get; private set; } = "";
    public bool Busy { get; private set; }
    public event Action? Updated;
    string _path = "", _kind = "";
    internal Visual? BackdropVisual => _engine?.Attached == true ? _enginePreview : _window?.BackdropVisual;
    public bool Attached => _window?.Attached == true || _engine?.Attached == true;
    internal IntPtr EngineHandle => _engine?.WindowHandle ?? IntPtr.Zero;
    internal bool VideoDecoded => _window?.VideoDecoded == true;
    internal bool VideoPlaying => _window?.VideoPlaying == true;
    internal int GlassSampleInterval => _window?.GlassSampleInterval ?? 100;
    internal long ContentRevision { get; private set; }
    internal bool ResourcesReleased => _window?.ResourcesReleased == true;
    internal void ProbeWallpaperCoverage(double coverage, long age = 0) => _window?.ProbeCoverage(coverage, age);
    internal TimeSpan VideoPosition => _window?.VideoPosition ?? TimeSpan.Zero;
    internal double? VideoSpeed => _window?.VideoSpeed;
    internal double? VideoVolume => _window?.VideoVolume;
    internal bool? VideoMuted => _window?.VideoMuted;
    internal WebWallpaper? Web => _window?.Web;
    internal IntPtr NativeHandle => _window?.DesktopHandle ?? IntPtr.Zero;
    internal bool NativeWallpaperVisible => _window?.CoversPrimaryScreen == true;
    internal bool NativeWallpaperComposited => _window?.HasDesktopCompositionStyle == true;
    public WallpaperService() { App.Current.Changed += Sync; Library.Changed += LibraryChanged; }
    void LibraryChanged() => Updated?.Invoke();
    public void Choose() { var dialog = new OpenFileDialog { Title = "选择本地壁纸", Filter = "图片、视频、网页与壁纸项目|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff;*.mp4;*.wmv;*.mov;*.m4v;*.webm;*.mkv;*.avi;*.mpeg;*.mpg;*.html;*.htm;project.json|壁纸项目|project.json|所有文件|*.*" }; if (dialog.ShowDialog(App.Current.Manager) == true) Select(dialog.FileName); }
    internal void InitializeLibrary()
    {
        var p = App.Current.Prefs.Wallpaper;
        if (p.Kind == "engine" && File.Exists(p.Path))
        {
            try
            {
                if (WallpaperLibrary.ReadProject(p.Path, Path.GetDirectoryName(p.Path)!)?.NativeWeb == true)
                    App.Current.Apply(next => next.Wallpaper.Kind = "web");
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or ArgumentException)
            { App.Current.ReportBackgroundError("迁移网页壁纸", ex); }
        }
        if (!p.LibraryInitialized)
        {
            var detected = WallpaperLibrary.DiscoverSteam();
            App.Current.Apply(next =>
            {
                next.Wallpaper.LibraryInitialized = true;
                if (next.Wallpaper.LibrarySources.Count == 0) next.Wallpaper.LibrarySources.AddRange(detected.Sources);
                if (next.Wallpaper.EnginePath.Length == 0) next.Wallpaper.EnginePath = detected.Engine;
            });
        }
        Library.Sync(App.Current.Prefs.Wallpaper.LibrarySources);
    }
    internal void AddSources(string[] sources)
    {
        var valid = sources.Where(Directory.Exists).Select(Path.GetFullPath).ToArray();
        App.Current.Apply(p =>
        {
            p.Wallpaper.LibraryInitialized = true;
            foreach (string source in valid)
                if (!p.Wallpaper.LibrarySources.Contains(source, StringComparer.OrdinalIgnoreCase))
                    p.Wallpaper.LibrarySources.Add(source);
        });
    }
    internal void Select(WallpaperItem item, bool engine = false)
    {
        if (!engine && item.UsesEngine && !item.NativeWeb && !item.NativeScene)
        {
            if (item.HasPreview) Select(item.Preview);
            else App.Current.Notify("此项目没有可用的预览图。可在壁纸库选择原引擎播放。");
            return;
        }
        if (!item.Available) { App.Current.Notify("壁纸主文件不可用，请检查项目是否下载完整。"); return; }
        if (engine)
        {
            if (!EngineWallpaper.ValidExecutable(App.Current.Prefs.Wallpaper.EnginePath))
            { App.Current.Notify("先在壁纸页选择 Wallpaper Engine 程序。"); return; }
            string? previousRequest = _requestedEngineProject;
            _requestedEngineProject = item.Project;
            if (!App.Current.Apply(p => { p.Wallpaper.Path = item.Project; p.Wallpaper.Kind = "engine"; p.Wallpaper.Paused = false; }))
                _requestedEngineProject = previousRequest;
        }
        else if (item.NativeWeb) SelectLocal(item.Key, "web");
        else if (item.NativeScene) SelectLocal(item.Key, "scene");
        else Select(item.Path);
    }
    public void Select(string path)
    {
        if (!File.Exists(path)) { App.Current.Notify("壁纸文件不存在"); return; }
        if (Path.GetFileName(path).Equals("project.json", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var project = WallpaperLibrary.ReadProject(Path.GetFullPath(path), Path.GetDirectoryName(path)!);
                if (project == null) App.Current.Notify("壁纸项目缺少有效主文件。");
                else Select(project);
            }
            catch (Exception ex) { App.Current.Notify("壁纸项目无法读取：" + ex.Message); }
            return;
        }
        string? kind = WallpaperItem.IsHtml(path) ? "web" : WallpaperLibrary.MediaKind(path);
        if (kind == null) { App.Current.Notify("请选择图片、视频、网页或 Wallpaper Engine 的 project.json。"); return; }
        if (kind == "image") try { _ = Thumbnail(path, 64); } catch (Exception ex) { App.Current.Notify("图片无法读取：" + ex.Message); return; }
        SelectLocal(path, kind);
    }

    void SelectLocal(string path, string kind) =>
        App.Current.Apply(p => { p.Wallpaper.Path = Path.GetFullPath(path); p.Wallpaper.Kind = kind; p.Wallpaper.Paused = false; });
    public void Sync()
    {
        if (_disposed) return;
        var p = App.Current.Prefs.Wallpaper;
        Library.Sync(p.LibrarySources);
        if (p.Kind == "engine")
        {
            if (!string.Equals(_requestedEngineProject, p.Path, StringComparison.OrdinalIgnoreCase))
            {
                ShowEnginePreview(p);
                return;
            }
            string key = p.Path + "|" + p.EnginePath;
            if (_kind != "engine" || _path != key)
            {
                Reset();
                _kind = "engine"; _path = key;
                OpenEngine(p.Path, p.EnginePath);
            }
            else if (_engine?.Attached == true) _engine.ApplyPlayback(p);
            return;
        }
        _requestedEngineProject = null;
        if (_kind == "engine") Reset();
        if (p.Kind == "none") { Reset(); Status = "使用当前系统壁纸"; Updated?.Invoke(); return; }
        try
        {
            if (!File.Exists(p.Path)) { App.Current.Notify("壁纸文件已移动或删除，继续显示系统壁纸"); _window?.Close(); _window = null; _path = _kind = ""; return; }
            if (_window == null || !_window.Attached || !Native.IsWindow(_window.DesktopHandle) || _path != p.Path || _kind != p.Kind)
            {
                if (_window == null || !_window.Attached || !Native.IsWindow(_window.DesktopHandle))
                {
                    _window?.Close();
                    _window = new WallpaperWindow(p);
                    _window.ContentChanged += ContentChanged;
                    _window.Show();
                }
                else _window.Replace(p);
                _path = p.Path;
                _kind = p.Kind;
            }
            _window.Apply(p);
            ContentChanged();
        }
        catch (Exception ex)
        {
            if (_window?.Attached != true) { _window?.Close(); _window = null; }
            Status = "壁纸未能显示：" + ex.Message;
            App.Current.ReportBackgroundError("本地壁纸", ex);
            App.Current.Notify(Status);
            Updated?.Invoke();
        }
    }
    void ShowEnginePreview(WallpaperPreference preference)
    {
        if (_kind == "engine-preview" && _path == preference.Path) return;
        Reset();
        _kind = "engine-preview";
        _path = preference.Path;
        Status = "已保留壁纸选择。可在壁纸库开启原引擎播放。";
        try
        {
            if (File.Exists(preference.Path))
            {
                var item = WallpaperLibrary.ReadProject(preference.Path, Path.GetDirectoryName(preference.Path)!);
                if (item?.HasPreview == true)
                {
                    var preview = new WallpaperPreference { Kind = "image", Path = item.Preview, Fit = preference.Fit };
                    _window = new WallpaperWindow(preview);
                    _window.Show();
                    Status = "当前显示项目预览图。可在壁纸库开启原引擎播放。";
                }
            }
        }
        catch (Exception ex)
        {
            _window?.Close();
            _window = null;
            App.Current.ReportBackgroundError("壁纸预览图", ex);
        }
        Updated?.Invoke();
    }
    void ContentChanged()
    {
        ContentRevision++;
        if (_window?.Web is { } web)
        {
            string label = _kind == "scene" ? "场景壁纸" : "网页壁纸";
            Status = web.Error.Length > 0 ? label + "未能显示：" + web.Error :
                web.Ready ? "澄屿正在渲染" + label : "正在加载" + label + "…";
            if (web.Error.Length > 0) App.Current.Notify(Status);
        }
        else if (_window?.ResourcesReleased == true)
            Status = "壁纸被遮挡 · 播放资源已释放";
        else if (_kind == "video")
        {
            Status = _window?.VideoError is { Length: > 0 } error ? "视频解码失败：" + error :
                _window?.VideoDecoded != true ? "正在加载视频壁纸…" :
                _window.VideoPlaying ? "澄屿正在播放视频壁纸" : "视频壁纸已暂停";
        }
        else Status = "澄屿正在显示图片壁纸";
        Updated?.Invoke();
    }
    public static BitmapSource? Thumbnail(string path, int width)
    {
        if (!File.Exists(path)) return null; var b = new BitmapImage(); b.BeginInit(); b.CacheOption = BitmapCacheOption.OnLoad; b.DecodePixelWidth = width; b.UriSource = new Uri(Path.GetFullPath(path)); b.EndInit(); b.Freeze(); return b;
    }
    async void OpenEngine(string path, string executable)
    {
        var cancellation = new CancellationTokenSource();
        _opening = cancellation;
        var engine = new EngineWallpaper();
        _engine = engine;
        Busy = true;
        Status = "正在连接 Wallpaper Engine…";
        Updated?.Invoke();
        try
        {
            await engine.Open(executable, path, cancellation.Token);
            if (cancellation.IsCancellationRequested) return;
            await engine.ApplyPlaybackAsync(App.Current.Prefs.Wallpaper, cancellation.Token);
            try
            {
                var item = WallpaperLibrary.ReadProject(path, Path.GetDirectoryName(path)!);
                _enginePreview.Source = item == null ? null : Thumbnail(item.Preview, (int)SystemParameters.PrimaryScreenWidth);
                _enginePreview.Measure(new Size(SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight));
                _enginePreview.Arrange(new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight));
            }
            catch { _enginePreview.Source = null; }
            Status = "Wallpaper Engine 正在渲染 · 播放与性能由原引擎管理";
        }
        catch (OperationCanceledException) { engine.Dispose(); }
        catch (Exception ex)
        {
            engine.Dispose();
            if (!cancellation.IsCancellationRequested)
            {
                Status = "壁纸未能打开：" + ex.Message;
                App.Current.Notify(Status);
                App.Current.ReportBackgroundError("Wallpaper Engine", ex);
                _path = "";
            }
        }
        finally
        {
            if (ReferenceEquals(_opening, cancellation))
            {
                _opening = null;
                Busy = false;
                Updated?.Invoke();
            }
            cancellation.Dispose();
        }
    }
    void Reset()
    {
        _opening?.Cancel();
        _opening = null;
        Busy = false;
        _engine?.Dispose(); _engine = null;
        _enginePreview.Source = null;
        _window?.Close(); _window = null;
        _path = _kind = "";
    }
    public void Dispose()
    {
        _disposed = true;
        App.Current.Changed -= Sync;
        Library.Changed -= LibraryChanged;
        Library.Dispose();
        Reset();
        Updated = null;
    }
}
internal sealed class WallpaperWindow : Window
{
    internal bool Attached { get; private set; }
    internal IntPtr DesktopHandle => new WindowInteropHelper(this).Handle;
    internal bool CoversPrimaryScreen
    {
        get
        {
            var handle = DesktopHandle;
            if (handle == IntPtr.Zero || !Native.IsWindowVisible(handle) || !GetWindowRect(handle, out var rect)) return false;
            var monitor = new Native.MonitorInfo { Size = Marshal.SizeOf<Native.MonitorInfo>() };
            return Native.GetMonitorInfo(Native.MonitorFromPoint(new Native.NativePoint(), 1), ref monitor) &&
                rect.Left <= monitor.Monitor.Left && rect.Top <= monitor.Monitor.Top &&
                rect.Right >= monitor.Monitor.Right && rect.Bottom >= monitor.Monitor.Bottom;
        }
    }
    internal WebWallpaper? Web { get; private set; }
    internal event Action? ContentChanged;
    internal Visual? BackdropVisual => Web == null ? Content as Visual : _webPreview;
    internal bool VideoDecoded => _video is { NaturalVideoWidth: > 0, NaturalDuration.HasTimeSpan: true };
    internal string VideoError { get; private set; } = "";
    internal bool HasDesktopCompositionStyle
    {
        get
        {
            var parent = GetParent(DesktopHandle);
            if (parent == IntPtr.Zero) return false;
            if (_compositionHost == IntPtr.Zero)
                return (DesktopLayer.GetStyle(parent, -20).ToInt64() & 0x00200000) == 0;
            return parent == _compositionHost && GetLayeredWindowAttributes(_compositionHost, out _, out byte alpha, out uint flags) &&
                alpha == byte.MaxValue && (flags & 2) != 0;
        }
    }
    internal bool VideoPlaying => _playing;
    internal bool ResourcesReleased => _resourcesReleased;
    internal int GlassSampleInterval => _coverage >= .35 ? 250 : 100;
    internal TimeSpan VideoPosition => _video?.Position ?? TimeSpan.Zero;
    internal double? VideoSpeed => _video?.SpeedRatio;
    internal double? VideoVolume => _video?.Volume;
    internal bool? VideoMuted => _video?.IsMuted;
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string? title);
    [DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr h, uint message, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr child);
    [DllImport("user32.dll", SetLastError = true)] static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint colorKey, byte alpha, uint flags);
    [DllImport("user32.dll")] static extern bool GetLayeredWindowAttributes(IntPtr hwnd, out uint colorKey, out byte alpha, out uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateWindowEx(int extendedStyle, string windowClass, string? title, int style, int x, int y,
        int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, IntPtr data);
    delegate bool EnumProc(IntPtr h, IntPtr data);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out Native.NativeRect r);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out Native.NativeRect r);
    [DllImport("user32.dll")] static extern bool ScreenToClient(IntPtr h, ref Native.NativePoint point);
    [DllImport("user32.dll")] static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, ref Guid setting, uint flags);
    [DllImport("user32.dll")] static extern bool UnregisterPowerSettingNotification(IntPtr handle);
    IntPtr _powerNotification;
    IntPtr _compositionHost;
    MediaElement? _video;
    Image? _image, _webPreview;
    readonly DispatcherTimer _watch = new() { Interval = TimeSpan.FromMilliseconds(750) };
    WallpaperPreference _preference;
    bool _locked, _asleep, _playing, _playbackApplied;
    double _coverage;
    bool _covered, _resourcesReleased;
    long? _coveredSince;
    TimeSpan _resumePosition;
    BitmapSource? _retainedFrame;
    public WallpaperWindow(WallpaperPreference p)
    {
        _preference = p; Title = "澄屿 · 壁纸"; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; ShowActivated = false; Background = Brushes.Black; IsHitTestVisible = false;
        Left = SystemParameters.WorkArea.Left; Top = 0; Width = SystemParameters.PrimaryScreenWidth; Height = SystemParameters.PrimaryScreenHeight;
        SourceInitialized += (_, _) => AttachDesktop();
        Loaded += (_, _) => StartContent();
        _watch.Tick += (_, _) => Playback();
        Replace(p);
        SystemEvents.SessionSwitch += Session;
        SystemEvents.PowerModeChanged += Power;
        Closed += (_, _) =>
        {
            Attached = false;
            ReleaseContent();
            if (_powerNotification != IntPtr.Zero) UnregisterPowerSettingNotification(_powerNotification);
            if (_compositionHost != IntPtr.Zero) DestroyWindow(_compositionHost);
            SystemEvents.SessionSwitch -= Session;
            SystemEvents.PowerModeChanged -= Power;
        };
    }

    // Keep Explorer's wallpaper child window in place when changing the source.
    internal void Replace(WallpaperPreference p)
    {
        MediaElement? video = null;
        Image? image = null, webPreview = null;
        WebWallpaper? web = null;
        if (p.Kind == "video")
        {
            video = new MediaElement
            {
                Source = new Uri(p.Path), LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Manual,
                Volume = 0, IsMuted = true, ScrubbingEnabled = true
            };
            video.MediaOpened += (_, _) =>
            {
                if (_video != video) return;
                if (_resumePosition > TimeSpan.Zero)
                {
                    if (!video.NaturalDuration.HasTimeSpan || _resumePosition < video.NaturalDuration.TimeSpan)
                        video.Position = _resumePosition;
                    _resumePosition = TimeSpan.Zero;
                }
                else if (!_playing) video.Position = TimeSpan.FromMilliseconds(1);
                _retainedFrame = null;
                ContentChanged?.Invoke();
            };
            video.MediaEnded += (_, _) =>
            {
                if (_video != video) return;
                video.Position = TimeSpan.Zero;
                if (_playing) video.Play();
            };
            video.MediaFailed += (_, e) =>
            {
                if (_video != video) return;
                VideoError = e.ErrorException.Message;
                App.Current.ReportBackgroundError("壁纸视频 " + Path.GetFileName(p.Path), e.ErrorException);
                App.Current.Notify("视频解码失败：" + VideoError);
                _video = null;
                video.Close();
                _playing = false;
                ContentChanged?.Invoke();
            };
        }
        else if (p.Kind is "web" or "scene")
        {
            bool scene = p.Kind == "scene";
            bool project = Path.GetFileName(p.Path).Equals("project.json", StringComparison.OrdinalIgnoreCase);
            var item = project ? WallpaperLibrary.ReadProject(p.Path, Path.GetDirectoryName(p.Path)!) : null;
            if (scene && item?.NativeScene != true)
                throw new InvalidDataException("场景壁纸缺少有效的项目文件。");
            if (!scene && (project && item?.NativeWeb != true || !project && !WallpaperItem.IsHtml(p.Path)))
                throw new InvalidDataException("网页壁纸缺少有效的 HTML 入口。");
            web = new WebWallpaper(item?.Path ?? p.Path, item?.Project ?? "", p, scene);
            web.Changed += () =>
            {
                if (Web != web) return;
                if (web.Error.Length > 0)
                {
                    web.View.Visibility = Visibility.Hidden;
                    if (_webPreview?.Source is { } preview)
                        Content = new Image { Source = preview, Stretch = Stretch.UniformToFill };
                }
                ContentChanged?.Invoke();
            };
            if (item?.HasPreview == true)
            {
                try
                {
                    webPreview = new Image { Source = WallpaperService.Thumbnail(item.Preview, (int)Width), Stretch = Stretch.UniformToFill };
                    webPreview.Measure(new Size(Width, Height));
                    webPreview.Arrange(new Rect(0, 0, Width, Height));
                }
                catch { }
            }
        }
        else image = new Image { Source = WallpaperService.Thumbnail(p.Path, (int)SystemParameters.PrimaryScreenWidth) };

        ReleaseContent();
        _preference = p;
        _resourcesReleased = false;
        _coveredSince = null;
        _resumePosition = TimeSpan.Zero;
        _retainedFrame = null;
        VideoError = "";
        _video = video;
        _image = image;
        Web = web;
        _webPreview = webPreview;
        if (web != null && webPreview != null)
        {
            var layers = new Grid();
            layers.Children.Add(webPreview);
            layers.Children.Add(web.View);
            Content = layers;
        }
        else Content = (FrameworkElement?)video ?? (FrameworkElement?)web?.View ?? image;
        if (IsLoaded) StartContent();
    }

    void StartContent()
    {
        Apply(_preference);
        if (_video != null || Web != null) _watch.Start();
        if (Web is { } web) _ = web.Open(App.Current.Store.Root);
    }

    void ReleaseContent()
    {
        _watch.Stop();
        _playing = false;
        _playbackApplied = false;
        var previousVideo = _video;
        _video = null;
        previousVideo?.Close();
        var previousWeb = Web;
        Web = null;
        Content = null;
        previousWeb?.Dispose();
        _image = _webPreview = null;
    }
    void AttachDesktop()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var hwnd = new WindowInteropHelper(this).Handle;
        _compositionHost = AttachToDesktop(hwnd, (int)Math.Round(Width * dpi.DpiScaleX),
            (int)Math.Round(Height * dpi.DpiScaleY), nativeRenderer: true);
        Attached = true;
        var displayState = new Guid("6FE69556-704A-47A0-8F24-C28D936FDA47"); _powerNotification = RegisterPowerSettingNotification(hwnd, ref displayState, 0);
        HwndSource.FromHwnd(hwnd).AddHook((IntPtr h, int message, IntPtr wp, IntPtr lp, ref bool handled) => { if (message == 0x218 && wp.ToInt64() == 0x8013 && lp != IntPtr.Zero && Marshal.ReadInt32(lp, 16) == 4) { _asleep = Marshal.ReadInt32(lp, 20) == 0; Playback(); } return IntPtr.Zero; });
    }
    internal static IntPtr AttachToDesktop(IntPtr hwnd, int width, int height, bool nativeRenderer = false)
    {
        var progman = FindWindow("Progman", null); if (progman == IntPtr.Zero) throw new InvalidOperationException("找不到 Windows 桌面");
        SendMessageTimeout(progman, 0x052C, IntPtr.Zero, IntPtr.Zero, 2, 1000, out _); IntPtr worker = IntPtr.Zero;
        EnumWindows((h, _) => { if (DesktopLayer.FindWindowEx(h, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero) { worker = DesktopLayer.FindWindowEx(IntPtr.Zero, h, "WorkerW", null); return false; } return true; }, IntPtr.Zero);
        bool modern = false;
        if (worker == IntPtr.Zero) { worker = DesktopLayer.FindWindowEx(progman, IntPtr.Zero, "WorkerW", null); modern = worker != IntPtr.Zero; }
        if (worker == IntPtr.Zero) throw new InvalidOperationException("桌面壁纸层未就绪");
        var parent = modern ? progman : worker;
        var origin = new Native.NativePoint { X = 0, Y = 0 }; ScreenToClient(parent, ref origin);
        IntPtr after = modern ? DesktopLayer.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null) : IntPtr.Zero;
        bool layered = nativeRenderer && modern && after != IntPtr.Zero &&
            (DesktopLayer.GetStyle(parent, -20).ToInt64() & 0x00200000) != 0;
        IntPtr host = IntPtr.Zero;
        if (layered)
        {
            // WPF clears WS_EX_LAYERED on opaque windows. Let a native parent own the layer.
            host = CreateWindowEx(0x080800A0, "Static", null, 0x42000000, origin.X, origin.Y,
                width, height, parent, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (host == IntPtr.Zero || !SetLayeredWindowAttributes(host, 0, byte.MaxValue, 2))
            {
                App.Current.ReportBackgroundError("壁纸窗口合成", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
                if (host != IntPtr.Zero) DestroyWindow(host);
                host = IntPtr.Zero;
            }
        }
        var target = host != IntPtr.Zero ? host : parent;
        long wallpaperStyle = DesktopLayer.GetStyle(hwnd, -20).ToInt64() | 0x080000A0;
        DesktopLayer.SetStyle(hwnd, -20, new IntPtr(wallpaperStyle));
        long style = DesktopLayer.GetStyle(hwnd, -16).ToInt64();
        DesktopLayer.SetStyle(hwnd, -16, new IntPtr((style & ~(0x80000000L | 0x00C00000L | 0x00040000L)) | 0x40000000L));
        SetParent(hwnd, target);
        try
        {
            if (GetParent(hwnd) != target) throw new InvalidOperationException("壁纸窗口未能附着到 Windows 桌面。");
            const uint place = 0x10 | 0x40 | 0x20;
            bool positioned = host != IntPtr.Zero
                ? DesktopLayer.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, width, height, place) &&
                  DesktopLayer.SetWindowPos(host, after, origin.X, origin.Y, width, height, place)
                : DesktopLayer.SetWindowPos(hwnd, after, origin.X, origin.Y, width, height, place);
            if (!positioned) throw new InvalidOperationException("壁纸窗口未能设置到桌面壁纸层。");
            return host;
        }
        catch
        {
            if (host != IntPtr.Zero)
            {
                if (GetParent(hwnd) == host) SetParent(hwnd, parent);
                DestroyWindow(host);
            }
            throw;
        }
    }
    public void Apply(WallpaperPreference p)
    {
        _preference = p;
        if (!_preference.AutoPause) { _coverage = 0; _covered = false; }
        var fit = p.Fit == "contain" ? Stretch.Uniform : Stretch.UniformToFill;
        if (_image != null) _image.Stretch = fit;
        if (_video != null)
        {
            _video.Stretch = fit;
            _video.SpeedRatio = p.PlaybackRate / 100.0;
            _video.IsMuted = !p.AudioEnabled;
            _video.Volume = p.Volume / 100.0;
        }
        Playback();
    }
    void ReleaseCoveredPlayback()
    {
        _resumePosition = VideoPosition;
        _retainedFrame = _webPreview?.Source as BitmapSource;
        try
        {
            if (_video is { } video && VideoDecoded && video.ActualWidth > 0 && video.ActualHeight > 0)
            {
                int width = 640, height = Math.Max(1, (int)(640 * video.ActualHeight / video.ActualWidth));
                var drawing = new DrawingVisual();
                using (var context = drawing.RenderOpen())
                    context.DrawRectangle(new VisualBrush(video), null, new Rect(0, 0, width, height));
                var frame = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                frame.Render(drawing);
                frame.Freeze();
                _retainedFrame = frame;
            }
        }
        catch (Exception ex) { App.Current.ReportBackgroundError("壁纸暂停帧", ex); }
        ReleaseContent();
        _resourcesReleased = true;
        Content = new Image { Source = _retainedFrame, Stretch = Stretch.Fill };
        _watch.Start();
        ContentChanged?.Invoke();
    }

    void Playback() => Playback(null);

    internal void ProbeCoverage(double coverage, long coveredFor)
    {
        Playback(coverage, coveredFor);
        _watch.Stop();
    }

    void Playback(double? coverage, long coveredFor = 0)
    {
        if (_video == null && Web == null && !_resourcesReleased) return;
        _coverage = _preference.AutoPause ? coverage ?? Occlusion.WallpaperCoverage() : 0;
        _covered = _preference.AutoPause && _coverage >= (_covered ? .9 : .95);
        bool blocked = _locked || _asleep || _covered;
        long now = Environment.TickCount64;
        _coveredSince = blocked ? coverage.HasValue ? now - coveredFor : _coveredSince ?? now : null;
        if (_resourcesReleased)
        {
            if (blocked && _preference.ReleaseWhenCovered) return;
            var position = _resumePosition;
            Replace(_preference);
            _resumePosition = position;
            ContentChanged?.Invoke();
            return;
        }
        bool play = !_preference.Paused && !blocked;
        int fps = _coverage >= .7 ? 12 : _coverage >= .35 ? 20 : 30;
        Web?.Apply(_preference, !play, fps, blocked);
        if (_preference.ReleaseWhenCovered && _coveredSince is long since && now - since >= 12000)
        {
            ReleaseCoveredPlayback();
            return;
        }
        if (_video == null || (_playbackApplied && play == _playing)) return;
        _playbackApplied = true;
        _playing = play;
        if (play) _video.Play(); else _video.Pause();
        ContentChanged?.Invoke();
    }
    void Session(object sender, SessionSwitchEventArgs e) { _locked = e.Reason == SessionSwitchReason.SessionLock || e.Reason == SessionSwitchReason.SessionLogoff; Dispatcher.BeginInvoke(Playback); }
    void Power(object sender, PowerModeChangedEventArgs e) { _asleep = e.Mode == PowerModes.Suspend; Dispatcher.BeginInvoke(Playback); }
}
