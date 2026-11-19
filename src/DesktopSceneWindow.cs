using System;
using System.Collections.Generic;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;

namespace ChengyuDock;
// Native, independent widget windows. No permanent browser or full-screen web compositor.
public sealed class DesktopSceneWindow : IDisposable
{
    internal readonly Dictionary<string, WidgetWindow> Windows = new();
    SnapGuides? _guides; bool _shown;
    public Task Ready => Task.CompletedTask;
    public int ComponentCount => Windows.Count;
    public int NativeGlassFrames => Windows.Values.Sum(w => (int)w.Glass.RenderedFrames);
    public DesktopSceneWindow() { App.Current.Changed += Sync; App.Current.Services.Changed += Publish; }
    public void Show() { _shown = true; Sync(); }
    public void Sync()
    {
        if (!_shown) return;
        var prefs = App.Current.Prefs;
        var ids = prefs.Widgets.Where(p => p.Key != "shelf" && p.Value.Enabled).Select(p => p.Key).Concat(prefs.Widgets["shelf"].Enabled ? prefs.ShelfBoxes.Where(b => b.Enabled).Select(b => "shelf:" + b.Id) : Array.Empty<string>()).ToHashSet();
        foreach (var id in Windows.Keys.Where(id => !ids.Contains(id)).ToArray()) { Windows[id].Close(); Windows.Remove(id); }
        foreach (var id in ids) { if (!Windows.TryGetValue(id, out var window)) { Windows[id] = window = new WidgetWindow(this, id); window.Show(); } window.Sync(); }
        Publish();
    }
    void Publish() { foreach (var window in Windows.Values.ToArray()) window.UpdateData(); }
    internal IEnumerable<Rect> Peers(WidgetWindow active) => Windows.Values.Where(w => w != active && w.IsVisible).Select(w => w.Bounds);
    internal void Guides(double? x, double? y, Rect work) { if (x == null && y == null) { _guides?.Fade(); return; } _guides ??= new SnapGuides(); _guides.ShowLines(x, y, work); }
    public void AddShelfDialog() => AddShelfDialog(App.Current.Prefs.ShelfBoxes.FirstOrDefault()?.Id);
    public void AddShelfDialog(string? id) { if (id == null) return; var d = new OpenFileDialog { Title = "添加收纳项目", Multiselect = true, Filter = "所有文件|*.*" }; if (d.ShowDialog() == true) AddShelf(d.FileNames, id); }
    public void AddShelf(IEnumerable<string> paths) => AddShelf(paths, App.Current.Prefs.ShelfBoxes.FirstOrDefault()?.Id);
    public void AddShelf(IEnumerable<string> paths, string? id) => App.Current.Apply(p => { var box = p.ShelfBoxes.FirstOrDefault(b => b.Id == id); if (box == null) return; foreach (var path in paths.Where(path => File.Exists(path) || Directory.Exists(path))) { if (box.Pins.Count >= 128) break; if (!box.Pins.Any(i => i.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) box.Pins.Add(new Pin { Path = path, Name = Directory.Exists(path) ? new DirectoryInfo(path).Name : Path.GetFileNameWithoutExtension(path) }); } });
    public void Dispose() { _shown = false; App.Current.Changed -= Sync; App.Current.Services.Changed -= Publish; foreach (var w in Windows.Values) w.Close(); Windows.Clear(); _guides?.Close(); }
}
internal sealed class WidgetWindow : Window
{
    readonly DesktopSceneWindow _scene;
    internal readonly string Id;
    internal readonly GlassSurface Glass = new();
    readonly Grid _body = new() { Background = Brushes.Transparent };
    readonly WidgetFace _face;
    readonly Canvas _actions = new();
    readonly SnapEngine _snap = new();
    DragGhostWindow? _shelfGhost;
    readonly DispatcherTimer _animation = new() { Interval = TimeSpan.FromMilliseconds(16) };
    Point _dragStart, _start; bool _pressed, _moved;
    internal readonly IslandMotion Island = new();
    readonly Stopwatch _clock = Stopwatch.StartNew(); double _lastAnimation;
    internal bool FocusRunning; internal TimeSpan FocusRemaining = TimeSpan.FromMinutes(25); internal DateTime FocusEnds;
    double _x, _y, _w, _h;
    string _actionSignature = "", _dataSignature = ""; internal int Page;
    double _baseW, _baseH;
    internal ShelfBoxPreference? Shelf => Id.StartsWith("shelf:") ? App.Current.Prefs.ShelfBoxes.FirstOrDefault(b => "shelf:" + b.Id == Id) : null;
    string Caption => Shelf?.Title ?? WidgetPreference.Names.GetValueOrDefault(Id, "收纳盒");
    internal Rect Bounds => new(Left + 34, Top + 34, _w, _h);
    internal WidgetPreference Preference => Shelf ?? App.Current.Prefs.Widgets[Id];
    internal JsonElement Data;
    internal IslandMedia PresentedMedia = IslandMedia.Empty;
    internal bool Expanded => Island.Expanded;
    public WidgetWindow(DesktopSceneWindow scene, string id)
    {
        _scene = scene; Id = id; (_baseW, _baseH) = id switch { "shelf" => (290, 38), "clock" => (630, 180), "weather" => (320, 152), "performance" => (340, 215), "todayTodo" => (320, 150), "music" => (320, 150), "calendar" => (320, 330), "almanac" => (320, 285), "spectrum" => (651, 180), _ => (120, 30) }; if (Shelf is { } shelf) { _baseW = shelf.Width; _baseH = shelf.Height; } if (id == "island") { _baseW = 120; _baseH = 30; } _w = _baseW; _h = _baseH;
        Title = "澄屿 · " + Caption; WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = App.Current.InteractionMode && Id == "island"; ShowActivated = false; Focusable = false;
        var root = new Grid { Margin = new Thickness(34) }; root.Children.Add(Glass); root.Children.Add(_body); _face = new WidgetFace(this) { IsHitTestVisible = false }; _body.Children.Add(_face); if (Id == "spectrum") _body.Children.Add(new SpectrumVisual { Margin = new Thickness(12) }); _body.Children.Add(_actions); Content = root;
        if (App.Current.InteractionMode && Id == "island") { var pixels = new byte[16 * 16 * 4]; for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = 38; pixels[i + 1] = 24; pixels[i + 2] = 18; pixels[i + 3] = 255; } Glass.InputProvider = () => { var p = Glass.PointToScreen(new Point()); return new GlassInput(pixels, 16, 16, (int)p.X, (int)p.Y, 1); }; }
        SourceInitialized += (_, _) => { if (!(App.Current.InteractionMode && Id == "island")) { DesktopLayer.Attach(this, priority: Id == "island"); GlassPump.AllowCapture(this); } var hwnd = new WindowInteropHelper(this).Handle; HwndSource.FromHwnd(hwnd).AddHook((IntPtr h, int m, IntPtr wp, IntPtr lp, ref bool handled) => { if (m == 0x84 && !_pressed) { var point = PointFromScreen(new Point(unchecked((short)(lp.ToInt64() & 0xffff)), unchecked((short)(lp.ToInt64() >> 16)))); if (point.X < 34 || point.Y < 34 || point.X > _w + 34 || point.Y > _h + 34) { handled = true; return new IntPtr(-1); } } return IntPtr.Zero; }); }; Loaded += (_, _) => { Sync(); if (!(App.Current.InteractionMode && Id == "island")) DesktopLayer.Bottom(this); };
        PreviewMouseLeftButtonDown += Down; PreviewMouseMove += Move; PreviewMouseLeftButtonUp += (_, e) => { if (_pressed) { FinishDrag(); e.Handled = true; } }; LostMouseCapture += (_, _) => { if (_pressed) FinishDrag(true); };
        MouseEnter += (_, _) => { if (Id == "island") { Island.Enter(_clock.Elapsed.TotalSeconds); WakeIsland(); } }; MouseLeave += (_, _) => { if (Id == "island" && !_pressed) { Island.Leave(_clock.Elapsed.TotalSeconds); WakeIsland(); } };
        MouseWheel += (_, e) => { if (Id == "island") { Island.Wheel(e.Delta < 0 ? 1 : -1, _clock.Elapsed.TotalSeconds); Page = Island.Page; _actionSignature = ""; WakeIsland(); UpdateData(true); e.Handled = true; } };
        MouseRightButtonUp += (_, e) => { Context(); e.Handled = true; };
        AllowDrop = Shelf != null; Drop += (_, e) => { if (e.Data.GetData(DataFormats.FileDrop) is string[] files) _scene.AddShelf(files, Shelf?.Id); };
        _animation.Tick += (_, _) => Animate(); App.Current.Spectrum.Changed += SpectrumChanged; App.Current.Media.Changed += SystemMediaChanged; Closed += (_, _) => { EndShelfDrag(); _animation.Stop(); App.Current.Spectrum.Changed -= SpectrumChanged; App.Current.Media.Changed -= SystemMediaChanged; };
    }
    void SystemMediaChanged() { if (Id == "island") UpdateData(true); }
    void SpectrumChanged() { if (Id == "spectrum") UpdateData(true); }
    Point DefaultPosition()
    {
        var r = SystemParameters.WorkArea;
        if (Shelf is { } shelf) { int i = App.Current.Prefs.ShelfBoxes.IndexOf(shelf); return new(r.Right - shelf.Width - 24 - i / 3 * 300, r.Top + 24 + i % 3 * 220); }
        return Id switch { "shelf" => new(r.Left + 20, r.Top + 2), "clock" => new(r.Left + (r.Width - _baseW) / 2, r.Top + 48), "weather" => new(r.Left + 24, r.Top + 140), "performance" => new(r.Right - 360, r.Top + 2), "todayTodo" => new(r.Right - 340, r.Top + 234), "music" => new(r.Right - 334, r.Top + 398), "calendar" => new(r.Left + 24, r.Top + 310), "almanac" => new(r.Left + 360, r.Top + 310), "spectrum" => new(r.Left + (r.Width - _baseW) / 2, r.Bottom - 125 - _baseH), _ => new(r.Left + (r.Width - _baseW) / 2, r.Top + 2) };
    }
    public void Sync()
    {
        var p = Preference; if (Shelf is { } shelf) { _w = _baseW = shelf.Width; _h = _baseH = shelf.Height; _actionSignature = ""; } var d = DefaultPosition(); var work = SystemParameters.WorkArea;
        if (Id == "clock")
        {
            var size = RainmeterClock.CanvasSize(App.Current.Prefs.ClockStyle);
            double scale = Math.Min(1, Math.Min(work.Width / size.Width, work.Height / size.Height));
            _w = _baseW = size.Width * scale;
            _h = _baseH = size.Height * scale;
            d = DefaultPosition();
        }
        _x = Math.Clamp(p.X ?? d.X, work.Left, Math.Max(work.Left, work.Right - _baseW)); _y = Math.Clamp(p.Y ?? d.Y, work.Top, Math.Max(work.Top, work.Bottom - _baseH));
        if (Id == "island") { _x = (SystemParameters.PrimaryScreenWidth - _baseW) / 2; _y = 0; }
        Glass.Material = p.Material; Glass.Refresh(); Geometry(); UpdateData(true);
    }
    void Geometry() { Width = _w + 68; Height = _h + 68; Left = _x - 34 - (Id == "island" ? (_w - _baseW) / 2 : 0); Top = (Id == "island" ? (SystemParameters.WorkArea.Top + 8) * (1 - Math.Clamp(Island.Hide.Value, 0, 1)) + (6 - _h) * Math.Clamp(Island.Hide.Value, 0, 1) : _y) - 34; Glass.Radius = Id == "island" ? Math.Min(_h / 2, _h / 2 + (30 - _h / 2) * Math.Clamp((_h - 34) / 122, 0, 1)) : 22; _body.Clip = new RectangleGeometry(new Rect(0, 0, _w, _h), Glass.Radius, Glass.Radius); }
    void WakeIsland() { if (!_animation.IsEnabled) { _lastAnimation = _clock.Elapsed.TotalSeconds; _animation.Start(); } }
    void Animate()
    {
        double time = _clock.Elapsed.TotalSeconds, dt = Math.Clamp(time - _lastAnimation, .001, .05); _lastAnimation = time;
        bool active = Island.Step(time, dt, App.Current.Prefs.ReducedMotion || !SystemParameters.ClientAreaAnimation);
        _w = Island.Width.Value; _h = Island.Height.Value; Geometry(); _face.InvalidateVisual();
        _actions.Opacity = Math.Clamp((Island.Expansion - .55) / .45, 0, 1) * Island.Content.Value; _actions.IsHitTestVisible = Island.Expanded && _actions.Opacity > .5;
        var transform = _body.RenderTransform as ScaleTransform; if (transform == null) { transform = new ScaleTransform(1, 1); _body.RenderTransform = transform; _body.RenderTransformOrigin = new Point(.5, .5); } transform.ScaleX = transform.ScaleY = 1 - Island.Press.Value * .025;
        BuildActions(); if (!active && !_pressed) _animation.Stop();
    }
    void Down(object sender, MouseButtonEventArgs e)
    {
        if (Preference.Locked && Id != "island" || Ui.Interactive(e.OriginalSource) || e.LeftButton != MouseButtonState.Pressed) return;
        if (Id == "island") { Island.Press.Target = 1; WakeIsland(); }
        Native.GetCursorPos(out var p); var dpi = VisualTreeHelper.GetDpi(this); _dragStart = new(p.X / dpi.DpiScaleX, p.Y / dpi.DpiScaleY); _start = new(_x, _y); _pressed = true; _moved = false; _snap.Reset(); _body.CaptureMouse(); e.Handled = true;
    }
    void Move(object sender, MouseEventArgs e)
    {
        if (!_pressed || Preference.Locked || Id == "island") return; Native.GetCursorPos(out var c); var dpi = VisualTreeHelper.GetDpi(this); var delta = new Point(c.X / dpi.DpiScaleX, c.Y / dpi.DpiScaleY) - _dragStart;
        if (Math.Abs(delta.X) + Math.Abs(delta.Y) < 3 && !_moved) return; _moved = true;
        var raw = new Rect(_start.X + delta.X, _start.Y + delta.Y, _baseW, _baseH); var work = Native.MonitorWork(this, true);
        var result = App.Current.Prefs.SnapEnabled ? _snap.Move(raw, _scene.Peers(this), work) : (new Point(raw.X, raw.Y), (double?)null, (double?)null);
        _x = Math.Clamp(result.Item1.X, work.Left, Math.Max(work.Left, work.Right - _baseW)); _y = Math.Clamp(result.Item1.Y, work.Top, Math.Max(work.Top, work.Bottom - _baseH)); Geometry(); _scene.Guides(result.Item2, result.Item3, work); e.Handled = true;
    }
    void FinishDrag(bool canceled = false)
    {
        _pressed = false; _body.ReleaseMouseCapture(); _scene.Guides(null, null, SystemParameters.WorkArea);
        if (_moved && !Preference.Locked) { double x = _x, y = _y; App.Current.Apply(p => { var item = Shelf == null ? p.Widgets[Id] : p.ShelfBoxes.First(b => "shelf:" + b.Id == Id); item.X = x; item.Y = y; }); }
        if (Id == "island") { Island.Press.Target = 0; if (!_moved && !canceled) Island.Click(_clock.Elapsed.TotalSeconds); if (!IsMouseOver) Island.Leave(_clock.Elapsed.TotalSeconds); WakeIsland(); } DesktopLayer.Bottom(this);
    }
    void Context()
    {
        var menu = AppMenu.Create(Caption);
        if (Id == "spectrum")
        {
            AppMenu.Action(menu, "实时频谱", () => App.Current.Apply(p => p.Spectrum.LiveEnabled = !p.Spectrum.LiveEnabled),
                "♫", selected: App.Current.Prefs.Spectrum.LiveEnabled);
            foreach (var (label, source) in new[] { ("系统声音", "system"), ("麦克风", "microphone") })
                AppMenu.Action(menu, label, () => App.Current.Apply(p => p.Spectrum.Source = source),
                    selected: App.Current.Prefs.Spectrum.Source == source);
            foreach (var pair in WidgetStyles.Spectrum.Select(x => (x.Name, x.Key))) AppMenu.Action(menu, pair.Item1, () => App.Current.Apply(p => p.Spectrum.Style = pair.Item2), selected: App.Current.Prefs.Spectrum.Style == pair.Item2);
            AppMenu.Action(menu, "白色配色", () => App.Current.Apply(p => p.Spectrum.Color = "white"), selected: App.Current.Prefs.Spectrum.Color == "white");
            AppMenu.Action(menu, "彩虹配色", () => App.Current.Apply(p => p.Spectrum.Color = "rainbow"), selected: App.Current.Prefs.Spectrum.Color == "rainbow");
            AppMenu.Action(menu, "静音时隐藏频谱", () => App.Current.Apply(p => p.Spectrum.HideWhenSilent = !p.Spectrum.HideWhenSilent), selected: App.Current.Prefs.Spectrum.HideWhenSilent);
            AppMenu.Divider(menu);
        }
        if (Id == "todayTodo") { AppMenu.Action(menu, "添加待办", () => App.Current.AddTodo(), "＋"); AppMenu.Divider(menu); }
        if (Id == "performance") { AppMenu.Action(menu, "选择显卡", () => AppMenu.Manager("performance")); AppMenu.Divider(menu); }
        if (Id == "clock") { foreach (var style in WidgetStyles.Clock) AppMenu.Action(menu, style.Name, () => App.Current.Apply(p => p.ClockStyle = style.Key), selected: App.Current.Prefs.ClockStyle == style.Key); AppMenu.Divider(menu); }
        if (Shelf is { } box) { AppMenu.Action(menu, "添加文件", () => _scene.AddShelfDialog(box.Id), "＋"); AppMenu.Action(menu, "显示标题", () => App.Current.Apply(p => p.ShelfBoxes.First(b => b.Id == box.Id).ShowTitle = !box.ShowTitle), selected: box.ShowTitle); AppMenu.Action(menu, "显示横向分隔线", () => App.Current.Apply(p => p.ShelfBoxes.First(b => b.Id == box.Id).ShowDivider = !box.ShowDivider), selected: box.ShowDivider); AppMenu.Action(menu, "设置收纳盒", () => AppMenu.Manager("storage"), "⚙"); AppMenu.Divider(menu); }
        void Change(Action<WidgetPreference> change) => App.Current.Apply(p => change(Shelf == null ? p.Widgets[Id] : p.ShelfBoxes.First(b => "shelf:" + b.Id == Id)));
        AppMenu.Materials(menu, Preference.Material, value => Change(p => p.Material = value), Id == "island");
        AppMenu.Divider(menu);
        if (Id != "island") AppMenu.Action(menu, "锁定位置", () => Change(p => p.Locked = !p.Locked), "◇", Preference.Locked, "保持桌面底层");
        AppMenu.Action(menu, "隐藏此组件", () => Change(p => p.Enabled = false), "−");
        AppMenu.Action(menu, "打开组件设置", () => AppMenu.Manager(Shelf != null ? "storage" : Id == "spectrum" ? "spectrum" : Id == "island" ? "island" : "components"), "⚙");
        if (Id == "island") { Island.MenuOpen = true; menu.Closed += (_, _) => { Island.MenuOpen = false; if (!IsMouseOver) Island.Leave(_clock.Elapsed.TotalSeconds); WakeIsland(); }; }
        AppMenu.Show(menu, _body);
    }

