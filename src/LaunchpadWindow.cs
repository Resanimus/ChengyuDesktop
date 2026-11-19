using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ChengyuDock;

public sealed class LaunchpadWindow : Window
{
    readonly WrapPanel _apps; readonly TextBox _search;
    public LaunchpadWindow(GlassInput? scene = null)
    {
        Title = "澄屿 Dock · 启动台"; Width = 680 * App.Current.Prefs.LaunchpadScale / 100.0; Height = 440 * App.Current.Prefs.LaunchpadScale / 100.0;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; Topmost = true;
        var glass = new GlassSurface { Radius = 32, LiquidKind = 3, FrostedKind = 1, Material = App.Current.Prefs.LaunchpadMaterial, InputProvider = () => scene };
        var root = new Border { CornerRadius = new CornerRadius(32), Background = Brushes.Transparent, Padding = new Thickness(25) };
        var grid = new Grid(); grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); grid.RowDefinitions.Add(new RowDefinition());
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 20) };
        var close = new Button { Content = "✕", Padding = new Thickness(8, 4, 8, 4), HorizontalAlignment = HorizontalAlignment.Right }; close.Click += (_, _) => Close(); DockPanel.SetDock(close, System.Windows.Controls.Dock.Right); top.Children.Add(close);
        top.Children.Add(new TextBlock { Text = "启动台", FontSize = 22, FontWeight = FontWeights.SemiBold }); grid.Children.Add(top);
        _search = new TextBox { Margin = new Thickness(0, 0, 0, 18), ToolTip = "搜索已固定的应用、文件或文件夹" }; _search.TextChanged += (_, _) => Render(); Grid.SetRow(_search, 1); grid.Children.Add(_search);
        _apps = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center }; var scroll = new ScrollViewer { Content = _apps, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; Grid.SetRow(scroll, 2); grid.Children.Add(scroll);
        root.Child = grid; var layers = new Grid(); layers.Children.Add(glass); layers.Children.Add(root); Content = layers;
        MouseRightButtonUp += (_, e) => { var menu = AppMenu.Create("启动台材质"); AppMenu.Materials(menu, App.Current.Prefs.LaunchpadMaterial, value => { App.Current.Apply(p => p.LaunchpadMaterial = value); glass.Material = value; glass.Refresh(); }); AppMenu.Show(menu, root); e.Handled = true; };
        SourceInitialized += (_, _) => { Rect work = Native.MonitorWork(this, true); Left = work.Left + (work.Width - Width) / 2; Top = work.Top + (work.Height - Height) / 2; GlassPump.AllowCapture(this); };
        Loaded += (_, _) => { Render(); _search.Focus(); }; KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        bool closing = false; Closing += (_, _) => closing = true; Deactivated += (_, _) => { if (!closing) Close(); };
    }
    internal int ProbeSearch(string query) { _search.Text = query; Render(); return _apps.Children.OfType<Button>().Count(); }
    void Render()
    {
        if (_apps == null) return; _apps.Children.Clear(); string query = _search.Text.Trim(); double size = 54 * App.Current.Prefs.LaunchpadIconScale / 100.0;
        foreach (var pin in App.Current.Prefs.Pins.Where(x => x.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)))
        {
            var stack = new StackPanel { Width = Math.Max(84, size + 20), Margin = new Thickness(8) };
            stack.Children.Add(new Image { Source = Icons.Get(pin.Path, pin.Name), Width = size, Height = size, Margin = new Thickness(0, 3, 0, 10) });
            stack.Children.Add(new TextBlock { Text = pin.Name, FontSize = 11, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            var button = new Button { Content = stack, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(0), ToolTip = pin.Path }; button.Click += (_, _) => { App.Current.Launch(pin); Close(); }; _apps.Children.Add(button);
        }
        if (_apps.Children.Count == 0) _apps.Children.Add(new TextBlock { Text = "没有匹配的项目", Foreground = new SolidColorBrush(Color.FromRgb(140, 146, 162)), Margin = new Thickness(20) });
    }
}
