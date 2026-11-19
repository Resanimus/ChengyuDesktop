using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using Microsoft.Win32;

namespace ChengyuDock;
internal static class Ui
{
    public static Brush Brush(string color) => (Brush)new BrushConverter().ConvertFromString(color)!;
    public static TextBlock Text(string text, double size = 13, string color = "#E9EDF5") => new() { Text = text, FontSize = size, Foreground = Brush(color), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    public static Button Button(string text, Action click) { var b = new Button { Content = text, Margin = new Thickness(3), Padding = new Thickness(13, 8, 13, 8) }; b.Click += (_, _) => click(); return b; }
    public static StackPanel Stack(params UIElement[] items) { var s = new StackPanel(); foreach (var item in items) s.Children.Add(item); return s; }
    public static WrapPanel Actions(params UIElement[] items) { var s = new WrapPanel { VerticalAlignment = VerticalAlignment.Center }; foreach (var item in items) s.Children.Add(item); return s; }
    public static Border Card(UIElement child) => new()
    {
        Background = new LinearGradientBrush(Color.FromRgb(27, 40, 53), Color.FromRgb(19, 29, 40), 70),
        BorderBrush = Brush("#2D4052"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(16),
        Padding = new Thickness(19), Margin = new Thickness(0, 0, 0, 13), Child = child
    };
    public static Grid Row(string title, UIElement control, string detail = "")
    {
        var g = new Grid { Margin = new Thickness(0, 9, 0, 9) }; g.ColumnDefinitions.Add(new ColumnDefinition()); g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = Stack(Text(title, 14));
        if (detail.Length > 0) { var subtitle = Text(detail, 11, "#9BADBF"); subtitle.Margin = new Thickness(0, 4, 0, 0); label.Children.Add(subtitle); }
        label.Margin = new Thickness(0, 0, 20, 0); g.Children.Add(label); Grid.SetColumn(control, 1); g.Children.Add(control); return g;
    }
    public static bool Interactive(object? target)
    {
        for (var d = target as DependencyObject; d != null; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is ButtonBase or TextBoxBase or Selector or Slider or ScrollBar or Thumb or DockView or DatePicker) return true;
        return false;
    }
}
public sealed partial class ManagerWindow : Window
{
    static readonly (string Key, string Title, string Subtitle)[] Pages =
    {
        ("overview", "桌面概览", "查看已启用的组件，继续布置你的桌面。"),
        ("components", "桌面组件", "选择组件、材质和位置锁定方式。"),
        ("dock", "Dock 与启动台", "调整常用应用、运行区域和启动台。"),
        ("island", "灵动岛", "设置顶部胶囊的显示和材质。"),
        ("spectrum", "音频频谱", "选择音源、频谱样式和静音行为。"),
        ("storage", "桌面收纳", "分别管理每个收纳盒的外观与内容。"),
        ("wallpaper", "本地壁纸", "扫描本地目录，管理图片、视频与 Wallpaper Engine 壁纸。"),
        ("performance", "性能监控", "选择桌面性能组件读取的显卡。"),
        ("content", "内容与数据", "管理天气位置、待办和本地音乐。"),
        ("settings", "设置", "调整桌面层级、动画和系统行为。")
    };
    readonly StackPanel _nav = new(), _page = new();
    readonly TextBlock _status = Ui.Text("设置自动保存", 12, "#8FFFFFFF"), _topState = Ui.Text("", 12, "#E0FFFFFF");
    readonly DockView _preview = new(true);
    readonly List<Action> _sync = new();
    readonly Dictionary<string, Button> _navButtons = new();
    readonly ScrollViewer _scroll;
    string _current = "overview";
    bool _syncing;
    Border? _hero;
    ContentControl? _previewHost;
    GlassInput? _previewImage;
    ulong _revision;
    Action? _refreshGpuPage;
    Action? _refreshStage;
    internal bool IsPerformancePage => IsVisible && _current == "performance";

    public ManagerWindow()
    {
        Title = "澄屿 · 桌面管理";
        Icon = Icons.Brand(false);
        var work = SystemParameters.WorkArea;
        Width = Math.Min(1180, work.Width - 36);
        Height = Math.Min(820, work.Height - 36);
        MinWidth = Math.Min(820, Width);
        MinHeight = Math.Min(560, Height);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        Background = Ui.Brush("#080B10");
        Foreground = Brushes.White;
        FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("ConsoleTheme.xaml", UriKind.Relative) });
        UseLayoutRounding = true;
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 0, ResizeBorderThickness = new Thickness(7), CornerRadius = new CornerRadius(28), GlassFrameThickness = new Thickness(0) });

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(41) });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(51) });
        AddConsoleBackdrop(root);
        Content = new Border
        {
            BorderBrush = Ui.Brush("#33FFFFFF"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(28),
            Child = root
        };

        var shell = new Grid();
        shell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(216) });
        shell.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetRow(shell, 1);
        root.Children.Add(shell);
        var rail = new Border
        {
            BorderBrush = Ui.Brush("#14FFFFFF"), BorderThickness = new Thickness(0, 0, 1, 0),
            Padding = new Thickness(12, 8, 12, 12)
        };
        shell.Children.Add(rail);
        for (int i = 0; i < Pages.Length; i++)
        {
            string? group = i switch { 0 => "工作台", 2 => "桌面体验", 7 => "监控与设置", _ => null };
            if (group != null)
            {
                var label = Ui.Text(group, 11, "#66FFFFFF");
                label.Margin = new Thickness(12, 14, 0, 6);
                _nav.Children.Add(label);
            }
            var page = Pages[i];
            var button = Ui.Button("", () => Navigate(page.Key));
            button.Margin = new Thickness(0, 2, 0, 2);
            button.Padding = new Thickness(12, 0, 8, 0);
            button.Height = 38;
            button.HorizontalContentAlignment = HorizontalAlignment.Left;
            var labelRow = new StackPanel { Orientation = Orientation.Horizontal };
            var symbol = ConsoleIcons.Draw(page.Key, 16, "#D1FFFFFF");
            symbol.Margin = new Thickness(0, 0, 10, 0);
            labelRow.Children.Add(symbol);
            labelRow.Children.Add(Ui.Text(page.Title, 13));
            button.Content = labelRow;
            AutomationProperties.SetName(button, page.Title);
            _nav.Children.Add(button);
            _navButtons[page.Key] = button;
        }
        var version = Ui.Text("澄屿 0.8", 11, "#66FFFFFF");
        version.Margin = new Thickness(14, 18, 0, 0);
        _nav.Children.Add(version);
        var scrollStyle = (Style)FindResource("ConsoleScroll");
        rail.Child = new ScrollViewer { Style = scrollStyle, Content = _nav, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };

        var toolbar = new DockPanel { Background = Brushes.Transparent, Margin = new Thickness(16, 0, 6, 0) };
        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        controls.Children.Add(WindowButton("minimize", "最小化", () => WindowState = WindowState.Minimized));
        controls.Children.Add(WindowButton("maximize", "最大化或还原", ToggleMaximize));
        controls.Children.Add(WindowButton("close", "关闭控制台", Hide));
        DockPanel.SetDock(controls, Dock.Right);
        toolbar.Children.Add(controls);
        var pill = new Border
        {
            Background = Ui.Brush("#050506"), BorderBrush = Ui.Brush("#24FFFFFF"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14), Height = 28, Padding = new Thickness(10, 0, 12, 0), Margin = new Thickness(0, 0, 8, 0),
            Child = StatusPill(), VerticalAlignment = VerticalAlignment.Center
        };
        DockPanel.SetDock(pill, Dock.Right);
        toolbar.Children.Add(pill);
        var brandRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        brandRow.Children.Add(new Image { Source = Icon, Width = 22, Height = 22, Margin = new Thickness(0, 0, 10, 0) });
        var brandName = Ui.Text("澄屿", 13);
        brandName.FontWeight = FontWeights.SemiBold;
        brandRow.Children.Add(brandName);
        var consoleLabel = Ui.Text("控制台", 13, "#66FFFFFF");
        consoleLabel.Margin = new Thickness(8, 0, 0, 0);
        brandRow.Children.Add(consoleLabel);
        toolbar.Children.Add(brandRow);
        root.Children.Add(toolbar);

        _scroll = new ScrollViewer { Style = scrollStyle, Content = _page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        _page.Margin = new Thickness(28, 8, 28, 22);
        Grid.SetColumn(_scroll, 1);
        shell.Children.Add(_scroll);
        var footer = new DockPanel { LastChildFill = true };
        var quit = Ui.Button("退出澄屿", () => App.Current.Quit());
        quit.Padding = new Thickness(12, 5, 12, 5);
        quit.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(quit, Dock.Right);
        footer.Children.Add(quit);
        footer.Children.Add(_status);
        var footerBorder = new Border { Child = footer, Padding = new Thickness(22, 0, 18, 0), BorderBrush = Ui.Brush("#14FFFFFF"), BorderThickness = new Thickness(0, 1, 0, 0) };
        Grid.SetRow(footerBorder, 2);
        root.Children.Add(footerBorder);
        SizeChanged += (_, _) => UpdateRail(shell, version);

        toolbar.MouseLeftButtonDown += (_, e) =>
        {
            if (Ui.Interactive(e.OriginalSource) || e.ButtonState != MouseButtonState.Pressed) return;
            if (e.ClickCount == 2) ToggleMaximize();
            else if (WindowState == WindowState.Normal) { try { DragMove(); } catch (InvalidOperationException) { } }
        };
        Closing += (_, e) => { if (!_quitting) { e.Cancel = true; Hide(); } };
        App.Current.Changed += Sync;
        Activated += (_, _) => Sync();
        App.Current.Notice += text => _status.Text = text;
        App.Current.Services.Changed += () => { if (IsVisible) { _refreshGpuPage?.Invoke(); _refreshStage?.Invoke(); } };
        App.Current.WindowsChanged += () => { if (_previewHost != null && IsVisible) _preview.RefreshRunning(); };
        IsVisibleChanged += (_, _) => { if (IsVisible) Navigate(_current); else ClearPage(); };
        Navigate("overview");
    }

    Button WindowButton(string key, string label, Action click)
    {
        var button = Ui.Button("", click);
        button.Style = (Style)FindResource("ConsoleWindowButton");
        button.Content = ConsoleIcons.Draw(key, 17, "#B8FFFFFF");
        button.ToolTip = label;
        AutomationProperties.SetName(button, label);
        button.Margin = new Thickness(0);
        button.ClearValue(Control.PaddingProperty);
        return button;
    }

    void ClearPage()
    {
        if (_previewHost != null) _previewHost.Content = null;
        _previewHost = null; _hero = null; _previewImage = null; _refreshGpuPage = null; _refreshStage = null; _refreshWallpaperPage = null;
        _sync.Clear();
        _page.Children.Clear();
    }

    bool _quitting;
    public void AllowClose() => _quitting = true;
    void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    public void Sync()
    {
        _syncing = true;
        try
        {
            foreach (var action in _sync.ToArray()) action();
            if (_previewHost?.Content != null) ConfigureDockPreview(App.Current.Prefs);
            _refreshStage?.Invoke();
            var prefs = App.Current.Prefs;
            int enabled = prefs.Widgets.Count(p => p.Key != "shelf" && p.Value.Enabled);
            if (prefs.Widgets["shelf"].Enabled) enabled += prefs.ShelfBoxes.Count(b => b.Enabled);
            _topState.Text = $"{enabled} 个组件  ·  Dock {(prefs.Enabled ? "已启用" : "已停用")}";
        }
        finally { _syncing = false; }
    }

    public void Navigate(string page)
    {
        var entry = Pages.FirstOrDefault(p => p.Key == page);
        if (entry.Key == null) entry = Pages[0];
        ClearPage();
        _current = entry.Key;
        foreach (var pair in _navButtons)
        {
            bool active = pair.Key == _current;
            StyleNavButton(pair.Key, pair.Value, active);
        }

        var title = Ui.Text(entry.Title, 30);
        title.FontWeight = FontWeights.SemiBold;
        var description = Ui.Text(entry.Subtitle, 13, "#8FFFFFFF");
        description.Margin = new Thickness(0, 5, 0, 0);
        var heading = Ui.Stack(title, description);
        heading.Margin = new Thickness(0, 10, 0, 16);
        Add(heading);
        switch (_current)
        {
            case "overview": Overview(); break;
            case "components": Components(); break;
            case "dock": DockPage(); break;
            case "island": Island(); break;
            case "spectrum": SpectrumPage(); break;
            case "storage": StoragePage(); break;
            case "wallpaper": WallpaperPage(); break;
            case "performance": PerformancePage(); break;
            case "content": ContentPage(); break;
            case "settings": Settings(); break;
        }
        _scroll.ScrollToTop();
        Sync();
        AnimatePage();
    }
    CheckBox Toggle(Func<Preferences, bool> get, Action<Preferences, bool> set)
    { var c = new CheckBox { Style = (Style)FindResource("ConsoleToggle") }; _sync.Add(() => c.IsChecked = get(App.Current.Prefs)); c.Click += (_, _) => { if (!_syncing) App.Current.Apply(p => set(p, c.IsChecked == true)); }; return c; }
    UIElement Material(Func<Preferences, string> get, Action<Preferences, string> set, bool black = false)
    {
        var panel = new WrapPanel();
        var options = black
            ? new[] { ("液态玻璃", "liquid"), ("磨砂", "frosted"), ("无背景", "none"), ("纯黑", "solid") }
            : new[] { ("液态玻璃", "liquid"), ("磨砂", "frosted"), ("无背景", "none") };
        foreach (var (name, value) in options)
        {
            var button = ChoiceButton(name, () => App.Current.Apply(p => set(p, value)));
            _sync.Add(() => SelectChoice(button, get(App.Current.Prefs) == value));
            panel.Children.Add(button);
        }
        return Segment(panel);
    }
    UIElement Range(string name, int min, int max, Func<Preferences, int> get, Action<Preferences, int> set, string unit)
    {
        var s = new Slider { Minimum = min, Maximum = max, Width = 170, IsSnapToTickEnabled = true, TickFrequency = 1 };
        Action<int> showValue;
        UIElement readout;
        if (unit == " px")
        {
            var input = new TextBox
            {
                Width = 64,
                Margin = new Thickness(10, 0, 4, 0),
                Padding = new Thickness(5, 3, 5, 3),
                TextAlignment = TextAlignment.Right,
                VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip = $"输入 {min}–{max} px，按回车应用"
            };
            AutomationProperties.SetName(input, name + "（px）");
            var suffix = Ui.Text("px", 12);
            readout = Ui.Actions(input, suffix);
            showValue = number =>
            {
                if (!input.IsKeyboardFocused) input.Text = number.ToString();
            };
            void CommitInput()
            {
                if (_syncing) return;
                if (int.TryParse(input.Text, out int number) && number >= min && number <= max)
                {
                    if (number != get(App.Current.Prefs)) App.Current.Apply(p => set(p, number));
                }
                else App.Current.Notify($"{name}请输入 {min}–{max} 之间的整数");
                input.Text = get(App.Current.Prefs).ToString();
            }
            input.LostKeyboardFocus += (_, _) => CommitInput();
            input.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) CommitInput();
                else if (e.Key == Key.Escape) input.Text = get(App.Current.Prefs).ToString();
                else return;
                e.Handled = true;
            };
        }
        else
        {
            var label = Ui.Text("", 12);
            label.Width = 58;
            label.TextAlignment = TextAlignment.Right;
            readout = label;
            showValue = number => label.Text = number + unit;
        }
        _sync.Add(() => { s.Value = get(App.Current.Prefs); showValue((int)s.Value); });
        s.ValueChanged += (_, _) => { showValue((int)s.Value); if (!_syncing && _previewHost != null) { var draft = App.Current.Prefs.Clone(); set(draft, (int)s.Value); ConfigureDockPreview(draft); } };
        void Commit() { if (!_syncing && get(App.Current.Prefs) != (int)s.Value) App.Current.Apply(p => set(p, (int)s.Value)); }
        s.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => Commit())); s.PreviewMouseLeftButtonUp += (_, _) => Commit(); s.KeyUp += (_, _) => Commit(); s.LostKeyboardFocus += (_, _) => Commit();
        return Row(name, Ui.Actions(s, readout));
    }
    void Add(UIElement element) => _page.Children.Add(element);
    void Overview()
    {
        AddOverviewStage();
        var manage = Ui.Button("管理桌面组件", () => Navigate("components"));
        manage.Background = Paper;
        var manageLabel = Ui.Text("管理桌面组件", 13, "#17202B");
        manageLabel.FontWeight = FontWeights.SemiBold;
        manage.Content = manageLabel;
        AutomationProperties.SetName(manage, manageLabel.Text);
        var actions = Ui.Actions(manage, Ui.Button("返回桌面", Hide));
        actions.Margin = new Thickness(-3, 0, 0, 12);
        Add(actions);

        var shortcuts = new Grid { Margin = new Thickness(0, 0, 0, 13) };
        var destinations = new[] { ("dock", "Dock 与启动台", "排列常用应用与运行区域"), ("storage", "桌面收纳", "调整盒子、图标与分隔线"), ("performance", "性能监控", "查看显卡与显存读数") };
        for (int i = 0; i < destinations.Length; i++)
        {
            var (key, label, detail) = destinations[i];
            shortcuts.ColumnDefinitions.Add(new ColumnDefinition());
            var symbol = ConsoleIcons.Draw(key, 20, "#E0FFFFFF");
            symbol.HorizontalAlignment = HorizontalAlignment.Left;
            symbol.Margin = new Thickness(0, 0, 0, 9);
            var text = Ui.Text(label, 15);
            text.FontWeight = FontWeights.SemiBold;
            var subtext = Ui.Text(detail, 12, "#8FFFFFFF");
            subtext.Margin = new Thickness(0, 5, 0, 0);
            var button = Ui.Button("", () => Navigate(key));
            button.Content = Ui.Stack(symbol, text, subtext);
            button.HorizontalContentAlignment = HorizontalAlignment.Left;
            button.Margin = new Thickness(0, 0, i == destinations.Length - 1 ? 0 : 12, 0);
            button.Padding = new Thickness(15, 12, 10, 12);
            button.MinHeight = 108;
            button.Background = Ui.Brush("#9E10141C");
            button.BorderBrush = Edge;
            AutomationProperties.SetName(button, label);
            Grid.SetColumn(button, i);
            shortcuts.Children.Add(button);
        }
        Add(shortcuts);
        Add(Card(Ui.Stack(
            Row("Dock 栏", Toggle(p => p.Enabled, (p, v) => p.Enabled = v), "从桌面打开常用应用"),
            Row("拖动吸附", Toggle(p => p.SnapEnabled, (p, v) => p.SnapEnabled = v), "拖动组件时与边缘和相邻组件对齐"))));
    }

    void PerformancePage()
    {
        var scene = new Grid();
        var plate = PerformancePreview(overview: false);
        plate.HorizontalAlignment = HorizontalAlignment.Left;
        plate.Margin = new Thickness(36, 0, 0, 0);
        scene.Children.Add(plate);
        Add(Stage(scene, 210));
        var adapterButtons = new WrapPanel { Margin = new Thickness(0, 12, 0, 3) };
        var info = Ui.Text("", 13, "#A7BCCF");
        var reading = Ui.Text("", 29);
        reading.FontWeight = FontWeights.SemiBold;
        var gpuName = Ui.Text("", 16);
        Add(Card(Ui.Stack(
            Row("在桌面显示性能组件", Toggle(p => p.Widgets["performance"].Enabled, (p, v) => p.Widgets["performance"].Enabled = v)),
            Row("材质", Material(p => p.Widgets["performance"].Material, (p, v) => p.Widgets["performance"].Material = v)))));
        Add(Card(Ui.Stack(Ui.Text("显卡读数", 17), gpuName, reading, info)));
        Add(Card(Ui.Stack(Ui.Text("显卡来源", 17), Ui.Text("Windows 性能计数器 · 自动选择当前最忙的显卡", 12, "#A7BCCF"), adapterButtons)));

        string? buttonSignature = null;
        void Refresh()
        {
            var services = App.Current.Services;
            var gpu = services.SelectedGpu;
            gpuName.Text = gpu?.Name ?? "等待显卡数据";
            reading.Text = gpu?.Usage is double usage ? $"{usage:0}%  GPU" : "—  GPU";
            info.Text = gpu?.DedicatedBytes is long bytes
                ? $"专用显存 {bytes / 1073741824.0:0.00} GB" + (gpu.CapacityBytes > 0 ? $" / {gpu.CapacityBytes / 1073741824.0:0.00} GB" : "")
                : "等待专用显存读数";

            string selected = App.Current.Prefs.GpuAdapterId ?? "";
            string signature = selected + "|" + string.Join("|", services.GpuReadings.Select(g => g.Id + g.Name));
            if (signature == buttonSignature) return;
            buttonSignature = signature;
            adapterButtons.Children.Clear();
            void AddChoice(string id, string title)
            {
                var button = ChoiceButton(title, () => App.Current.Apply(p => p.GpuAdapterId = id));
                SelectChoice(button, id == selected);
                adapterButtons.Children.Add(button);
            }
            AddChoice("", "自动选择");
            foreach (var adapter in services.GpuReadings) AddChoice(adapter.Id, adapter.Name);
        }
        _refreshGpuPage = Refresh;
        _sync.Add(Refresh);
        App.Current.Services.RequestGpuSample();
    }
    void Components()
    {
        Add(Ui.Actions(Ui.Button("全部锁定", () => LockAll(true)), Ui.Button("全部解锁", () => LockAll(false)), Ui.Button("恢复默认位置", ResetPositions)));
        var styles = new WrapPanel();
        foreach (var style in WidgetStyles.Clock)
        {
            var button = ChoiceButton(style.Name, () => App.Current.Apply(p => p.ClockStyle = style.Key));
            _sync.Add(() => SelectChoice(button, App.Current.Prefs.ClockStyle == style.Key));
            styles.Children.Add(button);
        }
        var preview = new RainmeterClockPreview();
        var color = Ui.Button("选择颜色", () =>
        {
            var current = App.Current.Prefs.ClockTextColor ?? "#FFFFFF";
            using var dialog = new System.Windows.Forms.ColorDialog
            {
                FullOpen = true,
                Color = System.Drawing.ColorTranslator.FromHtml(current)
            };
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            var owner = System.Windows.Forms.NativeWindow.FromHandle(hwnd);
            if (dialog.ShowDialog(owner) == System.Windows.Forms.DialogResult.OK)
                App.Current.Apply(p => p.ClockTextColor = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}");
        });
        var clockAppearance = Ui.Stack(
            new Border { Background = Ui.Brush("#151E28"), CornerRadius = new CornerRadius(10), Child = preview },
            Row("文字颜色", Ui.Actions(color, Ui.Button("恢复原色", () => App.Current.Apply(p => p.ClockTextColor = null)))),
            Range("文字不透明度", 0, 100, p => p.ClockOpacity, (p, v) => p.ClockOpacity = v, "%"));
        _sync.Add(() =>
        {
            var prefs = App.Current.Prefs;
            clockAppearance.Visibility = RainmeterClock.IsStyle(prefs.ClockStyle) ? Visibility.Visible : Visibility.Collapsed;
            color.Content = prefs.ClockTextColor ?? "选择颜色";
            preview.InvalidateVisual();
        });
        Add(Card(Ui.Stack(Ui.Text("时钟样式", 16), Segment(styles), clockAppearance, Ui.Button("管理多个收纳盒", () => Navigate("storage")))));
        var widgets = new StackPanel();
        foreach (var pair in WidgetPreference.Names)
        {
            string id = pair.Key;
            var enabled = Toggle(p => p.Widgets[id].Enabled, (p, v) => p.Widgets[id].Enabled = v);
            AutomationProperties.SetName(enabled, pair.Value);
            var name = Ui.Text(pair.Value, 14);
            name.FontWeight = FontWeights.SemiBold;
            name.Margin = new Thickness(0, 0, 0, 6);
            var controls = Ui.Stack(name);
            if (id == "shelf")
            {
                controls.Children.Add(Ui.Text("每个收纳盒单独设置材质、尺寸与锁定。", 12, "#8FFFFFFF"));
                controls.Children.Add(Ui.Actions(Ui.Button("打开收纳盒设置", () => Navigate("storage"))));
            }
            else
            {
                controls.Children.Add(Material(p => p.Widgets[id].Material, (p, v) => p.Widgets[id].Material = v, id == "island"));
                if (id != "island")
                {
                    var locked = Toggle(p => p.Widgets[id].Locked, (p, v) => p.Widgets[id].Locked = v);
                    AutomationProperties.SetName(locked, "锁定" + pair.Value);
                    var lockLabel = Ui.Text("锁定位置", 12, "#8FFFFFFF");
                    lockLabel.Margin = new Thickness(0, 0, 12, 0);
                    var lockRow = Ui.Actions(lockLabel, locked);
                    lockRow.Margin = new Thickness(0, 8, 0, 0);
                    controls.Children.Add(lockRow);
                }
            }
            var row = new DockPanel { Margin = new Thickness(0, 10, 0, 10) };
            DockPanel.SetDock(enabled, System.Windows.Controls.Dock.Right);
            enabled.VerticalAlignment = VerticalAlignment.Top;
            enabled.Margin = new Thickness(16, 0, 0, 0);
            row.Children.Add(enabled);
            row.Children.Add(controls);
            if (widgets.Children.Count > 0)
                widgets.Children.Add(new Border { Height = 1, Background = Ui.Brush("#19FFFFFF") });
            widgets.Children.Add(row);
        }
        Add(Card(widgets));
    }
    void DockPage()
    {
        var scene = new Grid();
        AddDockPreview(scene);
        _hero = Stage(scene, 292);
        Add(_hero);
        _hero.SizeChanged += (_, _) => _previewImage = null;
        Add(Card(Ui.Stack(Row("启用 Dock", Toggle(p => p.Enabled, (p, v) => p.Enabled = v)), Row("材质", Material(p => p.Material, (p, v) => p.Material = v)), Range("图标大小", 50, 120, p => p.IconScale, (p, v) => p.IconScale = v, "%"), Range("图标间距", 4, 24, p => p.IconSpacing, (p, v) => p.IconSpacing = v, " px"), Range("距离底部上移", 0, 160, p => p.BottomOffset, (p, v) => p.BottomOffset = v, " px"), Row("显示右侧运行中应用", Toggle(p => p.ShowRunningApps, (p, v) => p.ShowRunningApps = v)), Row("图标倒影", Toggle(p => p.Reflection, (p, v) => p.Reflection = v)))));
        Add(Card(Ui.Stack(Ui.Text("启动台", 17), Row("在 Dock 中显示启动台", Toggle(p => p.ShowLaunchpad, (p, v) => p.ShowLaunchpad = v)), Row("材质", Material(p => p.LaunchpadMaterial, (p, v) => p.LaunchpadMaterial = v)), Range("面板缩放", 60, 140, p => p.LaunchpadScale, (p, v) => p.LaunchpadScale = v, "%"), Range("图标大小", 50, 120, p => p.LaunchpadIconScale, (p, v) => p.LaunchpadIconScale = v, "%"), Ui.Button("打开启动台 ↗", () => App.Current.ShowLaunchpad()))));
        var pins = new StackPanel(); Add(Card(Ui.Stack(Ui.Text("固定应用", 17), Ui.Actions(Ui.Button("＋ 添加文件 / 应用", AddFile), Ui.Button("添加文件夹", AddFolder)), pins)));
        string signature = ""; _sync.Add(() => { string next = string.Join("|", App.Current.Prefs.Pins.Select(p => p.Id)); if (next == signature) return; signature = next; pins.Children.Clear(); foreach (var pin in App.Current.Prefs.Pins) { var icon = new Image { Source = Icons.Get(pin.Path, pin.Name), Width = 30, Height = 30, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 10, 0) }; var actions = Ui.Actions(Ui.Button("↑", () => MovePin(pin.Id, -1)), Ui.Button("↓", () => MovePin(pin.Id, 1)), Ui.Button("移除", () => App.Current.Apply(p => p.Pins.RemoveAll(x => x.Id == pin.Id)))); var row = Row(pin.Name, actions); row.Children.Add(icon); ((StackPanel)row.Children[0]).Margin = new Thickness(40, 0, 10, 0); pins.Children.Add(row); } });
    }
    GlassInput? PreviewInput()
    {
        if (_hero == null || _previewHost == null || _hero.ActualWidth < 10) return null;
        if (_previewImage == null) { var visibility = _previewHost.Visibility; _previewHost.Visibility = Visibility.Hidden; try { var dpi = VisualTreeHelper.GetDpi(_hero); var bitmap = new RenderTargetBitmap((int)Math.Ceiling(_hero.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(_hero.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32); bitmap.Render(_hero); var data = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(data, bitmap.PixelWidth * 4, 0); _previewImage = new GlassInput(data, bitmap.PixelWidth, bitmap.PixelHeight, 0, 0, ++_revision); } finally { _previewHost.Visibility = visibility; } }
        var point = _hero.PointToScreen(new Point()); return _previewImage with { Left = (int)point.X, Top = (int)point.Y };
    }
    void Island()
    {
        var scene = new Grid();
        scene.Children.Add(IslandPreview(overview: false));
        Add(Stage(scene, 180));
        Add(Card(Ui.Stack(
            Row("显示灵动岛", Toggle(p => p.Widgets["island"].Enabled, (p, v) => p.Widgets["island"].Enabled = v)),
            Row("材质", Material(p => p.Widgets["island"].Material, (p, v) => p.Widgets["island"].Material = v, true)),
            Ui.Text("悬停展开，移开收回。播放本地音乐或有曲目信息的媒体时显示曲名和音量条。滚轮切换音乐、日历、待办、天气、时钟和专注。", 12, "#8FFFFFFF"))));
    }
    void SpectrumPage()
    {
        var scene = new Grid();
        var wave = new SpectrumVisual { Width = 300, Height = 105 };
        var plate = PreviewPlate(wave, new Thickness(18, 10, 18, 10));
        plate.VerticalAlignment = VerticalAlignment.Bottom;
        plate.Margin = new Thickness(0, 0, 0, 36);
        scene.Children.Add(plate);
        Add(Stage(scene, 292));
        var samples = new[] { .28, .46, .70, .92, .64, .80, .48, .66, .38, .58, .84, .50, .34, .62, .44 };
        _sync.Add(() =>
        {
            PreviewMaterial(plate, App.Current.Prefs.Widgets["spectrum"].Material);
            wave.InvalidateVisual();
        });
        void UpdateSpectrumPreview()
        {
            wave.VerificationAmplitudes = App.Current.Spectrum.Active ? null : samples;
            wave.InvalidateVisual();
        }
        wave.Loaded += (_, _) => { App.Current.Spectrum.Changed += UpdateSpectrumPreview; UpdateSpectrumPreview(); };
        wave.Unloaded += (_, _) => App.Current.Spectrum.Changed -= UpdateSpectrumPreview;
        Add(Card(Ui.Stack(
            Row("显示音频频谱", Toggle(p => p.Widgets["spectrum"].Enabled, (p, v) => p.Widgets["spectrum"].Enabled = v)),
            Row("实时频谱", Toggle(p => p.Spectrum.LiveEnabled, (p, v) => p.Spectrum.LiveEnabled = v), "显示组件时自动采集所选音源"),
            Ui.Actions(Ui.Button("使用系统声音", () => App.Current.Apply(p => p.Spectrum.Source = "system")),
                Ui.Button("使用麦克风", () => App.Current.Apply(p => p.Spectrum.Source = "microphone"))),
            Row("材质", Material(p => p.Widgets["spectrum"].Material, (p, v) => p.Widgets["spectrum"].Material = v)),
            Row("锁定位置", Toggle(p => p.Widgets["spectrum"].Locked, (p, v) => p.Widgets["spectrum"].Locked = v)),
            Ui.Text("右键可切换样式与配色；静音隐藏使用双阈值噪声门、保持和淡出，不会改动系统音量。", 12, "#98AAC2"),
            Ui.Actions(Ui.Button("彩虹配色", () => App.Current.Apply(p => p.Spectrum.Color = "rainbow")),
                Ui.Button("白色配色", () => App.Current.Apply(p => p.Spectrum.Color = "white"))),
            Row("静音时隐藏频谱", Toggle(p => p.Spectrum.HideWhenSilent, (p, v) => p.Spectrum.HideWhenSilent = v)),
            Range("静音阈值", -90, -30, p => p.Spectrum.SilenceThresholdDb, (p, v) => p.Spectrum.SilenceThresholdDb = v, " dB"),
            Ui.Actions(WidgetStyles.Spectrum.Select(style => Ui.Button(style.Name, () => App.Current.Apply(p => p.Spectrum.Style = style.Key))).ToArray()),
            Range("条宽", 2, 24, p => p.Spectrum.BarWidth, (p, v) => p.Spectrum.BarWidth = v, " px"),
            Range("间距", 0, 12, p => p.Spectrum.BarGap, (p, v) => p.Spectrum.BarGap = v, " px"))));
    }
    string? _storageId;
    void StoragePage()
    {
        var boxes = App.Current.Prefs.ShelfBoxes;
        if (!boxes.Any(b => b.Id == _storageId)) _storageId = boxes.FirstOrDefault()?.Id;
        if (boxes.Count > 0)
        {
            var scene = new Grid();
            scene.Children.Add(ShelfPreview(p => p.ShelfBoxes.Find(box => box.Id == _storageId), overview: false));
            Add(Stage(scene, 210));
        }
        Add(Card(Ui.Stack(Row("显示桌面收纳盒", Toggle(p => p.Widgets["shelf"].Enabled, (p, v) => p.Widgets["shelf"].Enabled = v)), Ui.Text($"{boxes.Count} / 20 个收纳盒 · 文件保留在原位置", 12, "#98AAC2"), Ui.Button("＋ 新建收纳盒", () => { if (boxes.Count >= 20) return; var box = new ShelfBoxPreference { Title = "收纳盒 " + (boxes.Count + 1) }; _storageId = box.Id; App.Current.Apply(p => { p.ShelfBoxes.Add(box); p.Widgets["shelf"].Enabled = true; }); Navigate("storage"); }))));
        if (boxes.Count == 0) { Add(Ui.Text("新建一个收纳盒，再拖入应用、文件或文件夹。", 13, "#98AAC2")); return; }
        string id = _storageId!; ShelfBoxPreference Get(Preferences p) => p.ShelfBoxes.First(b => b.Id == id);
        var select = new WrapPanel();
        foreach (var box in boxes)
        {
            string boxId = box.Id;
            var button = ChoiceButton(box.Title.Length == 0 ? "未命名收纳盒" : box.Title, () => { _storageId = boxId; Navigate("storage"); });
            SelectChoice(button, boxId == id);
            select.Children.Add(button);
        }

        var title = new TextBox { Text = Get(App.Current.Prefs).Title, Width = 220, MaxLength = 60 };
        Add(Card(Ui.Stack(Ui.Text("选择收纳盒", 14), select, Ui.Actions(title, Ui.Button("保存标题", () => { App.Current.Apply(p => Get(p).Title = title.Text.Trim()); Navigate("storage"); })), Row("显示此盒", Toggle(p => Get(p).Enabled, (p, v) => Get(p).Enabled = v)), Row("显示标题", Toggle(p => Get(p).ShowTitle, (p, v) => Get(p).ShowTitle = v)), Row("显示横向分隔线", Toggle(p => Get(p).ShowDivider, (p, v) => Get(p).ShowDivider = v)), Row("材质", Material(p => Get(p).Material, (p, v) => Get(p).Material = v)), Row("锁定位置", Toggle(p => Get(p).Locked, (p, v) => Get(p).Locked = v)), Range("收纳盒宽度", 160, 900, p => Get(p).Width, (p, v) => Get(p).Width = v, " px"), Range("收纳盒高度", 100, 700, p => Get(p).Height, (p, v) => Get(p).Height = v, " px"), Range("内部图标大小", 20, 80, p => Get(p).IconSize, (p, v) => Get(p).IconSize = v, " px"), Ui.Actions(Ui.Button("添加文件", () => App.Current.Desktop.AddShelfDialog(id)), Ui.Button("添加文件夹", () => { var dialog = new OpenFolderDialog { Multiselect = true }; if (dialog.ShowDialog(this) == true) App.Current.Desktop.AddShelf(dialog.FolderNames, id); }), Ui.Button("删除此收纳盒", () => { Navigate("overview"); App.Current.Apply(p => p.ShelfBoxes.RemoveAll(b => b.Id == id)); Navigate("storage"); })))));
        var items = new StackPanel(); Add(Card(Ui.Stack(Ui.Text("收纳项目", 16), items))); string signature = "?";
        _sync.Add(() => { string sig = string.Join("|", Get(App.Current.Prefs).Pins.Select(p => p.Id)); if (sig == signature) return; signature = sig; items.Children.Clear(); foreach (var pin in Get(App.Current.Prefs).Pins) items.Children.Add(Row(pin.Name, Ui.Actions(Ui.Button("打开", () => App.Current.Launch(pin)), Ui.Button("移除", () => App.Current.Apply(p => Get(p).Pins.RemoveAll(x => x.Id == pin.Id)))))); });
    }
    void ContentPage()
    {
        var city = new TextBox { Text = App.Current.Prefs.WeatherCity, Width = 180 }; Add(Card(Ui.Stack(Ui.Text("天气位置", 17), Ui.Actions(city, Ui.Button("更新城市", async () => { try { _status.Text = await App.Current.Services.SetWeatherCity(city.Text) ? "城市已更新" : "未找到城市"; } catch (Exception ex) { _status.Text = "位置更新失败：" + ex.Message; } })))));
        var todoList = new StackPanel();
        Add(Card(Ui.Stack(Row("待办事项", Ui.Button("添加待办", () => App.Current.AddTodo(this))), todoList)));
        string todoSig = ""; _sync.Add(() => { string sig = string.Join("|", App.Current.Prefs.Todos.Select(t => t.Id + t.Done)); if (sig == todoSig) return; todoSig = sig; todoList.Children.Clear(); foreach (var t in App.Current.Prefs.Todos) { var c = new CheckBox { Content = t.Date + "  " + t.Text, IsChecked = t.Done, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center }; c.Click += (_, _) => App.Current.Apply(p => p.Todos.First(x => x.Id == t.Id).Done = c.IsChecked == true); todoList.Children.Add(Row("", Ui.Actions(c, Ui.Button("删除", () => App.Current.Apply(p => p.Todos.RemoveAll(x => x.Id == t.Id)))))); } });
        Add(Card(Ui.Stack(Ui.Text("桌面收纳", 17), Ui.Button("管理收纳盒", () => Navigate("storage")))));
        Add(Card(Ui.Stack(Ui.Text("本地音乐", 17), Ui.Text("选择本地音频后，可由桌面音乐卡片和灵动岛控制播放。", 12, "#98AAC2"), Ui.Actions(Ui.Button("选择音乐", () => App.Current.Services.OpenMusic()), Ui.Button("上一首", () => App.Current.Services.Next(-1)), Ui.Button("播放 / 暂停", () => App.Current.Services.ToggleMusic()), Ui.Button("下一首", () => App.Current.Services.Next(1))))));
    }
    void Settings()
    {
        var app = App.Current;
        var taskbar = new CheckBox { Style = (Style)FindResource("ConsoleToggle") };
        AutomationProperties.SetName(taskbar, "任务栏按需显示");
        var startup = new CheckBox { Style = (Style)FindResource("ConsoleToggle") };
        AutomationProperties.SetName(startup, "开机自动启动");
        var guardState = Ui.Text("", 12, "#A5DBD3");
        var startupState = Ui.Text("", 11, "#9BADBF");
        var updateStartup = Ui.Button("将自启动更新到当前版本", () => app.SetStartupEnabled(true));
        _sync.Add(() =>
        {
            taskbar.IsChecked = app.TaskbarGuard.Running; taskbar.IsEnabled = !app.TaskbarBusy;
            guardState.Text = app.TaskbarBusy ? "正在应用任务栏设置…" : app.TaskbarGuard.Status;
            try
            {
                startup.IsChecked = app.WindowsStartup.Enabled;
                bool previous = app.WindowsStartup.Enabled && !app.WindowsStartup.UsesCurrentCopy;
                startupState.Text = previous ? "已有其他目录的澄屿启动项，可更新到当前版本。" : "登录 Windows 后显示桌面组件，在托盘运行，双击托盘图标可打开管理界面。";
                updateStartup.Visibility = previous ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception ex) { startup.IsChecked = false; startupState.Text = "无法读取系统自启动设置：" + ex.Message; }
        });
        taskbar.Click += async (_, _) => { if (!_syncing) await app.SetTaskbarEnabledAsync(taskbar.IsChecked == true); };
        startup.Click += (_, _) => { if (!_syncing) app.SetStartupEnabled(startup.IsChecked == true); };
        Add(Card(Ui.Stack(Ui.Text("启动与任务栏", 17),
            Row("开机自动启动", startup), startupState, updateStartup,
            Row("任务栏按需显示", taskbar, "遮挡意外弹出的任务栏；鼠标触底或按 Win 键时正常唤出"), guardState,
            Ui.Text("需开启 Windows「自动隐藏任务栏」。关闭此开关或退出澄屿会恢复任务栏。", 11, "#9BADBF"),
            Ui.Button("打开 Windows 任务栏设置", () => Process.Start(new ProcessStartInfo("ms-settings:taskbar") { UseShellExecute = true })))));
        Add(Card(Ui.Stack(Ui.Text("桌面图标", 17), Row("双击桌面空白处切换显隐", Toggle(p => p.DesktopIcons.DoubleClick, (p, v) => p.DesktopIcons.DoubleClick = v), "识别桌面空白区域，双击图标仍正常打开"), Row("空闲自动隐藏", Toggle(p => p.DesktopIcons.AutoHide, (p, v) => p.DesktopIcons.AutoHide = v)), Range("空闲等待", 3, 60, p => p.DesktopIcons.DelaySeconds, (p, v) => p.DesktopIcons.DelaySeconds = v, " 秒"), Row("输入后恢复自动隐藏的图标", Toggle(p => p.DesktopIcons.RestoreOnInput, (p, v) => p.DesktopIcons.RestoreOnInput = v), "手动双击隐藏时保持隐藏，退出澄屿时恢复原状态"), Ui.Button("立即显示 / 隐藏图标", () => App.Current.DesktopIcons.Toggle()))));
        Add(Card(Ui.Stack(Ui.Text("截图与录屏", 16), Ui.Text("截图和录屏默认包含组件。玻璃独立采样壁纸，无需开启临时截图模式。", 12, "#98AAC2"))));
        Add(Card(Ui.Stack(Row("Dock 始终置顶", Toggle(p => p.DockTopmost, (p, v) => p.DockTopmost = v), "默认关闭；关闭时与其他组件一起留在桌面底层"), Row("拖动吸附对齐", Toggle(p => p.SnapEnabled, (p, v) => p.SnapEnabled = v), "同侧对齐 → 相邻间距 → 工作区边缘，按距离择优"), Row("减少动画", Toggle(p => p.ReducedMotion, (p, v) => p.ReducedMotion = v)), Ui.Actions(Ui.Button("锁定全部组件", () => LockAll(true)), Ui.Button("解锁全部组件", () => LockAll(false)), Ui.Button("恢复组件位置", ResetPositions)))));
        Add(Card(Ui.Stack(Ui.Text("数据保存在本机", 16), Ui.Text(App.Current.Store.Root, 12, "#91A5BD"), Ui.Button("打开数据文件夹", () => Process.Start(new ProcessStartInfo(App.Current.Store.Root) { UseShellExecute = true })))));
    }
    void LockAll(bool value) => App.Current.Apply(p => { foreach (var item in p.Widgets.Values) item.Locked = value; foreach (var box in p.ShelfBoxes) box.Locked = value; });
    void ResetPositions() => App.Current.Apply(p => { foreach (var item in p.Widgets.Values) { item.X = null; item.Y = null; } foreach (var box in p.ShelfBoxes) { box.X = null; box.Y = null; } });
    void MovePin(string id, int step) => App.Current.Apply(p => { int i = p.Pins.FindIndex(x => x.Id == id), j = i + step; if (i < 0 || j < 0 || j >= p.Pins.Count) return; var item = p.Pins[i]; p.Pins.RemoveAt(i); p.Pins.Insert(j, item); });
    void AddFile() { var d = new OpenFileDialog { Multiselect = true, Filter = "所有应用与文件|*.*" }; if (d.ShowDialog(this) == true) App.Current.AddPaths(d.FileNames); }
    void AddFolder() { var d = new OpenFolderDialog { Multiselect = true }; if (d.ShowDialog(this) == true) App.Current.AddPaths(d.FolderNames); }
}
internal static class BrushExtension { internal static Color ToColor(this Brush brush) => ((SolidColorBrush)brush).Color; }