    public void UpdateData(bool force = false)
    {
        Data = App.Current.Services.NativeModel;
        if (Id == "island") {
            var local = Data.GetProperty("music"); var external = App.Current.Media.Current;
            var localTitle = local.GetProperty("title").GetString() ?? "";
            bool localPlaying = local.GetProperty("playing").GetBoolean();
            if (localPlaying || (string.IsNullOrEmpty(external.Title) && !string.IsNullOrEmpty(localTitle)))
                PresentedMedia = new IslandMedia(localTitle, "本地音乐", localPlaying, false, local.GetProperty("position").GetDouble(), local.GetProperty("duration").GetDouble());
            else
                PresentedMedia = external;
            Island.Media(!string.IsNullOrEmpty(PresentedMedia.Title), PresentedMedia.Playing, PresentedMedia.Title + PresentedMedia.Artist, _clock.Elapsed.TotalSeconds); WakeIsland();
            if (FocusRunning) FocusRemaining = FocusEnds - DateTime.Now; if (FocusRemaining < TimeSpan.Zero) { FocusRemaining = TimeSpan.Zero; FocusRunning = false; }
        }

        string sig = Id == "clock" && RainmeterClock.IsStyle(App.Current.Prefs.ClockStyle)
            ? DateTime.Now.ToString("yyyyMMddHHmm")
            : Id is "clock" or "calendar" or "almanac" or "island" ? DateTime.Now.ToString("yyyyMMddHHmmss") + Page : Id == "performance" ? Data.GetProperty("performance").GetRawText() + App.Current.Services.SelectedGpu : Id == "weather" ? Data.GetProperty("weather").GetRawText() : Id == "music" ? Data.GetProperty("music").GetRawText() : JsonSerializer.Serialize(Shelf != null ? (object)Shelf : App.Current.Prefs.Todos);
        if (force || sig != _dataSignature) { _dataSignature = sig; _face.InvalidateVisual(); } BuildActions();
    }
    void BuildActions()
    {
        var shelf = Shelf;
        string sig = shelf != null
            ? JsonSerializer.Serialize(shelf)
            : Id + Page + Island.Expanded + PresentedMedia.Playing + App.Current.Spectrum.Status + (Data.ValueKind == JsonValueKind.Object ? Data.GetProperty("music").GetProperty("playing").ToString() : "") + App.Current.Prefs.Spectrum.Style + App.Current.Prefs.Spectrum.Source + (_h > 100) + string.Join("|", App.Current.Prefs.Todos.Select(t => t.Id + t.Done));
        if (sig == _actionSignature) return; _actionSignature = sig; _actions.Children.Clear();
        void Add(string text, double x, double y, double width, Action action) { var b = Ui.Button(text, action); b.Width = width; b.Height = 30; b.Padding = new Thickness(2); b.Margin = new Thickness(0); b.Background = Brushes.Transparent; b.BorderThickness = new Thickness(0); Canvas.SetLeft(b, x); Canvas.SetTop(b, y); _actions.Children.Add(b); }
        if (Id == "music" || Id == "island" && Island.Expanded && Page == 0) { double y = Id == "music" ? 108 : 98; Add("⏮", 90, y, 35, () => { if (Id == "island") MediaCommand(-1); else App.Current.Services.Next(-1); }); Add((Id == "island" ? PresentedMedia.Playing : Data.GetProperty("music").GetProperty("playing").GetBoolean()) ? "Ⅱ" : "▶", 137, y, 48, () => { if (Id == "island") MediaCommand(0); else App.Current.Services.ToggleMusic(); }); Add("⏭", 198, y, 35, () => { if (Id == "island") MediaCommand(1); else App.Current.Services.Next(1); }); }

        if (Id == "island" && Island.Expanded)
        {
            double width = Page == 0 ? 372 : 404, height = Page == 0 ? 156 : 208;
            for (int i = 0; i < 6; i++) { int page = i; Add(i == Page ? "●" : "·", width / 2 - 54 + i * 18, height - 27, 16, () => { Island.SetPage(page, page > Page ? 1 : -1); Page = page; _actionSignature = ""; WakeIsland(); }); }
            if (Page == 0) Add("＋", width - 40, 8, 28, () => App.Current.Services.OpenMusic());
            if (Page == 2) Add("添加待办", width - 112, 143, 90, () => App.Current.AddTodo());
            if (Page == 5) { Add(FocusRunning ? "暂停" : "开始专注", 105, 134, 95, () => { if (FocusRunning) FocusRemaining = FocusEnds - DateTime.Now; else FocusEnds = DateTime.Now + (FocusRemaining > TimeSpan.Zero ? FocusRemaining : TimeSpan.FromMinutes(25)); FocusRunning = !FocusRunning; _actionSignature = ""; UpdateData(true); }); Add("重置", 215, 134, 75, () => { FocusRunning = false; FocusRemaining = TimeSpan.FromMinutes(25); _actionSignature = ""; UpdateData(true); }); }
        }
        if (Id == "todayTodo") { int i = 0; foreach (var t in App.Current.Prefs.Todos.Where(t => t.Date == DateTime.Today.ToString("yyyy-MM-dd")).Take(3)) { var c = new CheckBox { Content = t.Text, IsChecked = t.Done, Width = 275, Foreground = Brushes.White, FontSize = 12 }; c.Click += (_, _) => App.Current.Apply(p => p.Todos.First(x => x.Id == t.Id).Done = c.IsChecked == true); Canvas.SetLeft(c, 20); Canvas.SetTop(c, 55 + i++ * 25); _actions.Children.Add(c); } Add("＋", 270, 7, 30, () => App.Current.AddTodo()); }
        if (shelf != null) BuildShelf(shelf);

    }
    void MediaCommand(int command) { if (PresentedMedia.External) App.Current.Media.Command(command); else if (command == 0) App.Current.Services.ToggleMusic(); else App.Current.Services.Next(command); }
    internal static double ShelfHeaderHeight(ShelfBoxPreference box) => box.ShowTitle ? box.ShowDivider ? 44 : 36 : box.ShowDivider ? 27 : 12;
    void BuildShelf(ShelfBoxPreference box)
    {
        double cell = box.IconSize + 16;
        var icons = new WrapPanel { ItemWidth = cell, ItemHeight = cell };
        var scroll = new ScrollViewer { Content = icons, Width = Math.Max(50, _w - 24), Height = Math.Max(40, _h - (ShelfHeaderHeight(box) + 12)), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, PanningMode = PanningMode.VerticalOnly, CanContentScroll = false };
        Canvas.SetLeft(scroll, 12); Canvas.SetTop(scroll, ShelfHeaderHeight(box)); _actions.Children.Add(scroll);
        foreach (var pin in box.Pins)
        {
            var button = Ui.Button("", () => App.Current.Launch(pin)); button.Content = new Image { Source = Icons.Get(pin.Path, pin.Name), Width = box.IconSize, Height = box.IconSize }; button.ToolTip = pin.Name; button.Width = box.IconSize + 14; button.Height = box.IconSize + 14; button.Padding = new Thickness(6); button.Margin = new Thickness(1); button.Background = Brushes.Transparent; button.BorderThickness = new Thickness(0);
            void Remove() => App.Current.Apply(p => p.ShelfBoxes.Find(b => b.Id == box.Id)?.Pins.RemoveAll(x => x.Id == pin.Id));
            button.RenderTransform = new TranslateTransform();
            button.PreviewMouseRightButtonUp += (_, e) =>
            {
                var menu = AppMenu.Create(pin.Name);
                AppMenu.Action(menu, "打开", () => App.Current.Launch(pin));
                AppMenu.Action(menu, "从收纳盒移除", Remove);
                AppMenu.Show(menu, button);
                e.Handled = true;
            };
            icons.Children.Add(button);
        }
        EnableShelfDrag(box, scroll, icons);
    }

