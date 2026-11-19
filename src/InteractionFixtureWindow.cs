using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace ChengyuDock;
// A separate data directory is required by the test launcher; it uses the production DockView.
internal sealed class InteractionFixtureWindow : Window
{
    readonly DockView _dock = new(); readonly TextBlock _status = Ui.Text("", 12);
    public InteractionFixtureWindow()
    {
        Title = "澄屿 · Dock交互验证"; Width = 1220; Height = 430; WindowStartupLocation = WindowStartupLocation.CenterScreen; Background = Ui.Brush("#172335");
        var root = new Grid(); root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(55) }); root.RowDefinitions.Add(new RowDefinition()); root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) });
        var heading = Ui.Text("Dock 交互验证 · 右键设置 / 常驻排序 / 远拖取消常驻", 17); heading.Margin = new Thickness(24, 0, 0, 0); root.Children.Add(heading);
        Grid.SetRow(_dock, 1); _dock.VerticalAlignment = VerticalAlignment.Top; root.Children.Add(_dock); Grid.SetRow(_status, 2); _status.Margin = new Thickness(24, 0, 0, 0); root.Children.Add(_status); Content = root;
        var source = new byte[16 * 16 * 4]; for (int i = 0; i < source.Length; i += 4) { source[i] = 54; source[i + 1] = 35; source[i + 2] = 23; source[i + 3] = 255; }
        _dock.PreviewInput = () => { var p = _dock.PointToScreen(new Point()); return new GlassInput(source, 16, 16, (int)p.X, (int)p.Y, 1); };
        _dock.Invoked = entry => _status.Text = "点击：" + entry.Name;
        App.Current.Changed += Sync; App.Current.WindowsChanged += Sync; Loaded += (_, _) => Sync(); Closed += (_, _) => { App.Current.Changed -= Sync; App.Current.WindowsChanged -= Sync; App.Current.Quit(); };
    }
    void Sync() { _dock.Configure(App.Current.Prefs); var items = DockItems.Build(App.Current.Prefs, App.Current.OpenWindows); _status.Text = $"常驻项目 {App.Current.Prefs.Pins.Count} 个 · 临时应用 {items.Count(e => e.Key.StartsWith("running-"))} 个 · 拖回 Dock 可以取消移除"; }
}
