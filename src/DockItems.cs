using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace ChengyuDock;
internal static class DockItems
{
    static readonly Dictionary<string, string> Targets = new(StringComparer.OrdinalIgnoreCase);
    internal static string Target(string path)
    {
        path = PackagedApps.UpgradeLegacyTarget(path);
        if (path.Equals("ms-settings:", StringComparison.OrdinalIgnoreCase)) return "@systemsettings";
        if (Path.GetFileName(path).Equals("calc.exe", StringComparison.OrdinalIgnoreCase)) return "@calculator";
        if (!path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return path;
        if (Targets.TryGetValue(path, out string? target)) return target;
        object? shell = null, shortcut = null;
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell"); if (type == null) return path;
            shell = Activator.CreateInstance(type); shortcut = type.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { path });
            target = shortcut?.GetType().InvokeMember("TargetPath", System.Reflection.BindingFlags.GetProperty, null, shortcut, null)?.ToString();
        }
        catch { target = path; }
        finally { if (shortcut != null && Marshal.IsComObject(shortcut)) Marshal.ReleaseComObject(shortcut); if (shell != null && Marshal.IsComObject(shell)) Marshal.ReleaseComObject(shell); }
        return Targets[path] = string.IsNullOrWhiteSpace(target) ? path : PackagedApps.UpgradeLegacyTarget(target);
    }
    internal static bool Matches(Pin pin, WindowEntry window)
    {
        string target = Target(pin.Path);
        if (PackagedApps.TryGetId(target, out _))
            return string.Equals(target, PackagedApps.WindowTarget(window), StringComparison.OrdinalIgnoreCase);
        string exe = Path.GetFileName(window.Exe);
        if (target == "@systemsettings") return exe.Equals("SystemSettings.exe", StringComparison.OrdinalIgnoreCase) || exe.Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase) && window.Title is "设置" or "Settings";
        if (target == "@calculator") return exe.Equals("CalculatorApp.exe", StringComparison.OrdinalIgnoreCase) || exe.Equals("Calculator.exe", StringComparison.OrdinalIgnoreCase) || exe.Equals("calc.exe", StringComparison.OrdinalIgnoreCase) || exe.Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase) && window.Title is "计算器" or "Calculator";
        if (Path.GetFileName(target).Equals("notepad.exe", StringComparison.OrdinalIgnoreCase) && target.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase)) return exe.Equals("notepad.exe", StringComparison.OrdinalIgnoreCase);
        return !string.IsNullOrEmpty(window.Exe) && string.Equals(target, window.Exe, StringComparison.OrdinalIgnoreCase);
    }
    internal static List<DockEntry> Build(Preferences prefs, IReadOnlyList<WindowEntry> windows, bool preview = false, IReadOnlySet<string>? runningPaths = null)
    {
        var entries = new List<DockEntry>();
        if (prefs.ShowLaunchpad) entries.Add(new DockEntry { Key = "@launchpad", Name = "启动台", Path = "@launchpad" });
        foreach (var pin in prefs.Pins)
        {
            var group = windows.Where(w => Matches(pin, w)).ToArray(); var match = group.FirstOrDefault(w => w.Handle == Native.GetForegroundWindow()) ?? group.FirstOrDefault();
            entries.Add(new DockEntry { Key = pin.Id, Name = pin.Name, Path = pin.Path, Pin = pin, Window = match?.Handle ?? IntPtr.Zero, Running = group.Length > 0 || runningPaths?.Contains(Target(pin.Path)) == true, Windows = group });
        }
        entries.Add(new DockEntry { Key = "@manage", Name = "澄屿桌面", Path = "@manage", Running = App.Current?.Manager?.IsVisible == true });
        if (!preview && prefs.ShowRunningApps)
        {
            var temporary = windows.Where(w => !prefs.Pins.Any(p => Matches(p, w)))
                .GroupBy(w => PackagedApps.WindowTarget(w) is { Length: > 0 } target ? target : "hwnd-" + w.Handle, StringComparer.OrdinalIgnoreCase);
            foreach (var group in temporary)
            {
                var match = group.FirstOrDefault(w => w.Handle == Native.GetForegroundWindow()) ?? group.First();
                entries.Add(new DockEntry { Key = "running-" + group.Key.ToLowerInvariant(), Name = match.Title, Path = PackagedApps.WindowTarget(match), Window = match.Handle, Windows = group.ToArray(), Running = true, SectionBreakBefore = !entries.Any(e => e.SectionBreakBefore) });
            }
        }
        return entries;
    }
    static HashSet<string> _processPaths = new(StringComparer.OrdinalIgnoreCase);
    static long _lastProcessScan;
    internal static HashSet<string> RunningPaths()
    {
        if (Environment.TickCount64 - _lastProcessScan < 1500) return _processPaths; _lastProcessScan = Environment.TickCount64;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses()) using (process) { try { var path = process.MainModule?.FileName; if (!string.IsNullOrEmpty(path)) paths.Add(PackagedApps.UpgradeLegacyTarget(path)); } catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { } }
        return _processPaths = paths;
    }
    internal static bool DragRemoves(PointLike cursor, System.Windows.Rect dock, double threshold = 90)
    { double dx = Math.Max(dock.Left - cursor.X, Math.Max(0, cursor.X - dock.Right)); double dy = Math.Max(dock.Top - cursor.Y, Math.Max(0, cursor.Y - dock.Bottom)); return Math.Sqrt(dx * dx + dy * dy) > threshold; }
}
internal readonly record struct PointLike(double X, double Y);