    void EnableShelfDrag(ShelfBoxPreference box, ScrollViewer scroll, WrapPanel icons)
    {
        var buttons = icons.Children.Cast<Button>().ToArray();
        if (buttons.Length == 0) return;

        Point start = default;
        int source = -1, target = -1;
        bool dragging = false;
        double cell = icons.ItemWidth;
        var edgeScroll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
        bool InsideBox(Point point) => new Rect(0, 0, _w, _h).Contains(point);
        bool InsideGrid(Point point) => new Rect(0, 0, scroll.ViewportWidth, scroll.ViewportHeight).Contains(point);

        static void Slide(TranslateTransform transform, DependencyProperty axis, double value, bool animate)
        {
            if ((double)transform.GetAnimationBaseValue(axis) == value) return;
            double from = (double)transform.GetValue(axis);
            transform.SetValue(axis, value);
            transform.BeginAnimation(axis, animate ? new DoubleAnimation(from, value, TimeSpan.FromMilliseconds(130))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            } : null);
        }

        void ShowOrder(int destination)
        {
            if (destination == target) return;
            target = destination;
            bool animate = !App.Current.Prefs.ReducedMotion && SystemParameters.ClientAreaAnimation;
            int columns = Math.Max(1, (int)(icons.ActualWidth / cell));
            for (int i = 0; i < buttons.Length; i++)
            {
                int slot = i;
                if (i == source) slot = target;
                else if (source < i && i <= target) slot--;
                else if (target <= i && i < source) slot++;

                var transform = (TranslateTransform)buttons[i].RenderTransform;
                Slide(transform, TranslateTransform.XProperty, (slot % columns - i % columns) * cell, animate);
                Slide(transform, TranslateTransform.YProperty, (slot / columns - i / columns) * cell, animate);
            }
        }

