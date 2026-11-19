using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Ellipse = System.Windows.Shapes.Ellipse;

namespace ChengyuDock;

public sealed partial class ManagerWindow
{
    BitmapSource? _wallpaperImage;
    string? _wallpaperPath;
    readonly TranslateTransform _pageOffset = new();
    static readonly Brush Paper = Ui.Brush("#EBFFFFFF");
    static readonly Brush Ink = Ui.Brush("#17202B");
    static readonly Brush Edge = Ui.Brush("#29FFFFFF");
    static readonly Brush MutedText = Ui.Brush("#8FFFFFFF");

    BitmapSource? PreviewWallpaper()
    {
        var setting = App.Current.Prefs.Wallpaper;
        string bundled = Path.Combine(AppContext.BaseDirectory, "Assets", "midnight.png");
        string path = setting.Kind == "image" && File.Exists(setting.Path) ? setting.Path : bundled;
        if (path == _wallpaperPath) return _wallpaperImage;
        _wallpaperPath = path;
        try { _wallpaperImage = WallpaperService.Thumbnail(path, 1280); }
        catch (Exception ex) when (ex is IOException or NotSupportedException or FormatException or UnauthorizedAccessException)
        { _wallpaperImage = path == bundled ? null : WallpaperService.Thumbnail(bundled, 1280); }
        return _wallpaperImage;
    }

    void AddConsoleBackdrop(Grid root)
    {
        var wallpaper = new Image
        {
            Stretch = Stretch.UniformToFill,
            Margin = new Thickness(-25),
            Effect = new BlurEffect { Radius = 28, RenderingBias = RenderingBias.Performance }
        };
        void Refresh() => wallpaper.Source = IsVisible ? PreviewWallpaper() : null;
        App.Current.Changed += Refresh;
        IsVisibleChanged += (_, _) => Refresh();
        var backdrop = new Grid { Background = Ui.Brush("#080B10"), IsHitTestVisible = false, ClipToBounds = true };
        backdrop.Children.Add(wallpaper);
        backdrop.Children.Add(new Border
        {
            Background = new LinearGradientBrush(Ui.Brush("#AC080B10").ToColor(), Ui.Brush("#DB080B10").ToColor(), 90)
        });
        backdrop.Children.Add(Highlight(56, "#7AFFFFFF"));
        Grid.SetRowSpan(backdrop, 3);
        root.Children.Add(backdrop);
        root.SizeChanged += (_, _) =>
        {
            double radius = WindowState == WindowState.Maximized ? 0 : 27;
            root.Clip = new RectangleGeometry(new Rect(root.RenderSize), radius, radius);
        };
        StateChanged += (_, _) =>
        {
            double radius = WindowState == WindowState.Maximized ? 0 : 27;
            root.Clip = new RectangleGeometry(new Rect(root.RenderSize), radius, radius);
            if (Content is Border frame) frame.CornerRadius = new CornerRadius(radius == 0 ? 0 : 28);
        };
    }

    StackPanel StatusPill()
    {
        var status = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var dot = new Ellipse
        {
            Width = 6, Height = 6, Fill = Ui.Brush("#30D158"), Margin = new Thickness(0, 0, 8, 0),
            Effect = new DropShadowEffect { Color = Color.FromRgb(48, 209, 88), BlurRadius = 8, ShadowDepth = 0, Opacity = .75 }
        };
        status.Children.Add(dot);
        status.Children.Add(_topState);
        return status;
    }

    void UpdateRail(Grid shell, FrameworkElement version)
    {
        bool compact = ActualWidth < 980;
        if ((shell.ColumnDefinitions[0].Width.Value == 72) == compact) return;
        shell.ColumnDefinitions[0].Width = new GridLength(compact ? 72 : 216);
        version.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        foreach (var item in _nav.Children.OfType<TextBlock>()) item.Visibility = version.Visibility;
        foreach (var button in _navButtons.Values)
        {
            var row = (StackPanel)button.Content;
            row.Children[1].Visibility = version.Visibility;
            ((FrameworkElement)row.Children[0]).Margin = compact ? new Thickness(0) : new Thickness(0, 0, 10, 0);
            button.HorizontalContentAlignment = compact ? HorizontalAlignment.Center : HorizontalAlignment.Left;
            button.Padding = compact ? new Thickness(0) : new Thickness(12, 0, 8, 0);
        }
        _page.Margin = new Thickness(compact ? 18 : 28, 8, compact ? 18 : 28, 22);
    }

