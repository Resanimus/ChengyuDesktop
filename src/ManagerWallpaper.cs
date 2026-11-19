using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace ChengyuDock;

public sealed partial class ManagerWindow
{
    Action? _refreshWallpaperPage;
    internal void WallpaperUpdated() => _refreshWallpaperPage?.Invoke();

    void WallpaperPage()
    {
        var service = App.Current.Wallpaper;
        var current = Ui.Text("", 12, "#A8B5C6");
        var playback = Ui.Text("", 12, "#A8B5C6");
        var fit = Toggle(p => p.Wallpaper.Fit == "contain", (p, value) => p.Wallpaper.Fit = value ? "contain" : "cover");
        var paused = Toggle(p => p.Wallpaper.Paused, (p, value) => p.Wallpaper.Paused = value);
        var autoPause = Toggle(p => p.Wallpaper.AutoPause, (p, value) => p.Wallpaper.AutoPause = value);
        var release = Toggle(p => p.Wallpaper.ReleaseWhenCovered, (p, value) => p.Wallpaper.ReleaseWhenCovered = value);
        var audio = Toggle(p => p.Wallpaper.AudioEnabled, (p, value) => p.Wallpaper.AudioEnabled = value);
        var speed = Ui.Stack(
            Range("播放速度（100% = 1×）", 25, 400, p => p.Wallpaper.PlaybackRate, (p, value) => p.Wallpaper.PlaybackRate = value, "%"),
            Ui.Actions(new[] { 50, 75, 100, 125, 150, 200 }.Select(rate =>
                Ui.Button((rate / 100.0).ToString("0.##") + "×", () => App.Current.Apply(p => p.Wallpaper.PlaybackRate = rate))).ToArray()));
        var playbackSettings = new Expander { Header = "播放设置", Margin = new Thickness(0, 10, 0, 0) };
        Add(Card(Ui.Stack(
            Ui.Actions(Ui.Button("选择文件", service.Choose), Ui.Button("恢复系统壁纸", () =>
                App.Current.Apply(p => { p.Wallpaper.Kind = "none"; p.Wallpaper.Path = ""; p.Wallpaper.Paused = false; }))),
            current, playback, playbackSettings)));
        playbackSettings.Content = Ui.Stack(
            Row("完整显示（留边）", fit),
            Row("暂停播放", paused),
            Row("桌面被遮挡时自动暂停", autoPause, "最大化、全屏或多个窗口合计遮住约 95% 主屏工作区时暂停"),
            Row("持续遮挡时释放播放资源", release, "持续 12 秒后释放解码器或网页，露出桌面时恢复"),
            speed,
            Row("背景音乐", audio),
            Range("壁纸音量", 0, 100, p => p.Wallpaper.Volume, (p, value) => p.Wallpaper.Volume = value, "%"),
            Ui.Text("场景试播直接读取项目文件。场景脚本和系统音频响应暂未接入。", 12, "#98AAC2"));

        var folders = new StackPanel();
        var pathInput = new TextBox { MinWidth = 230, MaxWidth = 520, ToolTip = "粘贴本地壁纸目录", VerticalContentAlignment = VerticalAlignment.Center };
        var engine = Ui.Text("", 11, "#98AAC2");
        var sourceSettings = new Expander { Header = "扫描目录" };
        Add(Card(sourceSettings));
        sourceSettings.Content = Ui.Stack(
            folders,
            Ui.Actions(pathInput, Ui.Button("添加路径", () =>
            {
                string path = pathInput.Text.Trim().Trim('"');
                if (!Directory.Exists(path)) { App.Current.Notify("目录不存在或暂时不可访问。"); return; }
                service.AddSources(new[] { path });
                pathInput.Clear();
            }), Ui.Button("浏览文件夹", () =>
            {
                var dialog = new OpenFolderDialog { Title = "添加壁纸目录", Multiselect = true };
                if (dialog.ShowDialog(this) == true) service.AddSources(dialog.FolderNames);
            })),
            Ui.Actions(Ui.Button("识别 Steam 壁纸目录", () =>
            {
                var detected = WallpaperLibrary.DiscoverSteam();
                service.AddSources(detected.Sources);
                if (detected.Engine.Length > 0)
                    App.Current.Apply(p => p.Wallpaper.EnginePath = detected.Engine);
                if (detected.Sources.Length == 0) App.Current.Notify("未找到 Steam 壁纸目录，可手动添加路径。");
            }), Ui.Button("重新扫描", () => service.Library.Refresh(App.Current.Prefs.Wallpaper.LibrarySources.ToArray()))),
            engine,
            Ui.Actions(Ui.Button("选择 Wallpaper Engine 程序", () =>
            {
                var dialog = new OpenFileDialog { Title = "选择 wallpaper32.exe 或 wallpaper64.exe", Filter = "Wallpaper Engine|wallpaper32.exe;wallpaper64.exe" };
                if (dialog.ShowDialog(this) != true) return;
                if (!EngineWallpaper.ValidExecutable(dialog.FileName)) { App.Current.Notify("请选择 wallpaper32.exe 或 wallpaper64.exe。"); return; }
                App.Current.Apply(p => p.Wallpaper.EnginePath = dialog.FileName);
            })));

        var search = new TextBox { MinWidth = 240, ToolTip = "搜索壁纸标题或路径", VerticalContentAlignment = VerticalAlignment.Center };
        var filter = new WrapPanel { Margin = new Thickness(0, 7, 0, 0) };
        string selectedKind = "";
        var filterButtons = new System.Collections.Generic.List<(string Kind, Button Button)>();
        var count = Ui.Text("", 12, "#98AAC2");
        var gallery = new WrapPanel();
        var pageLabel = Ui.Text("", 12, "#98AAC2");
        int page = 0;
        var previous = Ui.Button("上一页", () => { });
        var next = Ui.Button("下一页", () => { });
        Add(Card(Ui.Stack(Row("壁纸库", Ui.Actions(Ui.Text("搜索", 12, "#98AAC2"), search)), filter, count, gallery,
            Ui.Actions(previous, pageLabel, next))));

        WallpaperItem[] Builtins() => new[] { ("午夜微光", "midnight.png") }
            .Select(item => new WallpaperItem(item.Item1, "image",
                Path.Combine(AppContext.BaseDirectory, "Assets", item.Item2),
                Path.Combine(AppContext.BaseDirectory, "Assets", item.Item2), "", "内置壁纸")).ToArray();
        string oldGallery = "";
        string oldSources = "?";
        void RenderGallery()
        {
            string query = search.Text.Trim();
            string kind = selectedKind;
            var all = Builtins().Concat(service.Library.Items)
                .DistinctBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                .Where(item => (kind.Length == 0 || item.Kind == kind) &&
                    (query.Length == 0 || item.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        item.Key.Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray();
            const int pageSize = 24;
            int pages = Math.Max(1, (all.Length + pageSize - 1) / pageSize);
            page = Math.Clamp(page, 0, pages - 1);
            string signature = string.Join("\n", all.Select(item => item.Key)) + "|" + page + "|" +
                App.Current.Prefs.Wallpaper.Path + "|" + service.Library.Scanning + "|" + service.Library.Revision +
                "|" + App.Current.Prefs.Wallpaper.EnginePath;
            count.Text = (service.Library.Scanning ? "正在扫描…  " : "") + $"{all.Length} 张壁纸" +
                (service.Library.Issues.Count > 0 ? $" · {service.Library.Issues.Count} 项未能读取" : "");
            count.ToolTip = service.Library.Issues.Count == 0 ? null : string.Join("\n", service.Library.Issues.Take(20));
            if (oldGallery == signature) return;
            oldGallery = signature;
            gallery.Children.Clear();
            foreach (var item in all.Skip(page * pageSize).Take(pageSize))
            {
                var image = new Image { Width = 222, Height = 125, Stretch = Stretch.UniformToFill };
                try
                {
                    image.Source = WallpaperService.Thumbnail(item.Preview, 280);
                    if (image.Source == null && item.Kind == "video") image.Source = Icons.Get(item.Path, item.Title);
                }
                catch { image.Source = null; }
                var preview = new Border
                {
                    Width = 222, Height = 125, CornerRadius = new CornerRadius(10),
                    Background = Ui.Brush("#151D29"), ClipToBounds = true, Child = image
                };
                if (image.Source == null) preview.Child = new Viewbox
                {
                    Width = 36, Height = 36, Child = ConsoleIcons.Draw("wallpaper", 36, "#728199"),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                };
                var title = Ui.Text(item.Title, 13);
                title.TextWrapping = TextWrapping.NoWrap;
                title.TextTrimming = TextTrimming.CharacterEllipsis;
                title.ToolTip = item.Title;
                title.Margin = new Thickness(0, 9, 0, 4);
                var preference = App.Current.Prefs.Wallpaper;
                bool selected = preference.Path.Equals(item.Path, StringComparison.OrdinalIgnoreCase) ||
                    item.Project.Length > 0 && preference.Path.Equals(item.Project, StringComparison.OrdinalIgnoreCase) ||
                    (item.Kind is "scene" or "application") && item.HasPreview && preference.Path.Equals(item.Preview, StringComparison.OrdinalIgnoreCase);
                var detail = Ui.Text(item.TypeName + " · " + (item.NativeScene ? "支持独立试播" : item.NativeWeb || !item.UsesEngine ? "澄屿播放" :
                    selected && preference.Kind == "image" ? "静态预览" : "动态效果需原引擎") +
                    (selected ? " · 当前" : ""), 11, "#98AAC2");
                var actions = new WrapPanel();
                if (!item.UsesEngine || item.NativeWeb || item.NativeScene)
                {
                    var apply = Ui.Button(item.NativeScene ? "独立试播" : "应用壁纸", () => service.Select(item));
                    apply.IsEnabled = item.Available;
                    apply.ToolTip = item.Available ? "使用澄屿播放器" : "主文件缺失或类型暂不支持";
                    actions.Children.Add(apply);
                }
                else if (item.HasPreview) actions.Children.Add(Ui.Button("用预览图", () => service.Select(item.Preview)));
                if (item.Project.Length > 0 && (item.UsesEngine || item.Kind == "video"))
                {
                    var compatibility = Ui.Button(item.Kind == "application" ? "用原引擎运行" : "用原引擎播放", () => service.Select(item, engine: true));
                    compatibility.ToolTip = "交给 Wallpaper Engine";
                    compatibility.IsEnabled = item.Available && EngineWallpaper.ValidExecutable(preference.EnginePath);
                    actions.Children.Add(compatibility);
                }
                var card = new Border
                {
                    Width = 242, Padding = new Thickness(9), Margin = new Thickness(0, 10, 12, 0),
                    Background = Ui.Brush("#111720"), BorderThickness = new Thickness(1),
                    BorderBrush = Ui.Brush(selected ? "#6897C9" : "#293342"), CornerRadius = new CornerRadius(13),
                    Child = Ui.Stack(preview, title, detail, actions), ToolTip = item.Key
                };
                var menu = AppMenu.Create(item.Title);
                if (item.NativeScene && item.HasPreview)
                    AppMenu.Action(menu, "用预览图", () => service.Select(item.Preview));
                AppMenu.Action(menu, "打开所在文件夹", () =>
                {
                    var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
                    start.ArgumentList.Add(Path.GetDirectoryName(item.Key)!);
                    Process.Start(start)?.Dispose();
                });
                card.ContextMenu = menu;
                gallery.Children.Add(card);
            }
            pageLabel.Text = $"{page + 1} / {pages}";
            previous.IsEnabled = page > 0;
            next.IsEnabled = page + 1 < pages;
        }
        search.TextChanged += (_, _) => { page = 0; RenderGallery(); };
        foreach (var (label, kind) in new[] { ("全部", ""), ("图片", "image"), ("视频", "video"),
            ("场景", "scene"), ("网页", "web"), ("应用", "application") })
        {
            var button = ChoiceButton(label, () =>
            {
                selectedKind = kind;
                foreach (var entry in filterButtons) SelectChoice(entry.Button, entry.Kind == kind);
                page = 0;
                RenderGallery();
            });
            SelectChoice(button, kind == selectedKind);
            filterButtons.Add((kind, button));
            filter.Children.Add(button);
        }
        previous.Click += (_, _) => { page--; RenderGallery(); };
        next.Click += (_, _) => { page++; RenderGallery(); };

        void Refresh()
        {
            var preference = App.Current.Prefs.Wallpaper;
            current.Text = preference.Kind == "none" ? "使用当前系统壁纸" : preference.Path;
            playback.Text = preference.Kind == "none" ? "" : service.Status;
            bool scene = preference.Kind == "scene";
            speed.Visibility = scene ? Visibility.Collapsed : Visibility.Visible;
            playbackSettings.Header = "播放设置 · " + (scene ? "场景试播 · " : (preference.PlaybackRate / 100.0).ToString("0.##") + "× · ") +
                (preference.AudioEnabled ? preference.Volume + "%" : "静音");
            sourceSettings.Header = $"扫描目录 · {preference.LibrarySources.Count} 个";
            fit.IsEnabled = preference.Kind is "image" or "video" or "scene";
            paused.IsEnabled = preference.Kind is "video" or "web" or "scene";
            autoPause.IsEnabled = preference.Kind is "video" or "web" or "scene";
            release.IsEnabled = autoPause.IsEnabled && preference.AutoPause;
            audio.IsEnabled = preference.Kind is "video" or "web" or "scene" or "engine";
            engine.Text = preference.EnginePath.Length > 0 ? preference.EnginePath : "为原引擎播放方式配置 Wallpaper Engine 路径";
            string signature = string.Join("\n", preference.LibrarySources);
            if (signature != oldSources)
            {
                oldSources = signature;
                folders.Children.Clear();
                foreach (string source in preference.LibrarySources)
                    folders.Children.Add(Row(source, Ui.Button("移除", () => App.Current.Apply(p =>
                        p.Wallpaper.LibrarySources.RemoveAll(path => path.Equals(source, StringComparison.OrdinalIgnoreCase))))));
                if (preference.LibrarySources.Count == 0) folders.Children.Add(Ui.Text("添加一个目录开始扫描。", 12, "#98AAC2"));
            }
            RenderGallery();
        }
        _refreshWallpaperPage = Refresh;
        _sync.Add(Refresh);
        Refresh();
    }
}