        int Slot(Point point)
        {
            int columns = Math.Max(1, (int)(icons.ActualWidth / cell));
            int column = Math.Clamp((int)(point.X / cell), 0, columns - 1);
            int row = Math.Max(0, (int)(point.Y / cell));
            return Math.Min(buttons.Length - 1, row * columns + column);
        }

        void UpdateSlot()
        {
            if (InsideGrid(Mouse.GetPosition(scroll))) ShowOrder(Slot(Mouse.GetPosition(icons)));
        }

        edgeScroll.Tick += (_, _) =>
        {
            var pointer = Mouse.GetPosition(scroll);
            if (!dragging || !InsideGrid(pointer))
            {
                edgeScroll.Stop();
                return;
            }
            double edge = Math.Min(24, scroll.ViewportHeight / 3);
            double offset = scroll.VerticalOffset;
            if (pointer.Y < edge) offset -= 10;
            else if (pointer.Y > scroll.ViewportHeight - edge) offset += 10;
            scroll.ScrollToVerticalOffset(Math.Clamp(offset, 0, scroll.ScrollableHeight));
        };
        scroll.ScrollChanged += (_, _) => { if (dragging) UpdateSlot(); };

        for (int i = 0; i < buttons.Length; i++)
        {
            int index = i;
            buttons[i].PreviewMouseLeftButtonDown += (_, e) =>
            {
                source = index;
                target = index;
                start = e.GetPosition(_body);
            };
        }