    static void StyleNavButton(string key, Button button, bool selected)
    {
        button.Background = selected ? Paper : Brushes.Transparent;
        button.BorderBrush = Brushes.Transparent;
        var row = (StackPanel)button.Content;
        var label = (TextBlock)row.Children[1];
        label.Foreground = selected ? Ink : Ui.Brush("#D1FFFFFF");
        label.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
        ((Image)row.Children[0]).Source = ConsoleIcons.Draw(key, 16, selected ? "#17202B" : "#D1FFFFFF").Source;
    }

    void AnimatePage()
    {
        if (App.Current.Prefs.ReducedMotion || !SystemParameters.ClientAreaAnimation) return;
        _page.RenderTransform = _pageOffset;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        _page.BeginAnimation(OpacityProperty, new DoubleAnimation(.25, 1, TimeSpan.FromMilliseconds(230)) { EasingFunction = ease });
        _pageOffset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(6, 0, TimeSpan.FromMilliseconds(280)) { EasingFunction = ease });
    }

    static Border Highlight(double inset, string color) => new()
    {
        Height = 1,
        Margin = new Thickness(inset, 0, inset, 0),
        VerticalAlignment = VerticalAlignment.Top,
        IsHitTestVisible = false,
        Background = new LinearGradientBrush(new GradientStopCollection
        {
            new(Colors.Transparent, 0), new(Ui.Brush(color).ToColor(), .5), new(Colors.Transparent, 1)
        }, 0)
    };

    static Border Card(UIElement child)
    {
        var surface = new Grid();
        surface.Children.Add(new Border { Padding = new Thickness(18, 10, 18, 10), Child = child });
        surface.Children.Add(Highlight(22, "#66FFFFFF"));
        return new Border
        {
            CornerRadius = new CornerRadius(22), BorderBrush = Edge, BorderThickness = new Thickness(1),
            Background = Ui.Brush("#9E10141C"), Margin = new Thickness(0, 0, 0, 12), Child = surface
        };
    }

    static Grid Row(string name, UIElement control, string detail = "")
    {
        var row = Ui.Row(name, control, detail);
        row.Margin = new Thickness(0);
        row.MinHeight = 58;
        var label = (StackPanel)row.Children[0];
        label.Margin = new Thickness(0, 8, 18, 8);
        label.VerticalAlignment = VerticalAlignment.Center;
        ((TextBlock)label.Children[0]).FontWeight = FontWeights.SemiBold;
        if (label.Children.Count > 1)
        {
            var description = (TextBlock)label.Children[1];
            description.Foreground = MutedText;
            description.FontSize = 12;
        }
        if (control is FrameworkElement element) element.VerticalAlignment = VerticalAlignment.Center;
        if (control is CheckBox) System.Windows.Automation.AutomationProperties.SetName(control, name);
        var separator = new Border { Height = 1, Background = Ui.Brush("#19FFFFFF"), VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false };
        Grid.SetColumnSpan(separator, 2);
        row.Children.Add(separator);
        return row;
    }

    Button ChoiceButton(string name, Action choose)
    {
        var button = Ui.Button(name, choose);
        button.Style = (Style)FindResource("ConsoleChoice");
        button.Content = Ui.Text(name, 13, "#BDFFFFFF");
        System.Windows.Automation.AutomationProperties.SetName(button, name);
        button.ClearValue(Control.PaddingProperty);
        button.Margin = new Thickness(0);
        return button;
    }

    static void SelectChoice(Button button, bool selected)
    {
        button.Background = selected ? Paper : Brushes.Transparent;
        button.Foreground = selected ? Ink : Ui.Brush("#BDFFFFFF");
        button.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
        if (button.Content is TextBlock label)
        {
            label.Foreground = button.Foreground;
            label.FontWeight = button.FontWeight;
        }
    }

    static Border Segment(WrapPanel options) => new()
    {
        HorizontalAlignment = HorizontalAlignment.Left,
        Background = Ui.Brush("#73080A10"), BorderBrush = Ui.Brush("#1FFFFFFF"), BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(18), Padding = new Thickness(3), Child = options
    };

    Border Stage(Grid scene, double height)
    {
        var backdrop = new ImageBrush(PreviewWallpaper()) { Stretch = Stretch.UniformToFill };
        scene.Background = backdrop;
        scene.SizeChanged += (_, _) => scene.Clip = new RectangleGeometry(new Rect(scene.RenderSize), 21, 21);
        _sync.Add(() =>
        {
            var source = PreviewWallpaper();
            if (ReferenceEquals(source, backdrop.ImageSource)) return;
            backdrop.ImageSource = source;
            _previewImage = null;
        });
        return new Border
        {
            Height = height, CornerRadius = new CornerRadius(22), BorderBrush = Edge, BorderThickness = new Thickness(1),
            Background = Ui.Brush("#121621"), Child = scene, Margin = new Thickness(0, 0, 0, 12)
        };
    }

    void AddDockPreview(Grid scene)
    {
        _previewHost = new ContentControl { Content = _preview, VerticalAlignment = VerticalAlignment.Bottom };
        scene.Children.Add(_previewHost);
        _preview.PreviewInput = PreviewInput;
    }

    void ConfigureDockPreview(Preferences settings)
    {
        if (_previewHost == null) return;
        _previewHost.Visibility = settings.Enabled ? Visibility.Visible : Visibility.Hidden;
        _previewHost.Margin = new Thickness(0, 0, 0, 5 + settings.BottomOffset * .45);
        _preview.Configure(settings);
    }

    static Border PreviewPlate(UIElement content, Thickness padding, double radius = 22) => new()
    {
        Child = content, Padding = padding, CornerRadius = new CornerRadius(radius),
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
    };

    static void PreviewMaterial(Border plate, string material)
    {
        plate.Background = material switch
        {
            "none" => Brushes.Transparent,
            "solid" => Ui.Brush("#050506"),
            "frosted" => Ui.Brush("#B810141C"),
            _ => new LinearGradientBrush(Ui.Brush("#73141A26").ToColor(), Ui.Brush("#A510141C").ToColor(), 90)
        };
        plate.BorderThickness = new Thickness(material == "none" ? 0 : 1);
        plate.BorderBrush = Ui.Brush("#33FFFFFF");
    }

    Border IslandPreview(bool overview)
    {
        var title = Ui.Text("", 12);
        title.TextWrapping = TextWrapping.NoWrap;
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        var mark = Ui.Text("♪", 14);
        mark.HorizontalAlignment = HorizontalAlignment.Center;
        var markCircle = PreviewPlate(mark, new Thickness(0), 12);
        markCircle.Width = markCircle.Height = 24;
        markCircle.Background = Ui.Brush("#1AFFFFFF");
        markCircle.Margin = new Thickness(0, 0, 8, 0);
        var bars = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        for (int i = 0; i < 4; i++)
            bars.Children.Add(new Border { Width = 3, Height = 7, Margin = new Thickness(1, 0, 1, 0), CornerRadius = new CornerRadius(2), Background = Ui.Brush("#30D158") });
        var row = new DockPanel();
        DockPanel.SetDock(markCircle, Dock.Left);
        row.Children.Add(markCircle);
        DockPanel.SetDock(bars, Dock.Right);
        row.Children.Add(bars);
        row.Children.Add(title);
        var capsule = PreviewPlate(row, new Thickness(6, 0, 12, 0), 18);
        capsule.Width = 280;
        capsule.Height = 36;
        if (overview)
        {
            capsule.VerticalAlignment = VerticalAlignment.Top;
            capsule.Margin = new Thickness(0, 16, 0, 0);
        }
        _sync.Add(() =>
        {
            var prefs = App.Current.Prefs.Widgets["island"];
            PreviewMaterial(capsule, prefs.Material);
            capsule.Visibility = !overview || prefs.Enabled ? Visibility.Visible : Visibility.Collapsed;
        });
        _refreshStage += () =>
        {
            var app = App.Current;
            var local = app.Services.NativeModel.GetProperty("music");
            var media = app.Media.Current;
            bool playingLocal = local.GetProperty("playing").GetBoolean();
            bool playing = playingLocal || media.Playing;
            var name = playingLocal ? local.GetProperty("title").GetString() : media.Playing ? media.Title : null;
            title.Text = playing && !string.IsNullOrWhiteSpace(name) ? name : DateTime.Now.ToString("HH:mm");
            mark.Text = playing ? "♪" : "•";
            mark.Foreground = playing ? Brushes.White : Ui.Brush("#30D158");
            bars.Visibility = playing ? Visibility.Visible : Visibility.Collapsed;
            for (int i = 0; i < bars.Children.Count; i++)
                ((Border)bars.Children[i]).Height = 4 + 10 * Math.Clamp(app.Media.Levels[i], 0, 1);
        };
        return capsule;
    }

    Border ShelfPreview(Func<Preferences, ShelfBoxPreference?> get, bool overview)
    {
        var title = Ui.Text("", 12);
        var divider = Highlight(0, "#B3FFFFFF");
        divider.Margin = new Thickness(0, 6, 0, 6);
        var icons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center };
        var empty = Ui.Text("拖入文件至此", 12, "#99FFFFFF");
        empty.HorizontalAlignment = HorizontalAlignment.Center;
        var plate = PreviewPlate(Ui.Stack(title, divider, icons, empty), new Thickness(12, 8, 12, 10));
        if (!overview) plate.Margin = new Thickness(12, 8, 12, 8);
        string? iconSignature = null;
        _sync.Add(() =>
        {
            var prefs = App.Current.Prefs;
            var box = get(prefs);
            bool visible = box != null && (!overview || prefs.Widgets["shelf"].Enabled && box.Enabled);
            plate.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (box == null) return;
            PreviewMaterial(plate, box.Material);
            plate.Width = overview ? 230 : Math.Clamp(box.Width, 160, 460);
            plate.MaxHeight = overview ? 85 : Math.Clamp(box.Height, 100, 172);
            plate.ClipToBounds = true;
            title.Text = box.Title;
            title.Visibility = box.ShowTitle ? Visibility.Visible : Visibility.Collapsed;
            divider.Visibility = box.ShowDivider ? Visibility.Visible : Visibility.Collapsed;
            empty.Visibility = box.Pins.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            var pins = box.Pins.Take(overview ? 5 : 18).ToArray();
            string signature = box.IconSize + "|" + string.Join("|", pins.Select(pin => pin.Id));
            if (signature == iconSignature) return;
            iconSignature = signature;
            icons.Children.Clear();
            double size = overview ? Math.Clamp(box.IconSize * .8, 18, 36) : Math.Min(box.IconSize, 64);
            foreach (var pin in pins)
                icons.Children.Add(new Image { Source = Icons.Get(pin.Path, pin.Name), Width = size, Height = size, Margin = new Thickness(4, 2, 4, 2) });
        });
        return plate;
    }

    Border PerformancePreview(bool overview)
    {
        var name = Ui.Text("", 12, "#C9FFFFFF");
        var number = Ui.Text("", overview ? 34 : 60);
        number.FontWeight = FontWeights.SemiBold;
        var memory = Ui.Text("", 12, "#C9FFFFFF");
        var plate = PreviewPlate(overview ? Ui.Stack(number, memory) : Ui.Stack(name, number, memory), new Thickness(18, 12, 18, 12));
        _sync.Add(() =>
        {
            var prefs = App.Current.Prefs.Widgets["performance"];
            PreviewMaterial(plate, prefs.Material);
            plate.Visibility = !overview || prefs.Enabled ? Visibility.Visible : Visibility.Collapsed;
        });
        _refreshStage += () =>
        {
            var gpu = App.Current.Services.SelectedGpu;
            name.Text = gpu?.Name ?? "等待显卡数据";
            string usage = gpu?.Usage is double value ? $"{value:0}%" : "—";
            number.Text = overview ? usage + " GPU" : usage;
            memory.Text = gpu?.DedicatedBytes is long bytes
                ? $"专用显存 {bytes / 1073741824.0:0.00} GB" + (gpu.CapacityBytes > 0 ? $" / {gpu.CapacityBytes / 1073741824.0:0.00} GB" : "")
                : "专用显存 —";
        };
        return plate;
    }

    void AddOverviewStage()
    {
        var scene = new Grid();
        AddDockPreview(scene);
        var island = IslandPreview(overview: true);
        var clock = new OverviewClock { Width = 245, Height = 92, HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(16, 47, 0, 0) };
        var shelf = ShelfPreview(p => p.ShelfBoxes.FirstOrDefault(box => box.Enabled), overview: true);
        shelf.HorizontalAlignment = HorizontalAlignment.Left;
        shelf.VerticalAlignment = VerticalAlignment.Top;
        shelf.Margin = new Thickness(22, 150, 0, 0);
        var performance = PerformancePreview(overview: true);
        performance.HorizontalAlignment = HorizontalAlignment.Right;
        performance.VerticalAlignment = VerticalAlignment.Top;
        performance.Margin = new Thickness(0, 52, 22, 0);
        scene.Children.Add(clock);
        scene.Children.Add(shelf);
        scene.Children.Add(performance);
        scene.Children.Add(island);
        _sync.Add(() => clock.Visibility = App.Current.Prefs.Widgets["clock"].Enabled ? Visibility.Visible : Visibility.Collapsed);
        _refreshStage += clock.InvalidateVisual;
        _hero = Stage(scene, 292);
        _hero.SizeChanged += (_, _) => _previewImage = null;
        Add(_hero);
    }

    sealed class OverviewClock : FrameworkElement
    {
        protected override void OnRender(DrawingContext drawingContext)
        {
            var prefs = App.Current.Prefs;
            if (RainmeterClock.IsStyle(prefs.ClockStyle))
            {
                RainmeterClock.Draw(drawingContext, this, prefs);
                return;
            }
            if (prefs.ClockStyle == "editorial")
            {
                var now = DateTime.Now;
                var culture = System.Globalization.CultureInfo.InvariantCulture;
                double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
                void Line(string text, double size, double top, Brush brush)
                {
                    var type = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
                    var line = new FormattedText(text, culture, FlowDirection.LeftToRight, type, size, brush, dpi);
                    drawingContext.DrawText(line, new Point((ActualWidth - line.Width) / 2, top));
                }
                Line(string.Join(" ", now.ToString("dddd", culture).ToUpperInvariant().ToCharArray()), 15, 16, Brushes.White);
                Line(now.ToString("HH:mm:ss"), 22, 38, Brushes.White);
                Line(now.ToString("MMMM dd, yyyy", culture).ToUpperInvariant(), 11, 65, Ui.Brush("#D1FFFFFF"));
                return;
            }
            double scale = Math.Min(ActualWidth / 630, ActualHeight / 180);
            drawingContext.PushTransform(new TranslateTransform((ActualWidth - 630 * scale) / 2, (ActualHeight - 180 * scale) / 2));
            drawingContext.PushTransform(new ScaleTransform(scale, scale));
            ClockPainter.Draw(drawingContext, this, prefs.ClockStyle, new Size(630, 180));
            drawingContext.Pop();
            drawingContext.Pop();
        }
    }
}
