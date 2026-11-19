using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ChengyuDock;

internal static class SelfTest
{
    public static void Run(string root)
    {
        var store = new SettingsStore(Path.Combine(root, "self-test-" + Guid.NewGuid().ToString("N")));
        var prefs = Preferences.Default(); prefs.Validate(); store.Save(prefs);
        prefs.IconScale = 80; prefs.Reflection = true; prefs.Pins.Reverse(); store.Save(prefs);
        var loaded = store.Load();
        Require(loaded.IconScale == 80 && loaded.Reflection && loaded.Pins.First().Id == prefs.Pins.First().Id, "设置和排序重启后保持");
        File.WriteAllText(store.FilePath, "{broken"); var recovered = store.Load();
        Require(recovered.IconScale == 100 && store.LoadNotice.Contains("备份"), "损坏配置恢复备份");
        prefs.IconScale = 121; bool rejected = false; try { store.Save(prefs); } catch (InvalidDataException) { rejected = true; }
        Require(rejected, "越界值拒绝写入");
        Require(DockMotion.TargetScale(0) == 1.5 && DockMotion.TargetScale(1) == 1 && DockMotion.TargetScale(.25) > DockMotion.TargetScale(.5), "悬停曲线边界和距离衰减");
        double scale = 1, velocity = 0;
        for (int i = 0; i < 120; i++) DockView.Spring(ref scale, ref velocity, 1.5, 1.0 / 60, false);
        Require(Math.Abs(scale - 1.5) < .001, "弹簧收敛");
        DockView.Spring(ref scale, ref velocity, 1, 1.0 / 60, false); Require(scale > 1 && scale < 1.5 && velocity < 0, "中途反向保留连续状态");
        for (int i = 0; i < 120; i++) DockView.Spring(ref scale, ref velocity, 1, 1.0 / 60, false);
        Require(Math.Abs(scale - 1) < .001 && Math.Abs(velocity) < .012, "返回并停止");
        var peers = new[] { new System.Windows.Rect(100, 100, 200, 100) };
        var near = SnapEngine.Solve(106, 200, 105, 100, peers, 0, 1000, true, null);
        Require(near?.Position == 100 && near.Kind == 0, "同侧吸附");
        Require(SnapEngine.Solve(112, 200, 105, 100, peers, 0, 1000, true, near)?.Position == 100, "12px 滞回保持");
        Require(SnapEngine.Solve(113, 200, 105, 100, peers, 0, 1000, true, near) == null, "超过12px脱开");
        Require(SnapEngine.Solve(306, 150, 105, 100, peers, 0, 1000, true, null)?.Position == 308, "8px 相邻间距");
        Require(SnapEngine.Solve(106, 200, 211, 100, peers, 0, 1000, true, null) == null, "垂直间距超过10px不吸附");
        VerifyDesktop04(); VerifyDesktop05(); VerifyDesktop06();
        VerifyPackagedPin(store);
        File.WriteAllText(Path.Combine(root, "self-test-result.json"), JsonSerializer.Serialize(new { success = true, checks = new[] { "settings-roundtrip", "pin-order", "backup-recovery", "range-rejection", "magnification-boundaries", "spring-settle", "spring-interruption", "snap-edge", "snap-hysteresis", "snap-neighbor", "snap-perpendicular-limit", "dock-resident-running-lifecycle", "dock-grouping-divider", "unpin-drag-distance", "physical-double-click-state", "hann-fft-tone-silence", "island-timers-retarget", "styles-and-multibox-roundtrip", "legacy-shelf-migration", "tray-only-running-dot", "top-edge-island-stow", "packaged-pin-migration", "packaged-window-grouping" }, version = "0.8.0" }, new JsonSerializerOptions { WriteIndented = true }));
    }
    static void VerifyPackagedPin(SettingsStore store)
    {
        const string oldExe = @"C:\Program Files\WindowsApps\OpenAI.Codex_26.1106.6628.0_x64__2p2nqsd0c76g0\app\ChatGPT.exe";
        const string newExe = @"C:\Program Files\WindowsApps\OpenAI.Codex_26.1111.2228.0_x64__2p2nqsd0c76g0\app\ChatGPT.exe";
        var prefs = Preferences.Default();
        var pin = new Pin { Name = "ChatGPT", Path = oldExe };
        prefs.Pins.Insert(1, pin);
        store.Save(prefs);

        var loaded = store.Load();
        var migrated = loaded.Pins[1];
        Require(migrated.Id == pin.Id && migrated.Name == pin.Name &&
            PackagedApps.TryGetId(migrated.Path, out string id) && id == PackagedApps.ChatGptAppId &&
            File.ReadAllText(store.FilePath).Contains(PackagedApps.ChatGptAppId, StringComparison.Ordinal) &&
            File.ReadAllText(store.FilePath + ".bak").Contains("26.1106.6628.0", StringComparison.Ordinal), "迁移固定项并保留原排序及备份");

        var updated = new WindowEntry(new IntPtr(201), "ChatGPT", newExe, false, PackagedApps.ChatGptAppId);
        var other = new WindowEntry(new IntPtr(202), "ChatGPT", @"C:\other\ChatGPT.exe", false);
        Require(DockItems.Matches(migrated, updated with { AppId = null }) &&
            !DockItems.Matches(migrated, updated with { AppId = "Another.App_abc123!App" }) &&
            !DockItems.Matches(migrated, other), "仅相同应用标识归入现有固定项");
        var open = DockItems.Build(loaded, new[] { updated, other });
        Require(open.Single(e => e.Pin?.Id == pin.Id).Window == updated.Handle &&
            open.Count(e => e.Windows.Any(w => w.Handle == updated.Handle)) == 1, "更新后的程序只在左侧出现一次");
        loaded.Pins.RemoveAt(1);
        var temporary = DockItems.Build(loaded, new[] { updated, updated with { Handle = new IntPtr(203), Exe = oldExe } })
            .Single(e => e.SectionBreakBefore);
        Require(temporary.Windows.Length == 2 && temporary.Path == migrated.Path, "右侧按应用标识分组和固定");
    }
    static void VerifyDesktop04()
    {
        var prefs = Preferences.Default(); prefs.Pins.Clear(); var pin = new Pin { Name = "常驻样本", Path = @"C:\test\pinned.exe" }; prefs.Pins.Add(pin);
        var running = new WindowEntry(new IntPtr(123), "常驻窗口", pin.Path, false); var temp = new WindowEntry(new IntPtr(124), "临时窗口", @"C:\test\temporary.exe", false);
        var closed = DockItems.Build(prefs, Array.Empty<WindowEntry>()); Require(closed.Any(e => e.Pin?.Id == pin.Id) && !closed.Any(e => e.SectionBreakBefore), "关闭后左侧常驻保留，空临时区无分隔线");
        var opened = DockItems.Build(prefs, new[] { running, temp, temp with { Handle = new IntPtr(125) } }); Require(opened.Count(e => e.Key.StartsWith("running-")) == 1 && opened.Last().Windows.Length == 2 && opened.Last().SectionBreakBefore, "临时窗口按应用合并");
        Require(opened.Single(e => e.Pin?.Id == pin.Id).Running, "常驻打开后保留并标记运行");
        var restored = DockItems.Build(prefs, new[] { temp with { Minimized = true } }); Require(restored.Any(e => e.Window == temp.Handle) && DockItems.Build(prefs, new[] { temp }).Any(e => e.Window == temp.Handle), "临时窗口最小化与恢复均保留");
        prefs.Pins.Clear(); Require(DockItems.Build(prefs, new[] { running }).Any(e => e.Key.StartsWith("running-")), "取消常驻后运行中应用转到右侧");
        Require(DockItems.Matches(new Pin { Path = "ms-settings:" }, new WindowEntry(new IntPtr(1), "设置", @"C:\Windows\System32\ApplicationFrameHost.exe", false)), "设置URI匹配UWP外壳窗口");
        Require(DockItems.Matches(new Pin { Path = @"C:\Windows\System32\calc.exe" }, new WindowEntry(new IntPtr(2), "计算器", @"C:\Program Files\WindowsApps\CalculatorApp.exe", false)), "计算器启动器匹配实际窗口");
        var bounds = new System.Windows.Rect(500, 900, 450, 80); Require(DockItems.DragRemoves(new PointLike(600, 790), bounds) && !DockItems.DragRemoves(new PointLike(600, 850), bounds), "远拖移除与回拖取消");
        var click = new DoubleClickTracker(); var point = new Native.NativePoint { X = 100, Y = 100 }; var hwnd = new IntPtr(33);
        Require(!click.Push(1000, point, hwnd, true, 500, 4, 4) && click.Push(1500, new Native.NativePoint { X = 102, Y = 98 }, hwnd, true, 500, 4, 4), "系统双击容差边界");
        Require(!click.Push(1550, point, hwnd, true, 500, 4, 4), "三连击不重复切换");
        var invalid = new DoubleClickTracker(); invalid.Push(100, point, hwnd, false, 500, 4, 4); Require(!invalid.Push(200, point, hwnd, true, 500, 4, 4), "图标点击不参与空白双击"); Require(!invalid.Push(300, point, new IntPtr(34), true, 500, 4, 4), "不同窗口不算双击");
        var fft = new SpectrumDsp(); for (int i = 0; i < SpectrumDsp.Size * 2; i++) fft.Push((float)(.2 * Math.Sin(2 * Math.PI * 1000 * i / 48000)), 48000); Require(Math.Abs(fft.PeakFrequency() - 1000) < 6, "8192点Hann FFT频率峰"); var bars = new double[127]; fft.Read(bars, false); Require(bars.All(n => double.IsFinite(n) && n >= 0 && n <= 1) && bars.Max() > .5, "频谱幅度有限且有响应");
        var silence = new SpectrumDsp(); for (int i = 0; i < SpectrumDsp.Size; i++) silence.Push(0, 48000); silence.Read(bars, false); Require(bars.All(n => n == 0), "无声音时没有伪造频谱");
        var island = new IslandMotion(); island.Enter(0); island.Step(.319, .01, false); Require(!island.Expanded, "悬停320ms前不展开"); island.Step(.321, .002, false); Require(island.Expanded, "悬停320ms展开"); island.Leave(.4); island.Step(.849, .01, false); Require(island.Expanded, "移出450ms前保持"); island.Step(.851, .002, false); Require(!island.Expanded, "移出450ms收起");
        island.Click(1); for (int i = 0; i < 8; i++) island.Step(1 + i / 60.0, 1.0 / 60, false); double velocity = island.Width.Velocity; island.Click(1.14); Require(island.Width.Velocity == velocity && Math.Abs(velocity) > 1, "反向动作保留当前速度");
    }
    static void VerifyDesktop05()
    {
        var p = Preferences.Default(); p.ShelfPins.Add(new Pin { Name = "旧收纳", Path = @"C:\fixture\old.txt" }); p.Upgrade(); Require(p.ShelfPins.Count == 0 && p.ShelfBoxes[0].Pins.Count == 1, "旧收纳项目迁移且不丢失"); p.Upgrade(); Require(p.ShelfBoxes[0].Pins.Count == 1, "迁移幂等");
        p.ShelfBoxes.Add(new ShelfBoxPreference { Title = "第二盒", Width = 450, Height = 310, IconSize = 64, ShowTitle = false, Locked = true, X = 30, Y = 42 });
        foreach (var spectrum in WidgetStyles.Spectrum) foreach (var clock in WidgetStyles.Clock) { p.Spectrum.Style = spectrum.Key; p.ClockStyle = clock.Key; p.Validate(); var copy = p.Clone(); Require(copy.ShelfBoxes[1].Width == 450 && !copy.ShelfBoxes[1].ShowTitle && copy.ShelfBoxes[1].Locked && copy.Spectrum.Style == spectrum.Key && copy.ClockStyle == clock.Key, "多收纳盒与样式往返"); }
        var pin = new Pin { Name = "托盘应用", Path = @"C:\fixture\tray.exe" }; p.Pins.Clear(); p.Pins.Add(pin); var tray = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase) { pin.Path };
        var entries = DockItems.Build(p, Array.Empty<WindowEntry>(), runningPaths: tray); Require(entries.Single(e => e.Pin?.Id == pin.Id).Running && !entries.Any(e => e.SectionBreakBefore), "仅托盘运行保留小点但不增加临时窗口"); Require(!DockItems.Build(p, Array.Empty<WindowEntry>()).Single(e => e.Pin?.Id == pin.Id).Running, "进程退出小点熄灭");
        var motion = new IslandMotion(); motion.Retarget(); Require(motion.Height.Target == 30 && motion.Width.Target == 120 && motion.Hide.Value == 1, "不接触时顶部短条"); motion.HasMedia = true; motion.Retarget(); Require(motion.Hide.Value == 1, "未触发播放仍保持顶部收纳"); motion.Enter(0); motion.Click(.1); motion.Leave(.2); motion.Step(4, .02, true); Require(!motion.Expanded && motion.Hide.Value == 1, "离开后回到顶部收纳");
    }
    static void VerifyDesktop06()
    {
        var prefs = Preferences.Default(); prefs.ShowLaunchpad = false; var items = DockItems.Build(prefs, Array.Empty<WindowEntry>()); Require(!items.Any(i => i.Path == "@launchpad") && items.First().Pin != null, "隐藏启动台不留空槽"); prefs.ShowLaunchpad = true; Require(DockItems.Build(prefs, Array.Empty<WindowEntry>()).First().Path == "@launchpad", "重新显示启动台");
        prefs.Spectrum.Color = "white"; prefs.Spectrum.HideWhenSilent = true; prefs.Spectrum.SilenceThresholdDb = -65; prefs.Validate(); var copy = prefs.Clone(); Require(copy.Spectrum.Color == "white" && copy.Spectrum.HideWhenSilent && copy.Spectrum.SilenceThresholdDb == -65, "白色与静音选项持久化");
        var box = new ShelfBoxPreference(); foreach (bool title in new[] { false, true }) foreach (bool divider in new[] { false, true }) { box.ShowTitle = title; box.ShowDivider = divider; prefs.ShelfBoxes[0] = box; var read = prefs.Clone().ShelfBoxes[0]; Require(read.ShowTitle == title && read.ShowDivider == divider && WidgetWindow.ShelfHeaderHeight(read) >= 12, "标题横线四种独立组合"); }
        var gate = new AudioVisibilityGate(); for (int i = 0; i < 60; i++) gate.Step(-120, 1.0 / 60, -60); Require(gate.Gain == 0, "静音完全透明"); gate.Step(-50, .04, -60); Require(gate.Open && gate.Gain > .5, "音频超过开启阈值快速出现"); gate.Step(-57, .25, -60); Require(gate.Open, "双阈值中间区域不抖动"); gate.Step(-80, .1, -60); Require(gate.Open, "静音保持时间抑制短暂停顿"); for (int i = 0; i < 120; i++) gate.Step(-120, 1.0 / 60, -60); Require(!gate.Open && gate.Gain == 0, "保持和淡出后没有残留线");
        var island = new IslandMotion(); island.Media(true, true, "播放样本", 0); island.Step(.05, .05, true); Require(island.Width.Target == 236 && island.Height.Target == 34 && island.Hide.Value == 0, "播放事件唤出原版媒体胶囊"); island.Media(true, true, "播放样本", 1); island.Step(3.1, .05, true); Require(island.Hide.Value == 1, "持续播放不无限重置收纳计时"); island.Media(true, true, "新曲目", 4); island.Step(4.1, .05, true); Require(island.Hide.Value == 0, "切歌重新显示");
    }
    static void Require(bool value, string label) { if (!value) throw new Exception("验证失败：" + label); }
}
