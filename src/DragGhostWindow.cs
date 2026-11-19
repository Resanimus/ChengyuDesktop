using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace ChengyuDock;
internal sealed class DragGhostWindow : Window
{
    readonly Image _icon = new() { Width = 52, Height = 52 };
    readonly TextBlock _label = Ui.Text("", 11);
    readonly Border _caption;
    public DragGhostWindow()
    {
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; ShowActivated = false; Topmost = true; IsHitTestVisible = false; Width = 112; Height = 92;
        _caption = new Border { Background = Ui.Brush("#ED172332"), CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 5, 0, 0), Child = _label };
        _label.HorizontalAlignment = HorizontalAlignment.Center;
        Content = Ui.Stack(_icon, _caption);
        SourceInitialized += (_, _) => { Native.MakePassive(this); var h = new WindowInteropHelper(this).Handle; DesktopLayer.SetStyle(h, -20, new IntPtr(DesktopLayer.GetStyle(h, -20).ToInt64() | 0x20)); };
    }
    public void Update(DockEntry entry, bool removing, FrameworkElement owner) =>
        Update(entry.Path, entry.Name, removing ? "松手取消常驻" : "拖回 Dock 排序", removing, owner);

    public void Update(string path, string name, string? label, bool removing, FrameworkElement owner)
    {
        _icon.Source = Icons.Get(path, name);
        _icon.Opacity = removing ? .55 : .96;
        _caption.Visibility = string.IsNullOrEmpty(label) ? Visibility.Collapsed : Visibility.Visible;
        _label.Text = label ?? "";
        _label.Foreground = Ui.Brush(removing ? "#FFAAA5" : "#BBD5F2");
        Native.GetCursorPos(out var p); var dpi = VisualTreeHelper.GetDpi(owner); Left = p.X / dpi.DpiScaleX - 56; Top = p.Y / dpi.DpiScaleY - 28; if (!IsVisible) Show();
    }
}
