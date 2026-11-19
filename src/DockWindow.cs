using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace ChengyuDock;

public sealed class DockWindow : Window, IDisposable
{
    readonly DockView _view; readonly PreviewWindow _preview;
    Rect _work;
    public DockWindow()
    {
        Title = "澄屿 Dock · 桌面组件"; WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; ShowActivated = false; Topmost = false; Focusable = false;
        Height = 210; Width = 900;
        _view = new DockView(); Content = _view;
        _preview = new PreviewWindow();
        _view.Invoked = entry =>
        {
            _preview.HidePreview();
            if (entry.Path == "@launchpad") App.Current.ShowLaunchpad();
            else if (entry.Path == "@manage") App.Current.ShowManager();
            else if (entry.Pin != null) App.Current.Launch(entry.Pin);
            else if (entry.Window != IntPtr.Zero) Native.Activate(entry.Window, true);
        };
        _view.PreviewHidden = () => _preview.HidePreview();
        _view.PreviewRequested = (entry, _) =>
        {
            if (!IsVisible || _view.IsDragging) return;
            Native.GetCursorPos(out var cursor);
            _preview.ShowPreview(entry, cursor.X, Top);
        };
        _view.LayoutUpdatedFrame = UpdateBackdrop;
        SourceInitialized += (_, _) =>
        {
            DesktopLayer.Attach(this, () => !App.Current.Prefs.DockTopmost); GlassPump.AllowCapture(this);
            var source = (HwndSource)PresentationSource.FromVisual(this);
            source.AddHook((IntPtr h, int m, IntPtr w, IntPtr l, ref bool handled) =>
            {
                if (m == 0x84 && !_view.IsDragging)
                {
                    int x = unchecked((short)(l.ToInt64() & 0xffff)), y = unchecked((short)((l.ToInt64() >> 16) & 0xffff));
                    Point p = PointFromScreen(new Point(x, y));
                    if (!_view.Interactive(p)) { handled = true; return new IntPtr(-1); }
                }
                if (m == 0x2e0) Dispatcher.BeginInvoke(() => Sync());
                return IntPtr.Zero;
            });
        };
        App.Current.WindowsChanged += Refresh;
    }
    void Refresh() => _view.RefreshRunning();
    internal bool ContainsWindow(IntPtr handle) => _view.HasWindow(handle);
    internal double AlignmentError => _view.AlignmentError;
    internal bool DividerInGap => _view.DividerInGap;
    internal int ProbePreview(WindowEntry entry) { Native.GetCursorPos(out var p); _preview.ShowPreview(new DockEntry { Name = entry.Title, Window = entry.Handle }, p.X, Top); return _preview.LastResult; }
    internal void EndPreview() => _preview.HidePreview();
    public void Sync()
    {
        Topmost = App.Current.Prefs.DockTopmost;
        _view.Configure(App.Current.Prefs);
        if (!App.Current.Prefs.Enabled) { Hide(); _preview.HidePreview(); return; }
        if (!IsVisible) Show();
        _work = Native.MonitorWork(this);
        Width = Math.Max(250, Math.Min(_work.Width - 12, (App.Current.Prefs.Pins.Count + 14) * 85));
        Left = _work.Left + (_work.Width - Width) / 2;
        Top = _work.Bottom - 177 - App.Current.Prefs.BottomOffset;
        _view.Wake(); UpdateBackdrop();
    }
    void UpdateBackdrop() { /* Material is now drawn by the shared D3D11 surface. */ }
    public void Dispose()
    {
        App.Current.WindowsChanged -= Refresh; _view.LayoutUpdatedFrame = null;
        _preview.Close(); Close();
    }
}
internal sealed class PreviewWindow : Window
{
    internal int LastResult { get; private set; }
    IntPtr _thumbnail; readonly TextBlock _title, _fallback;
    public PreviewWindow()
    {
        Title = "澄屿 Dock · 窗口预览"; Width = 284; Height = 202; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false; ShowActivated = false; Topmost = true; Background = new SolidColorBrush(Color.FromRgb(27, 30, 40));
        var grid = new Grid(); _title = new TextBlock { Margin = new Thickness(13, 10, 13, 0), TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12 };
        _fallback = new TextBlock { Text = "此窗口暂时无法提供预览", Foreground = new SolidColorBrush(Color.FromRgb(140, 146, 162)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 30, 0, 0) };
        grid.Children.Add(_fallback); grid.Children.Add(_title); Content = grid;
        SourceInitialized += (_, _) => Native.MakePassive(this);
    }
    public void ShowPreview(DockEntry entry, int cursorX, double dockTop)
    {
        HidePreview(); _title.Text = entry.Name;
        if (!Native.IsWindow(entry.Window)) return;
        if (!IsVisible) Show();
        Rect work = Native.MonitorWork(this, true); var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        double x = transform.Transform(new Point(cursorX, 0)).X;
        Left = Math.Clamp(x - Width / 2, work.Left + 8, Math.Max(work.Left + 8, work.Right - Width - 8)); Top = Math.Max(work.Top + 8, dockTop - Height + 28);
        var hwnd = new WindowInteropHelper(this).Handle;
        LastResult = Native.DwmRegisterThumbnail(hwnd, entry.Window, out _thumbnail);
        if (LastResult != 0) { _thumbnail = IntPtr.Zero; _fallback.Visibility = Visibility.Visible; return; }
        Native.DwmQueryThumbnailSourceSize(_thumbnail, out var size);
        var dpi = VisualTreeHelper.GetDpi(this);
        double availableW = 260 * dpi.DpiScaleX, availableH = 151 * dpi.DpiScaleY;
        double scale = Math.Min(availableW / Math.Max(1, size.Width), availableH / Math.Max(1, size.Height));
        int width = (int)(size.Width * scale), height = (int)(size.Height * scale);
        int left = (int)(12 * dpi.DpiScaleX + (availableW - width) / 2), top = (int)(38 * dpi.DpiScaleY + (availableH - height) / 2);
        var properties = new Native.ThumbnailProperties { Flags = 1 | 4 | 8 | 16, Destination = new Native.NativeRect { Left = left, Top = top, Right = left + width, Bottom = top + height }, Visible = true, Opacity = 255, ClientOnly = false };
        LastResult = Native.DwmUpdateThumbnailProperties(_thumbnail, ref properties);
        _fallback.Visibility = LastResult == 0 ? Visibility.Hidden : Visibility.Visible;
    }
    public void HidePreview() { if (_thumbnail != IntPtr.Zero) { Native.DwmUnregisterThumbnail(_thumbnail); _thumbnail = IntPtr.Zero; } Hide(); }
    protected override void OnClosed(EventArgs e) { if (_thumbnail != IntPtr.Zero) { Native.DwmUnregisterThumbnail(_thumbnail); _thumbnail = IntPtr.Zero; } base.OnClosed(e); }
}
