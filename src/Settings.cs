using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ChengyuDock;

public sealed class Pin
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "应用";
    public string Path { get; set; } = "";
}
public sealed class Preferences
{
    public int Version { get; set; } = 1;
    public bool Enabled { get; set; } = true;
    public string Material { get; set; } = "liquid";
    public bool Reflection { get; set; } = false;
    public int IconScale { get; set; } = 100;
    public int BottomOffset { get; set; } = 8;
    public int IconSpacing { get; set; } = 10;
    public int LaunchpadScale { get; set; } = 100;
    public int LaunchpadIconScale { get; set; } = 100;
    public bool ShowMinimized { get; set; } = true;
    public bool ShowRunningApps { get; set; } = true;
    public bool ShowLaunchpad { get; set; } = true;
    public SpectrumPreference Spectrum { get; set; } = new();
    public DesktopIconPreference DesktopIcons { get; set; } = new();
    public bool ReducedMotion { get; set; } = false;
    public bool DockTopmost { get; set; } = false;
    public bool SnapEnabled { get; set; } = true;
    public bool TaskbarGuardEnabled { get; set; }
    public WallpaperPreference Wallpaper { get; set; } = new();
    public List<Pin> Pins { get; set; } = new();
    public string LaunchpadMaterial { get; set; } = "liquid";
    public Dictionary<string, WidgetPreference> Widgets { get; set; } = WidgetPreference.Defaults();
    public List<TodoItem> Todos { get; set; } = new();
    public List<Pin> ShelfPins { get; set; } = new(); // Legacy v0.4 migration only.
    public List<ShelfBoxPreference> ShelfBoxes { get; set; } = new() { new() };
    public string GpuAdapterId { get; set; } = "";
    public string ClockStyle { get; set; } = "editorial";
    public string? ClockTextColor { get; set; }
    public int ClockOpacity { get; set; } = 100;
    public void Upgrade() { if (ShelfPins?.Count > 0 && ShelfBoxes != null) { if (ShelfBoxes.Count == 0) ShelfBoxes.Add(new()); foreach (var pin in ShelfPins) if (!ShelfBoxes[0].Pins.Any(x => x.Path == pin.Path)) ShelfBoxes[0].Pins.Add(pin); ShelfPins.Clear(); } }
    public List<string> MusicFiles { get; set; } = new();
    public string WeatherCity { get; set; } = "济南";
    public double WeatherLatitude { get; set; } = 36.65;
    public double WeatherLongitude { get; set; } = 117.12;
    public void Validate()
    {
        if (ClockOpacity is < 0 or > 100 ||
            (ClockTextColor != null && !RainmeterClock.IsTextColor(ClockTextColor)))
            throw new InvalidDataException("时钟颜色或透明度无效");
        if (!WidgetStyles.Clock.Any(x => x.Key == ClockStyle) || ShelfBoxes == null || ShelfBoxes.Count > 20 || ShelfBoxes.Any(b => b == null || string.IsNullOrWhiteSpace(b.Id) || b.Title == null || b.Title.Length > 60 || b.Width is < 160 or > 900 || b.Height is < 100 or > 700 || b.IconSize is < 20 or > 80 || b.Pins == null || b.Pins.Count > 128 || b.Pins.Any(x => x == null || string.IsNullOrWhiteSpace(x.Id) || string.IsNullOrWhiteSpace(x.Path)) || b.Pins.Select(x => x.Id).Distinct().Count() != b.Pins.Count || !new[] { "liquid", "frosted", "none", "solid" }.Contains(b.Material) || !double.IsFinite(b.X ?? 0) || !double.IsFinite(b.Y ?? 0)) || ShelfBoxes.Select(b => b.Id).Distinct().Count() != ShelfBoxes.Count) throw new InvalidDataException("收纳盒或时钟设置无效");
        if (DesktopIcons == null || DesktopIcons.DelaySeconds is < 3 or > 60) throw new InvalidDataException("桌面图标设置无效");
        if (Spectrum == null || !new[] { "rainbow", "white" }.Contains(Spectrum.Color) || Spectrum.SilenceThresholdDb is < -90 or > -30 || !WidgetStyles.Spectrum.Any(x => x.Key == Spectrum.Style) || !new[] { "system", "microphone" }.Contains(Spectrum.Source) || Spectrum.BarWidth is < 2 or > 24 || Spectrum.BarGap is < 0 or > 12) throw new InvalidDataException("频谱设置无效");
        if (Wallpaper == null || !new[] { "none", "image", "video", "web", "scene", "engine" }.Contains(Wallpaper.Kind) ||
            !new[] { "cover", "contain" }.Contains(Wallpaper.Fit) || Wallpaper.Path == null || Wallpaper.Path.Length > 32767 ||
            Wallpaper.LibrarySources == null || Wallpaper.LibrarySources.Any(p => string.IsNullOrWhiteSpace(p) || p.Length > 32767) ||
            Wallpaper.EnginePath == null || Wallpaper.EnginePath.Length > 32767 ||
            Wallpaper.PlaybackRate is < 25 or > 400 || Wallpaper.Volume is < 0 or > 100)
            throw new InvalidDataException("壁纸配置无效");
        if (Version != 1 || !new[] { "liquid", "frosted", "none" }.Contains(Material)) throw new InvalidDataException("配置版本或材质不受支持");
        if (IconScale is < 50 or > 120 || BottomOffset is < 0 or > 160 || IconSpacing is < 4 or > 24 || LaunchpadScale is < 60 or > 140 || LaunchpadIconScale is < 50 or > 120) throw new InvalidDataException("配置数值超出范围");
        if (Pins == null || Pins.Count > 32 || Pins.Any(p => p == null || string.IsNullOrWhiteSpace(p.Id) || string.IsNullOrWhiteSpace(p.Name) || string.IsNullOrWhiteSpace(p.Path)) || Pins.Select(p => p.Id).Distinct().Count() != Pins.Count) throw new InvalidDataException("固定项目配置无效");
        if (!new[] { "liquid", "frosted", "none" }.Contains(LaunchpadMaterial) || Widgets == null || Widgets.Count > 10 || Widgets.Any(x => !WidgetPreference.Names.ContainsKey(x.Key) || x.Value == null || !new[] { "liquid", "frosted", "none", "solid" }.Contains(x.Value.Material) || !double.IsFinite(x.Value.X ?? 0) || !double.IsFinite(x.Value.Y ?? 0))) throw new InvalidDataException("桌面组件配置无效");
        if (Todos == null || Todos.Count > 500 || Todos.Any(t => t == null || string.IsNullOrWhiteSpace(t.Id) || t.Text == null || t.Text.Length > 300 || !DateOnly.TryParse(t.Date, out _)) || ShelfPins == null || ShelfPins.Count > 32 || ShelfPins.Any(p => p == null || string.IsNullOrWhiteSpace(p.Id) || string.IsNullOrWhiteSpace(p.Path)) || MusicFiles == null || MusicFiles.Count > 200) throw new InvalidDataException("组件内容配置无效");
        if (string.IsNullOrWhiteSpace(WeatherCity) || WeatherCity.Length > 60 || !double.IsFinite(WeatherLatitude) || !double.IsFinite(WeatherLongitude) || WeatherLatitude is < -90 or > 90 || WeatherLongitude is < -180 or > 180) throw new InvalidDataException("天气位置无效");
    }
    public Preferences Clone() => JsonSerializer.Deserialize<Preferences>(JsonSerializer.Serialize(this))!;
    public static Preferences Default()
    {
        string edge = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe");
        var p = new Preferences();
        p.Pins.Add(new Pin { Name = "文件资源管理器", Path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe") });
        p.Pins.Add(new Pin { Name = "Microsoft Edge", Path = File.Exists(edge) ? edge : "https://www.microsoft.com/edge" });
        p.Pins.Add(new Pin { Name = "记事本", Path = System.IO.Path.Combine(Environment.SystemDirectory, "notepad.exe") });
        p.Pins.Add(new Pin { Name = "计算器", Path = System.IO.Path.Combine(Environment.SystemDirectory, "calc.exe") });
        p.Pins.Add(new Pin { Name = "设置", Path = "ms-settings:" });
        return p;
    }
}
public sealed class WallpaperPreference
{
    public List<string> LibrarySources { get; set; } = new();
    public bool LibraryInitialized { get; set; }
    public string EnginePath { get; set; } = "";
    public int PlaybackRate { get; set; } = 100;
    public bool AudioEnabled { get; set; }
    public int Volume { get; set; } = 50;
    public string Kind { get; set; } = "none";
    public string Path { get; set; } = "";
    public string Fit { get; set; } = "cover";
    public bool Paused { get; set; }
    public bool AutoPause { get; set; } = true;
    public bool ReleaseWhenCovered { get; set; } = true;
}
public sealed class SpectrumPreference
{
    bool? _liveEnabled;
    // Older profiles only stored the source, so an old microphone selection needs a fresh opt-in.
    public bool LiveEnabled { get => _liveEnabled ?? (Source == "system"); set => _liveEnabled = value; }
    public string Style { get; set; } = "blocks";
    public string Color { get; set; } = "rainbow";
    public bool HideWhenSilent { get; set; } = true;
    public int SilenceThresholdDb { get; set; } = -60;
    public string Source { get; set; } = "system";
    public int BarWidth { get; set; } = 4;
    public int BarGap { get; set; } = 1;
}
public sealed class DesktopIconPreference
{
    public bool DoubleClick { get; set; } = true;
    public bool AutoHide { get; set; }
    public bool RestoreOnInput { get; set; } = true;
    public int DelaySeconds { get; set; } = 15;
}
public class WidgetPreference
{
    public bool Enabled { get; set; } = true;
    public string Material { get; set; } = "liquid";
    public double? X { get; set; }
    public double? Y { get; set; }
    public bool Locked { get; set; }
    public static readonly Dictionary<string, string> Names = new() { ["shelf"] = "桌面收纳盒", ["clock"] = "桌面时钟", ["weather"] = "实时天气", ["performance"] = "性能监控", ["todayTodo"] = "今日待办", ["music"] = "桌面音乐", ["calendar"] = "日历", ["almanac"] = "黄历", ["island"] = "灵动岛", ["spectrum"] = "音频频谱" };
    public static Dictionary<string, WidgetPreference> Defaults() => Names.Keys.ToDictionary(k => k, k => new WidgetPreference { Enabled = k is not ("calendar" or "almanac"), Material = k == "clock" ? "none" : k == "island" ? "solid" : k is "performance" or "todayTodo" or "music" or "spectrum" ? "frosted" : "liquid" });
}
public sealed class TodoItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Text { get; set; } = "";
    public string Date { get; set; } = DateOnly.FromDateTime(DateTime.Today).ToString("yyyy-MM-dd");
    public bool Done { get; set; }
}
public sealed class SettingsStore
{
    public string Root { get; }
    public string FilePath => Path.Combine(Root, "dock-preferences.json");
    public string LoadNotice { get; private set; } = "";
    bool _primaryInvalid;
    public SettingsStore(string root) { Root = root; Directory.CreateDirectory(root); }
    public Preferences Load()
    {
        if (!File.Exists(FilePath) && !File.Exists(FilePath + ".bak")) return Preferences.Default();
        foreach (string path in new[] { FilePath, FilePath + ".bak" })
        {
            if (!File.Exists(path)) continue;
            try
            {
                var p = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(path)) ?? throw new InvalidDataException();
                if (p.Widgets != null) foreach (var pair in WidgetPreference.Defaults()) p.Widgets.TryAdd(pair.Key, pair.Value);
                p.Upgrade(); p.Validate();
                bool migrated = false;
                foreach (var pin in p.Pins)
                {
                    string target = PackagedApps.UpgradeLegacyTarget(pin.Path);
                    if (target == pin.Path) continue;
                    pin.Path = target;
                    migrated = true;
                }
                if (migrated && path == FilePath)
                {
                    try { Save(p); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    { LoadNotice = "已修复本次运行的 Dock 固定项，暂时无法保存设置。"; }
                }
                if (path.EndsWith(".bak")) LoadNotice = "已从备份恢复设置；下次调整时会保存新配置。";
                return p;
            }
            catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException or UnauthorizedAccessException) { if (path == FilePath) _primaryInvalid = true; LoadNotice = "原配置不可用，已使用默认设置；原文件保留。"; }
        }
        return Preferences.Default();
    }
    public void Save(Preferences value)
    {
        value.Validate();
        string temp = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, value, new JsonSerializerOptions { WriteIndented = true }); stream.Flush(true); }
            if (File.Exists(FilePath) && _primaryInvalid) { File.Move(FilePath, FilePath + ".corrupt-" + DateTime.UtcNow.Ticks); File.Move(temp, FilePath); }
            else if (File.Exists(FilePath)) File.Replace(temp, FilePath, FilePath + ".bak", true); else File.Move(temp, FilePath);
            _primaryInvalid = false;
            LoadNotice = "";
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

public sealed class ShelfBoxPreference : WidgetPreference
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "常用收纳";
    public int Width { get; set; } = 280;
    public int Height { get; set; } = 200;
    public int IconSize { get; set; } = 36;
    public bool ShowTitle { get; set; } = true;
    public bool ShowDivider { get; set; } = true;
    public List<Pin> Pins { get; set; } = new();
}
internal static class WidgetStyles
{
    internal static readonly (string Name, string Key)[] Spectrum = { ("方块", "blocks"), ("线条", "pulse"), ("波浪", "waves"), ("圆角柱", "bars"), ("镜像", "mirror"), ("点阵", "dots"), ("环形", "ring"), ("山峦", "mountains") };
    internal static readonly (string Name, string Key)[] Clock =
    {
        ("Welfare", "welfare"), ("Mond", "mond"), ("InsideDream", "insideDream"),
        ("英文星期", "editorial"), ("极简数字", "minimal"), ("翻页时钟", "flip"),
        ("模拟表盘", "analog"), ("中文日期", "chinese"), ("纵向排版", "stacked")
    };
}