        icons.PreviewMouseMove += (_, e) =>
        {
            if (source < 0 || !buttons[source].IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed) return;
            var position = e.GetPosition(_body);
            var delta = position - start;
            if (!dragging)
            {
                if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
                    Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                dragging = true;
                buttons[source].Opacity = 0;
                ToolTipService.SetIsEnabled(buttons[source], false);
            }

            bool removing = !InsideBox(position);
            if (removing) edgeScroll.Stop();
            else
            {
                UpdateSlot();
                if (scroll.ScrollableHeight > 0 && InsideGrid(Mouse.GetPosition(scroll))) edgeScroll.Start();
            }
            var pin = box.Pins[source];
            _shelfGhost ??= new DragGhostWindow();
            _shelfGhost.Update(pin.Path, pin.Name, removing ? "松手移除项目" : null, removing, this);
            e.Handled = true;
        };

        void ResetDrag()
        {
            edgeScroll.Stop();
            if (dragging)
            {
                ShowOrder(source);
                buttons[source].Opacity = 1;
                ToolTipService.SetIsEnabled(buttons[source], true);
                EndShelfDrag();
            }
            dragging = false;
            source = target = -1;
        }

        icons.AddHandler(PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler((_, e) =>
        {
            if (!dragging) return;
            bool removing = !InsideBox(e.GetPosition(_body));
            if (!removing) UpdateSlot();
            int destination = target;
            int origin = source;
            string id = box.Pins[origin].Id;
            var button = buttons[origin];
            e.Handled = true;
            ResetDrag();
            button.ReleaseMouseCapture();
            if (!removing && destination == origin) return;

            double offset = scroll.VerticalOffset;
            if (App.Current.Apply(p =>
            {
                var pins = p.ShelfBoxes.Find(b => b.Id == box.Id)?.Pins;
                var pin = pins?.Find(item => item.Id == id);
                if (pins == null || pin == null) return;
                pins.Remove(pin);
                if (!removing) pins.Insert(Math.Min(destination, pins.Count), pin);
            }))
            {
                var updated = _actions.Children.OfType<ScrollViewer>().FirstOrDefault();
                updated?.ScrollToVerticalOffset(offset);
            }
        }));
        icons.LostMouseCapture += (_, e) =>
        {
            if (source >= 0 && ReferenceEquals(e.OriginalSource, buttons[source])) ResetDrag();
        };
        icons.Unloaded += (_, _) => ResetDrag();
    }

    void EndShelfDrag()
    {
        _shelfGhost?.Close();
        _shelfGhost = null;
    }
}
internal sealed class WidgetFace : FrameworkElement
{
    readonly WidgetWindow _owner;
    public WidgetFace(WidgetWindow window) => _owner = window;
    internal static void DrawShelfDivider(DrawingContext d, double width, double y)
    {
        var fade = new LinearGradientBrush(new GradientStopCollection { new(Colors.Transparent, 0), new(Color.FromArgb(15, 255, 255, 255), .08), new(Color.FromArgb(51, 255, 255, 255), .24), new(Color.FromArgb(51, 255, 255, 255), .76), new(Color.FromArgb(15, 255, 255, 255), .92), new(Colors.Transparent, 1) }, 0); fade.Freeze();
        d.DrawRoundedRectangle(fade, null, new Rect(12, y, Math.Max(1, width - 24), .8), .4, .4);
    }
    protected override void OnRender(DrawingContext d)
    {
        var data = _owner.Data; if (data.ValueKind != JsonValueKind.Object) return; double w = ActualWidth;
        void T(string text, double x, double y, double size = 13, string color = "#F2F4F9", bool bold = false, double max = 0) { var f = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(new FontFamily("Segoe UI, Microsoft YaHei UI"), FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal), size, Ui.Brush(color), VisualTreeHelper.GetDpi(this).PixelsPerDip); if (max > 0) { f.MaxTextWidth = max; f.MaxLineCount = 1; f.Trimming = TextTrimming.CharacterEllipsis; } d.DrawText(f, new Point(x, y)); }
        void L(double x, double y, double width, string color = "#30FFFFFF", double thick = 1) => d.DrawLine(new Pen(Ui.Brush(color), thick), new Point(x, y), new Point(x + width, y));
        string G(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind != JsonValueKind.Null ? v.ToString() : "--";
        string Temp(JsonElement e, string k) => double.TryParse(G(e, k), out double n) ? Math.Round(n) + "°" : "--°";
        var weather = data.GetProperty("weather"); var music = data.GetProperty("music"); var prefs = App.Current.Prefs;
        if (_owner.Shelf is { } box) { if (box.ShowDivider) DrawShelfDivider(d, w, box.ShowTitle ? 33 : 15); if (box.ShowTitle) T(box.Title, 16, 12, 12, bold: true, max: w - 32); if (box.Pins.Count == 0) T("拖入文件至此", 20, WidgetWindow.ShelfHeaderHeight(box) + 18, 12, "#96A7BD", max: w - 40); return; }
        if (_owner.Id == "island") { IslandPainter.Draw(d, this, _owner); return; }
        switch (_owner.Id)
        {
            case "spectrum": break;
            case "clock":
                if (RainmeterClock.IsStyle(prefs.ClockStyle)) RainmeterClock.Draw(d, this, prefs);
                else ClockPainter.Draw(d, this, prefs.ClockStyle);
                break;
            case "weather":
                T(G(weather, "Icon"), 20, 21, 27); T(Temp(weather, "Temperature"), 64, 14, 37); T("H:" + Temp(weather, "High") + "  L:" + Temp(weather, "Low"), 135, 27, 10); T(G(weather, "City"), w - 112, 22, 12, max: 100); T(G(weather, "Condition"), w - 112, 43, 11, "#B9C0CA"); L(20, 72, w - 40);
                int n = 0; foreach (var hour in weather.GetProperty("Hours").EnumerateArray()) { double x = 25 + n++ * 57; T(G(hour, "Time"), x, 82, 10, "#BFC5CF"); T(G(hour, "Icon"), x, 99, 16); T(Temp(hour, "Temperature"), x, 124, 11); } if (n == 0) T(G(weather, "Error"), 22, 95, 12, "#ADB7C7"); break;
            case "performance":
                var p = data.GetProperty("performance"); T("性能", 20, 20, 15, bold: true); T("CPU", 20, 67, 12, "#AFBBCB"); T(G(p, "cpu") + "%", 100, 60, 21, bold: true); T("GPU", 180, 67, 12, "#AFBBCB"); var gpu = App.Current.Services.SelectedGpu; T(gpu?.Usage is double usage ? $"{usage:0}%" : "—", 252, 60, 21, bold: true); T("处理器使用率", 20, 92, 11, "#8D9AAC");
                T(gpu?.DedicatedBytes is long bytes ? $"显存 {bytes / 1073741824.0:0.0} GB" : "显存 —", 180, 92, 11, "#8D9AAC");
                T("内存", 20, 132, 12, "#AFBBCB"); T(G(p, "memory") + "%", 100, 123, 21, bold: true); L(20, 160, 135, "#384656", 3); if (double.TryParse(G(p, "memory"), out double percent)) L(20, 160, 135 * percent / 100, "#29B5B8", 3); T(G(p, "memoryDetail"), 20, 175, 11, "#9DA9B8"); T("网络", 180, 125, 12, "#AFBBCB"); T("↑ " + G(p, "upload"), 180, 151, 11, "#55DBB6"); T("↓ " + G(p, "download"), 180, 177, 11, "#55DBB6"); break;
            case "todayTodo":
                T("今日待办", 20, 17, 16, bold: true); var list = prefs.Todos.Where(t => t.Date == DateTime.Today.ToString("yyyy-MM-dd")).ToArray(); T(list.Count(t => t.Done) + "/" + list.Length, w - 84, 20, 11, "#A3AEBF"); if (list.Length == 0) { T("今天没有待办", 102, 69, 14); T("点右上角 + 添加", 82, 96, 12, "#959DAA"); } break;
            case "music":
                d.DrawRoundedRectangle(Ui.Brush("#25FFFFFF"), null, new Rect(20, 17, 50, 50), 11, 11); T("♫", 35, 23, 28, "#B8C0CA"); T(string.IsNullOrEmpty(G(music, "title")) ? "暂无播放" : G(music, "title"), 85, 33, 14, bold: true, max: 210); L(22, 91, w - 44, "#3CFFFFFF", 3); double dur = music.GetProperty("duration").GetDouble(); if (dur > 0) L(22, 91, (w - 44) * Math.Clamp(music.GetProperty("position").GetDouble() / dur, 0, 1), "#A4C8F4", 3); break;
            case "calendar":
                T(DateTime.Today.ToString("yyyy 年 M 月"), 20, 20, 20, bold: true); string[] days = { "日", "一", "二", "三", "四", "五", "六" }; for (int i = 0; i < 7; i++) T(days[i], 23 + i * 41, 65, 12, "#A8B7CA"); var first = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1); for (int i = 1; i <= DateTime.DaysInMonth(first.Year, first.Month); i++) { int cell = i - 1 + (int)first.DayOfWeek; double x = 23 + cell % 7 * 41, y = 103 + cell / 7 * 36; if (i == DateTime.Today.Day) d.DrawRoundedRectangle(Ui.Brush("#387DF0"), null, new Rect(x - 8, y - 3, 32, 30), 9, 9); T(i.ToString(), x, y, 13); } break;
            case "almanac":
                T("日历 · 今日", 20, 22, 16, bold: true); T(DateTime.Today.Day.ToString(), 20, 58, 64); T(DateTime.Today.ToString("yyyy 年 M 月 d 日  dddd", CultureInfo.GetCultureInfo("zh-CN")), 22, 146, 14); var lunar = new ChineseLunisolarCalendar(); int month = lunar.GetMonth(DateTime.Today), leap = lunar.GetLeapMonth(lunar.GetYear(DateTime.Today)); T("农历 " + (leap > 0 && month == leap ? "闰" : "") + (leap > 0 && month >= leap ? month - 1 : month) + " 月 " + lunar.GetDayOfMonth(DateTime.Today) + " 日", 22, 179, 14, "#B6C4D6"); T("暂无宜忌数据", 22, 226, 12, "#8998AE"); break;
            case "island":
                d.DrawEllipse(Ui.Brush("#34ED82"), null, new Point(18, 19), 3.5, 3.5); T("澄屿 · " + (_owner.Page == 0 ? DateTime.Now.ToString("HH:mm") : _owner.Page == 1 ? G(weather, "City") + " " + Temp(weather, "Temperature") : string.IsNullOrEmpty(G(music, "title")) ? "本地音乐" : G(music, "title")), 32, 10, 12, bold: true, max: w - 72);
                if (_owner.Expanded) { if (_owner.Page == 0) { T(DateTime.Now.ToString("HH:mm:ss"), 30, 49, 32); T(DateTime.Today.ToString("M 月 d 日 · dddd", CultureInfo.GetCultureInfo("zh-CN")), 210, 66, 12, "#ACBACB"); } else if (_owner.Page == 1) { T(G(weather, "Icon") + "  " + Temp(weather, "Temperature"), 28, 51, 32); T(G(weather, "Condition") + "   H:" + Temp(weather, "High") + " L:" + Temp(weather, "Low"), 178, 66, 12, "#ACBACB"); } else T(string.IsNullOrEmpty(G(music, "title")) ? "选择本地音乐开始播放" : G(music, "title"), 28, 58, 14, max: w - 56); } break;
        }
    }
}
internal sealed class SnapGuides
{
    readonly Window _vertical = Line(), _horizontal = Line(); readonly DispatcherTimer _fade = new() { Interval = TimeSpan.FromMilliseconds(260) };
    static Window Line() { var window = new Window { Width = 2, Height = 2, WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Ui.Brush("#E60977E6"), ShowActivated = false, ShowInTaskbar = false, IsHitTestVisible = false, ResizeMode = ResizeMode.NoResize }; window.SourceInitialized += (_, _) => { DesktopLayer.Attach(window); var h = new WindowInteropHelper(window).Handle; DesktopLayer.SetStyle(h, -20, new IntPtr(DesktopLayer.GetStyle(h, -20).ToInt64() | 0x20)); }; return window; }
    public SnapGuides() { _fade.Tick += (_, _) => { _vertical.Hide(); _horizontal.Hide(); _fade.Stop(); }; }
    public void ShowLines(double? x, double? y, Rect work) { _fade.Stop(); if (x is double px) { _vertical.Left = px; _vertical.Top = work.Top; _vertical.Height = work.Height; _vertical.Show(); } else _vertical.Hide(); if (y is double py) { _horizontal.Left = work.Left; _horizontal.Top = py; _horizontal.Width = work.Width; _horizontal.Show(); } else _horizontal.Hide(); }
    public void Fade() => _fade.Start();
    public void Close() { _fade.Stop(); _vertical.Close(); _horizontal.Close(); }
}
