using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ChengyuDock;
public sealed class ComponentManagerWindow : Window
{
    readonly Dictionary<string, (CheckBox Enabled, ComboBox Material)> _controls = new();
    readonly TextBox _city = new() { MinWidth = 170, Margin = new Thickness(0, 0, 8, 0) };
    readonly TextBlock _weatherState = new() { Foreground = Brushes.Gray, FontSize = 11, Margin = new Thickness(0, 8, 0, 0) };
    readonly ComboBox _launchpad = MaterialBox(false);
    bool _syncing;
    static readonly string[] Materials = { "liquid", "frosted", "none", "solid" };
    static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));
    public ComponentManagerWindow()
    {
        Title = "澄屿 · 桌面组件与灵动岛"; Width = 650; Height = Math.Min(790, SystemParameters.WorkArea.Height - 40); MinHeight = 540; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brush("#101219"); Foreground = Brushes.White; FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");
        var panel = new StackPanel { Margin = new Thickness(26) };
        panel.Children.Add(new TextBlock { Text = "桌面组件", FontSize = 27, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "拖动调整位置 · 右键切换玻璃、无背景或锁定", FontSize = 12, Foreground = Brush("#9BA6B6"), Margin = new Thickness(0, 9, 0, 20) });
        foreach (var pair in WidgetPreference.Names)
        {
            string id = pair.Key;
            var row = new Grid { Margin = new Thickness(0, 0, 0, 8), Background = Brush("#191D27"), Height = 53 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) }); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
            var enabled = new CheckBox { Style = (Style)FindResource("Toggle"), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            System.Windows.Automation.AutomationProperties.SetName(enabled, "启用" + pair.Value);
            enabled.Checked += (_, _) => Toggle(id, true); enabled.Unchecked += (_, _) => Toggle(id, false); row.Children.Add(enabled);
            var name = new TextBlock { Text = pair.Value, VerticalAlignment = VerticalAlignment.Center, FontSize = 13 }; Grid.SetColumn(name, 1); row.Children.Add(name);
            var material = MaterialBox(true); material.Margin = new Thickness(4, 10, 12, 10); Grid.SetColumn(material, 2); row.Children.Add(material);
            System.Windows.Automation.AutomationProperties.SetName(material, pair.Value + "材质");
            material.SelectionChanged += (_, _) => { if (!_syncing && material.SelectedIndex >= 0) App.Current.Apply(p => Get(p, id).Material = Materials[material.SelectedIndex]); };
            _controls.Add(id, (enabled, material)); panel.Children.Add(row);
        }
        var launch = new DockPanel { Margin = new Thickness(0, 12, 0, 12) }; DockPanel.SetDock(_launchpad, Dock.Right); launch.Children.Add(_launchpad); launch.Children.Add(new TextBlock { Text = "启动台材质", VerticalAlignment = VerticalAlignment.Center }); panel.Children.Add(launch);
        _launchpad.SelectionChanged += (_, _) => { if (!_syncing && _launchpad.SelectedIndex >= 0) App.Current.Apply(p => p.LaunchpadMaterial = Materials[_launchpad.SelectedIndex]); };
        panel.Children.Add(new Separator { Margin = new Thickness(0, 5, 0, 15) });
        panel.Children.Add(new TextBlock { Text = "天气城市", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        var cityRow = new StackPanel { Orientation = Orientation.Horizontal }; cityRow.Children.Add(_city);
        var weather = new Button { Content = "查询并保存", Padding = new Thickness(14, 6, 14, 6) }; cityRow.Children.Add(weather); panel.Children.Add(cityRow); panel.Children.Add(_weatherState);
        weather.Click += async (_, _) => { weather.IsEnabled = false; _weatherState.Text = "正在查询城市…"; try { _weatherState.Text = await App.Current.Services.SetWeatherCity(_city.Text) ? "已保存天气位置" : "未找到城市，请输入更具体的名称"; } catch { _weatherState.Text = "暂时无法查询，请稍后再试"; } finally { weather.IsEnabled = true; } };
        var buttons = new WrapPanel { Margin = new Thickness(0, 20, 0, 5) };
        void Button(string label, Action action) { var b = new Button { Content = label, Margin = new Thickness(0, 0, 8, 8) }; b.Click += (_, _) => action(); buttons.Children.Add(b); }
        Button("恢复组件位置", () => App.Current.Apply(p => { foreach (var w in p.Widgets.Values) { w.X = null; w.Y = null; } }));
        Button("选择本地音乐", App.Current.Services.OpenMusic); Button("管理收纳盒", () => App.Current.Manager.Navigate("storage")); Button("Dock 管理", App.Current.ShowManager); panel.Children.Add(buttons);
        panel.Children.Add(new TextBlock { Text = "天气：Open-Meteo · 音乐：本地文件\n性能显示真实 CPU、内存和网络；GPU 暂无读数时显示“--”。", Foreground = Brush("#7F8A9A"), FontSize = 11, LineHeight = 19, Margin = new Thickness(0, 8, 0, 0) });
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        App.Current.Changed += Sync; Closed += (_, _) => App.Current.Changed -= Sync; Sync();
    }
    static ComboBox MaterialBox(bool solid)
    {
        var box = new ComboBox { Width = 125, FontSize = 12, Background = Brush("#252B38"), Foreground = Brushes.White, BorderBrush = Brush("#475064") };
        box.Items.Add("液态玻璃"); box.Items.Add("磨砂玻璃"); box.Items.Add("无背景"); if (solid) box.Items.Add("纯黑实体");
        var style = new Style(typeof(ComboBoxItem)); style.Setters.Add(new Setter(Control.BackgroundProperty, Brush("#252B38"))); style.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White)); box.ItemContainerStyle = style; return box;
    }
    static WidgetPreference Get(Preferences p, string id) { if (!p.Widgets.TryGetValue(id, out var value)) p.Widgets[id] = value = WidgetPreference.Defaults()[id]; return value; }
    void Toggle(string id, bool value) { if (!_syncing) App.Current.Apply(p => Get(p, id).Enabled = value); }
    void Sync()
    {
        _syncing = true;
        foreach (var pair in _controls) { var value = App.Current.Prefs.Widgets.TryGetValue(pair.Key, out var p) ? p : WidgetPreference.Defaults()[pair.Key]; pair.Value.Enabled.IsChecked = value.Enabled; pair.Value.Material.SelectedIndex = Array.IndexOf(Materials, value.Material); }
        _launchpad.SelectedIndex = Array.IndexOf(Materials, App.Current.Prefs.LaunchpadMaterial); if (!_city.IsKeyboardFocusWithin) _city.Text = App.Current.Prefs.WeatherCity; _syncing = false;
    }
}
