using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;

namespace ChengyuDock;

public sealed class DockEntry
{
    public string Key = "", Name = "", Path = "";
    public Pin? Pin;
    public IntPtr Window;
    public bool Running;
    public bool SectionBreakBefore;
    public WindowEntry[] Windows = Array.Empty<WindowEntry>();
}
public sealed class DockView : Canvas
{
    sealed class Tile
    {
        public DockEntry Entry = null!; public Grid Visual = null!; public Image Reflection = null!; public Ellipse Dot = null!;
        public double X, XVelocity, Scale = 1, ScaleVelocity; public Rect Bounds;
    }
    readonly List<Tile> _tiles = new();
    readonly List<string> _runningOrder = new();
    readonly GlassSurface _bar; readonly Border _label; readonly TextBlock _labelText;
    readonly Rectangle _separator;
    readonly DispatcherTimer _hoverTimer;
    Preferences _settings = null!;
    Point _mouse = new(-9999, -9999), _pressed;
    string? _pressedKey, _dragKey, _hoverKey;
    bool _tracking, _rendering, _dragged, _preview, _removeDrag;
    DragGhostWindow? _ghost;
    double _last, _barWidth, _barVelocity;
    double _lightX = .5, _lightY = -.25, _lightStrength, _lightXV, _lightYV, _lightSV;
    public Rect BarBounds { get; private set; }
    public Action<DockEntry>? Invoked;
    public Action<DockEntry, FrameworkElement>? PreviewRequested;
    public Action? PreviewHidden;
    public Action? LayoutUpdatedFrame;
    public Func<GlassInput?>? PreviewInput { get => _bar.InputProvider; set => _bar.InputProvider = value; }
    public bool IsDragging => _dragKey != null;
    internal bool HasWindow(IntPtr handle) => _tiles.Any(t => t.Entry.Window == handle);
    internal double AlignmentError => _tiles.Where(t => t.Entry.Key != _dragKey).Select(t => Math.Abs((t.Bounds.Top + t.Bounds.Width / 2) - (BarBounds.Top + BarBounds.Height / 2))).DefaultIfEmpty(0).Max();
    internal bool DividerInGap => _tiles.FindIndex(t => t.Entry.SectionBreakBefore) is int index && index > 0 && GetLeft(_separator) > _tiles[index - 1].Bounds.Right && GetLeft(_separator) < _tiles[index].Bounds.Left;
    protected override AutomationPeer OnCreateAutomationPeer() => new DockPeer(this);
    sealed class DockPeer : FrameworkElementAutomationPeer
    {
        readonly DockView _dock;
        public DockPeer(DockView owner) : base(owner) { _dock = owner; }
        protected override string GetClassNameCore() => "ChengyuDock";
        protected override string GetNameCore() => "桌面 Dock";
        protected override List<AutomationPeer> GetChildrenCore() => _dock._tiles.Select(t => (AutomationPeer)new TilePeer(_dock, t)).ToList();
    }
    sealed class TilePeer : FrameworkElementAutomationPeer, IInvokeProvider
    {
        readonly DockView _dock; readonly Tile _tile;
        public TilePeer(DockView dock, Tile tile) : base(tile.Visual) { _dock = dock; _tile = tile; }
        protected override string GetNameCore() => _tile.Entry.Name;
        protected override string GetClassNameCore() => "DockItem";
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;
        public override object? GetPattern(PatternInterface patternInterface) => patternInterface == PatternInterface.Invoke ? this : base.GetPattern(patternInterface);
        public void Invoke() => _dock.Dispatcher.BeginInvoke(() => _dock.Invoked?.Invoke(_tile.Entry));
    }
    public DockView(bool preview = false)
    {
        _preview = preview; Height = 210; Background = Brushes.Transparent; ClipToBounds = false; Focusable = false; AllowDrop = !preview;
        _bar = new GlassSurface { IsHitTestVisible = false, FrostedKind = 3 };
        Children.Add(_bar);
        _separator = new Rectangle { Width = 1, IsHitTestVisible = false, Fill = new LinearGradientBrush(new GradientStopCollection { new(Colors.Transparent, 0), new(Color.FromArgb(15, 255, 255, 255), .18), new(Color.FromArgb(51, 255, 255, 255), .36), new(Color.FromArgb(51, 255, 255, 255), .64), new(Color.FromArgb(15, 255, 255, 255), .82), new(Colors.Transparent, 1) }, 90) };
        _separator.Effect = new BlurEffect { Radius = .35 };
        Children.Add(_separator);
        _labelText = new TextBlock { FontSize = 11, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 220 };
        _label = new Border { Child = _labelText, Background = new SolidColorBrush(Color.FromArgb(235, 27, 29, 38)), BorderBrush = new SolidColorBrush(Color.FromArgb(55, 255, 255, 255)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7), Padding = new Thickness(9, 5, 9, 5), Visibility = Visibility.Collapsed, IsHitTestVisible = false };
        Children.Add(_label);
        _hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) }; _hoverTimer.Tick += (_, _) =>
        { _hoverTimer.Stop(); var tile = _tiles.FirstOrDefault(x => x.Entry.Key == _hoverKey); if (tile != null && tile.Entry.Window != IntPtr.Zero && !_preview && !IsDragging) PreviewRequested?.Invoke(tile.Entry, this); };
        SizeChanged += (_, _) => Wake();
        Loaded += (_, _) => Wake();
        Unloaded += (_, _) => { Stop(); _hoverTimer.Stop(); _ghost?.Close(); _ghost = null; };
        DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        Drop += (_, e) => { if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) App.Current.AddPaths(paths); e.Handled = true; };
    }
    List<DockEntry> BuildEntries(Preferences prefs)
    {
        var entries = DockItems.Build(prefs, App.Current.OpenWindows, _preview, App.Current.RunningPaths); var running = entries.Where(e => e.Key.StartsWith("running-")).ToDictionary(e => e.Key);
        _runningOrder.RemoveAll(key => !running.ContainsKey(key)); foreach (var entry in running.Values) if (!_runningOrder.Contains(entry.Key)) _runningOrder.Add(entry.Key);
        var result = entries.Where(e => !e.Key.StartsWith("running-")).ToList();
        for (int i = 0; i < _runningOrder.Count; i++) { var entry = running[_runningOrder[i]]; entry.SectionBreakBefore = i == 0; result.Add(entry); } return result;
    }
    public void Configure(Preferences prefs)
    {
        _settings = prefs;
        var entries = BuildEntries(prefs);
        _separator.Visibility = entries.Any(e => e.SectionBreakBefore) ? Visibility.Visible : Visibility.Collapsed;
        var old = _tiles.ToDictionary(x => x.Entry.Key);
        foreach (var tile in _tiles) Children.Remove(tile.Visual);
        _tiles.Clear();
        foreach (var entry in entries)
        {
            Tile tile = old.TryGetValue(entry.Key, out var existing) ? existing : CreateTile(entry);
            tile.Entry = entry; tile.Reflection.Visibility = prefs.Reflection ? Visibility.Visible : Visibility.Collapsed;
            tile.Dot.Visibility = entry.Running ? Visibility.Visible : Visibility.Collapsed;
            _tiles.Add(tile); Children.Insert(Children.Count - 1, tile.Visual);
        }
        _bar.Visibility = prefs.Material == "none" ? Visibility.Hidden : Visibility.Visible;
        _bar.Material = prefs.Material; _bar.Refresh();
        Wake();
    }
    Tile CreateTile(DockEntry entry)
    {
        var image = Icons.Get(entry.Path, entry.Name);
        var root = new Grid { Width = 58, Height = 88, IsHitTestVisible = false, RenderTransformOrigin = new Point(0, 0) };
        var icon = new Image { Source = image, Width = 54, Height = 54, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(2), Effect = new DropShadowEffect { BlurRadius = 6, ShadowDepth = 2, Opacity = .35 } };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        root.Children.Add(icon);
        var reflection = new Image { Source = image, Width = 50, Height = 25, Stretch = Stretch.Fill, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(4, 63, 4, 0), Opacity = .32,
            RenderTransformOrigin = new Point(.5, .5), RenderTransform = new ScaleTransform(1, -1), OpacityMask = new LinearGradientBrush(Colors.Transparent, Colors.Black, 90) };
        root.Children.Add(reflection);
        var dot = new Ellipse { Width = 4, Height = 4, Fill = new SolidColorBrush(Color.FromRgb(215, 229, 247)), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 61, 0, 0) };
        root.Children.Add(dot);
        return new Tile { Entry = entry, Visual = root, Reflection = reflection, Dot = dot, X = ActualWidth / 2 };
    }
    public void RefreshRunning()
    {
        if (_settings == null || IsDragging) return;
        var entries = BuildEntries(_settings);
        if (!_tiles.Select(t => t.Entry.Key).SequenceEqual(entries.Select(e => e.Key))) { Configure(_settings); return; }
        for (int i = 0; i < entries.Count; i++) { _tiles[i].Entry = entries[i]; _tiles[i].Dot.Visibility = entries[i].Running ? Visibility.Visible : Visibility.Collapsed; }
    }
    public void Wake()
    {
        if (_rendering || _settings == null || !IsLoaded) return;
        _rendering = true; _last = 0; CompositionTarget.Rendering += Frame;
    }
    void Stop() { if (!_rendering) return; CompositionTarget.Rendering -= Frame; _rendering = false; }
    internal static bool Spring(ref double value, ref double velocity, double target, double dt, bool snap, double frequency = 24)
    {
        if (snap) { value = target; velocity = 0; return false; }
        double zeta = frequency == 24 ? .68 : .78, decayRate = zeta * frequency;
        double wd = frequency * Math.Sqrt(1 - zeta * zeta), x = value - target, v = velocity;
        double e = Math.Exp(-decayRate * dt), cosine = Math.Cos(wd * dt), sine = Math.Sin(wd * dt);
        value = target + e * (x * cosine + (v + decayRate * x) / wd * sine);
        velocity = e * (v * cosine - (frequency * frequency * x + decayRate * v) / wd * sine);
        if (Math.Abs(target - value) < .0005 && Math.Abs(velocity) < .012) { value = target; velocity = 0; return false; }
        return true;
    }
    void Frame(object? sender, EventArgs args)
    {
        if (_settings == null || ActualWidth < 10) return;
        double now = ((RenderingEventArgs)args).RenderingTime.TotalSeconds;
        double dt = _last == 0 ? 1.0 / 60 : Math.Clamp(now - _last, .001, .04); _last = now;
        double overall = _settings.IconScale / 100.0, spacing = _settings.IconSpacing;
        double naturalWidth = 36 + _tiles.Count * 58 + Math.Max(0, _tiles.Count - 1) * spacing + (_tiles.Any(t => t.Entry.SectionBreakBefore) ? 22 : 0);
        double fit = Math.Min(1, Math.Max(.25, (ActualWidth - 24) / (naturalWidth * overall)));
        overall *= fit;
        bool active = _tracking && !IsDragging, changed = false, snap = _settings.ReducedMotion || !SystemParameters.ClientAreaAnimation;
        double naturalLeft = (ActualWidth - naturalWidth * overall) / 2 + 18 * overall, width = 36;
        double naturalCursor = naturalLeft;
        for (int i = 0; i < _tiles.Count; i++)
        {
            var tile = _tiles[i]; if (tile.Entry.SectionBreakBefore) naturalCursor += 22 * overall;
            double center = naturalCursor + 29 * overall; naturalCursor += (58 + spacing) * overall;
            double d = Math.Abs(_mouse.X - center) / ((58 + spacing) * 2.8 * overall);
            double target = active ? DockMotion.TargetScale(d) : 1;
            changed |= Spring(ref tile.Scale, ref tile.ScaleVelocity, target, dt, snap);
            width += 58 * tile.Scale + (i < _tiles.Count - 1 ? spacing : 0) + (tile.Entry.SectionBreakBefore ? 22 : 0);
        }
        width *= overall; changed |= Spring(ref _barWidth, ref _barVelocity, width, dt, snap || _barWidth < 1);
        double left = (ActualWidth - width) / 2 + 18 * overall;
        double baseline = _preview ? ActualHeight - 21 * overall : 151;
        double barHeight = 100 * overall;
        double barTop = baseline - 79 * overall;
        double centerY = barTop + barHeight / 2;
        for (int i = 0; i < _tiles.Count; i++)
        {
            var tile = _tiles[i];
            if (tile.Entry.SectionBreakBefore)
            {
                SetLeft(_separator, left + 8.5 * overall); SetTop(_separator, centerY - 38 * overall);
                _separator.Width = Math.Max(.7, overall); _separator.Height = 76 * overall;
                left += 22 * overall;
            }
            double size = 58 * tile.Scale * overall;
            if (tile.Entry.Key == _dragKey) tile.X = _mouse.X - size / 2;
            else changed |= Spring(ref tile.X, ref tile.XVelocity, left, dt, snap);
            double y = centerY - size / 2 - (tile.Entry.Key == _dragKey ? 9 : 0);
            SetLeft(tile.Visual, tile.X); SetTop(tile.Visual, y); tile.Visual.RenderTransform = new ScaleTransform(tile.Scale * overall, tile.Scale * overall);
            tile.Visual.Opacity = tile.Entry.Key == _dragKey ? 0 : tile.Entry.Key == _pressedKey ? .7 : 1;
            SetZIndex(tile.Visual, tile.Entry.Key == _dragKey ? 30 : i + 1);
            tile.Bounds = new Rect(tile.X, y, size, size + 12 * overall);
            // The running mark has its own bottom alignment, independent of icon centering.
            tile.Dot.Margin = new Thickness(0, (barTop + barHeight - 6 * overall - 2.5 - y) / (tile.Scale * overall), 0, 0);
            tile.Dot.Opacity = Math.Clamp((1.3 - tile.Scale) / .2, 0, 1);
            left += size + spacing * overall;
        }
        BarBounds = new Rect((ActualWidth - _barWidth) / 2, barTop, _barWidth, barHeight);
        SetLeft(_bar, BarBounds.Left); SetTop(_bar, BarBounds.Top); _bar.Width = BarBounds.Width; _bar.Height = BarBounds.Height; _bar.Radius = 34 * overall;
        changed |= Spring(ref _lightX, ref _lightXV, active ? Math.Clamp((_mouse.X - BarBounds.Left) / Math.Max(1, BarBounds.Width), 0, 1) : .5, dt, snap, 16);
        changed |= Spring(ref _lightY, ref _lightYV, active ? Math.Clamp((_mouse.Y - barTop) / Math.Max(1, barHeight), -.5, 1.5) : -.25, dt, snap, 16);
        changed |= Spring(ref _lightStrength, ref _lightSV, active ? 1 : 0, dt, snap, 14);
        _bar.Light(_lightX, _lightY, _lightStrength);
        var hovered = _tiles.FirstOrDefault(x => x.Entry.Key == _hoverKey);
        if (hovered != null && !IsDragging)
        { _label.Measure(new Size(250, 100)); SetLeft(_label, Math.Clamp(hovered.Bounds.Left + hovered.Bounds.Width / 2 - _label.DesiredSize.Width / 2, 4, Math.Max(4, ActualWidth - _label.DesiredSize.Width - 4))); SetTop(_label, Math.Max(2, hovered.Bounds.Top - 35)); SetZIndex(_label, 100); }
        LayoutUpdatedFrame?.Invoke();
        if (!changed && !IsDragging) Stop();
    }
    Tile? Hit(Point p) => _tiles.LastOrDefault(t => t.Bounds.Contains(p));
    public bool Interactive(Point p) => IsDragging || BarBounds.Contains(p) || _tiles.Any(t => t.Bounds.Contains(p));
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e); _mouse = e.GetPosition(this); _tracking = true;
        if (_pressedKey != null && e.LeftButton == MouseButtonState.Pressed && (_mouse - _pressed).Length > 5 && _tiles.FirstOrDefault(t => t.Entry.Key == _pressedKey)?.Entry.Pin != null)
        { _dragKey = _pressedKey; _dragged = true; Cursor = Cursors.SizeAll; }
        if (_dragKey != null)
        {
            var moving = _tiles.First(t => t.Entry.Key == _dragKey); var target = _tiles.Where(t => t.Entry.Pin != null && t != moving).OrderBy(t => Math.Abs(_mouse.X - (t.Bounds.Left + t.Bounds.Width / 2))).FirstOrDefault();
            _removeDrag = DockItems.DragRemoves(new PointLike(_mouse.X, _mouse.Y), BarBounds);
            _ghost ??= new DragGhostWindow(); _ghost.Update(moving.Entry, _removeDrag, this);
            if (!_removeDrag && target != null && Math.Abs(_mouse.X - (target.Bounds.Left + target.Bounds.Width / 2)) < 24)
            { int index = _tiles.IndexOf(target); _tiles.Remove(moving); _tiles.Insert(index, moving); }
            _label.Visibility = Visibility.Collapsed; PreviewHidden?.Invoke();
        }
        else
        {
            var tile = Hit(_mouse); string? key = tile?.Entry.Key;
            Cursor = tile == null ? Cursors.Arrow : Cursors.Hand;
            if (_hoverKey != key)
            {
                _hoverTimer.Stop(); PreviewHidden?.Invoke(); _hoverKey = key;
                _label.Visibility = tile == null ? Visibility.Collapsed : Visibility.Visible;
                if (tile != null) { _labelText.Text = tile.Entry.Name; _hoverTimer.Start(); }
            }
        }
        Wake();
    }
    protected override void OnMouseLeave(MouseEventArgs e)
    { base.OnMouseLeave(e); if (!IsMouseCaptured) { _tracking = false; _hoverKey = null; _label.Visibility = Visibility.Collapsed; _hoverTimer.Stop(); PreviewHidden?.Invoke(); Wake(); } }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    { base.OnMouseLeftButtonDown(e); var tile = Hit(e.GetPosition(this)); if (tile == null) return; _pressed = e.GetPosition(this); _pressedKey = tile.Entry.Key; _dragged = false; CaptureMouse(); Wake(); e.Handled = true; }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e); var tile = Hit(e.GetPosition(this)); string? pressed = _pressedKey; bool dragged = _dragged;
        string[] order = _tiles.Where(t => t.Entry.Pin != null).Select(t => t.Entry.Pin!.Id).ToArray();
        bool remove = dragged && DockItems.DragRemoves(new PointLike(e.GetPosition(this).X, e.GetPosition(this).Y), BarBounds);
        string? removedId = _tiles.FirstOrDefault(t => t.Entry.Key == pressed)?.Entry.Pin?.Id;
        _pressedKey = null; _dragKey = null; _ghost?.Hide(); _removeDrag = false; ReleaseMouseCapture(); Cursor = Cursors.Arrow;
        if (remove && removedId != null && !_preview) App.Current.Apply(p => p.Pins = order.Where(id => id != removedId).Select(id => p.Pins.First(x => x.Id == id)).ToList());
        else if (dragged && !_preview) App.Current.ReorderPins(order);
        else if (!dragged && tile != null && pressed != null && tile.Entry.Key == pressed && !_preview) Invoked?.Invoke(tile.Entry);
        _tracking = IsMouseOver; Wake(); e.Handled = true;
    }
    protected override void OnLostMouseCapture(MouseEventArgs e)
    { base.OnLostMouseCapture(e); if (_dragKey != null) { _ghost?.Hide(); _removeDrag = false; _dragKey = null; _pressedKey = null; Configure(_settings); } Wake(); }
    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e); if (_preview) return;
        var tile = Hit(e.GetPosition(this)); var menu = AppMenu.Create(tile?.Entry.Name ?? "Dock");
        if (tile != null)
        {
            AppMenu.Action(menu, tile.Entry.Window != IntPtr.Zero ? "显示窗口" : "打开", () => { if (tile.Entry.Window != IntPtr.Zero) Native.Activate(tile.Entry.Window, false); else Invoked?.Invoke(tile.Entry); }, "↗");
            if (tile.Entry.Pin is Pin pin) AppMenu.Action(menu, "取消常驻", () => App.Current.Apply(p => p.Pins.RemoveAll(x => x.Id == pin.Id)), "−", hint: "也可拖远移除");
            else if (tile.Entry.Window != IntPtr.Zero && !string.IsNullOrWhiteSpace(tile.Entry.Path)) AppMenu.Action(menu, "固定到左侧常驻区", () => App.Current.PinRunningApp(tile.Entry.Path, tile.Entry.Name), "+");
            if (tile.Entry.Windows.Length > 1) foreach (var window in tile.Entry.Windows) AppMenu.Action(menu, window.Title.Length > 30 ? window.Title[..30] + "…" : window.Title, () => Native.Activate(window.Handle, false), "▢");
            AppMenu.Divider(menu);
        }
        AppMenu.Materials(menu, _settings.Material, value => App.Current.Apply(p => p.Material = value));
        AppMenu.Divider(menu);
        AppMenu.Action(menu, "显示启动台图标", () => App.Current.Apply(p => p.ShowLaunchpad = !p.ShowLaunchpad), selected: _settings.ShowLaunchpad);
        AppMenu.Action(menu, "打开启动台", () => App.Current.ShowLaunchpad(), "⠿");
        AppMenu.Action(menu, "显示右侧运行中应用", () => App.Current.Apply(p => p.ShowRunningApps = !p.ShowRunningApps), selected: _settings.ShowRunningApps);
        AppMenu.Action(menu, "始终置顶", () => App.Current.Apply(p => p.DockTopmost = !p.DockTopmost), selected: _settings.DockTopmost);
        AppMenu.Action(menu, "打开 Dock 设置", () => AppMenu.Manager("dock"), "⚙");
        AppMenu.Action(menu, "添加常驻项目", () => AppMenu.Manager("dock"), "+");
        AppMenu.Show(menu, this); _tracking = false; _hoverTimer.Stop(); PreviewHidden?.Invoke(); Wake(); e.Handled = true;
    }
}
internal static class DockMotion
{
    public static double TargetScale(double normalizedDistance) => normalizedDistance < 1 ? 1 + .5 * Math.Pow(1 - normalizedDistance * normalizedDistance, 2) : 1;
}
