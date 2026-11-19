using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Microsoft.Win32;

namespace ChengyuDock;

internal static class SystemIntegrationVerification
{
    internal static async void RunHostFixture(IntPtr hwnd, string readyFile)
    {
        var service = new TaskbarGuardService(hwnd);
        try
        {
            await service.SetEnabledAsync(true);
            File.WriteAllText(readyFile, service.ProcessId.ToString());
        }
        catch (Exception ex) { File.WriteAllText(readyFile + ".error", ex.ToString()); App.Current.Shutdown(1); }
    }
    internal static async Task Run()
    {
        var app = App.Current; var checks = new Dictionary<string, bool>();
        string key = @"Software\ChengyuDesktop\Verification-" + Guid.NewGuid().ToString("N");
        var startup = app.WindowsStartup;
        Window? fixture = null; TaskbarGuardService? service = null; Process? host = null, conflict = null;
        try
        {
            await Task.Delay(400);
            checks["autostart_keeps_manager_hidden"] = Environment.GetCommandLineArgs().Contains("--autostart") && !app.Manager.IsVisible;
            checks["autostart_still_shows_desktop_and_dock"] = app.Desktop.ComponentCount > 0 && app.Dock.IsVisible;
            checks["taskbar_setting_defaults_off"] = !new Preferences().TaskbarGuardEnabled;
            var duplicateInfo = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            foreach (string a in new[] { "--autostart", "--data-dir", app.Store.Root }) duplicateInfo.ArgumentList.Add(a);
            using (var duplicate = Process.Start(duplicateInfo)!)
            {
                try { await duplicate.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(4)); checks["duplicate_autostart_exits_silently"] = duplicate.ExitCode == 0; }
                catch (TimeoutException) { duplicate.Kill(); checks["duplicate_autostart_exits_silently"] = false; }
            }
            var roundTrip = app.Prefs.Clone(); roundTrip.TaskbarGuardEnabled = true;
            var testStore = new SettingsStore(Path.Combine(app.Store.Root, "guard-setting")); testStore.Save(roundTrip);
            checks["taskbar_choice_survives_restart"] = testStore.Load().TaskbarGuardEnabled;

            app.WindowsStartup = new StartupRegistration(Environment.ProcessPath!, app.Store.Root, key, "Startup");
            using (var temp = Registry.CurrentUser.CreateSubKey(key)) temp.SetValue("Unrelated", "preserve-me");
            app.ShowManager(); app.Manager.Navigate("settings");
            var toggles = Descendants<CheckBox>((DependencyObject)app.Manager.Content).ToList();
            var startToggle = toggles.Single(c => System.Windows.Automation.AutomationProperties.GetName(c) == "开机自动启动");
            var guardToggle = toggles.Single(c => System.Windows.Automation.AutomationProperties.GetName(c) == "任务栏按需显示");
            checks["both_switches_in_settings"] = !startToggle.IsChecked.GetValueOrDefault() && !guardToggle.IsChecked.GetValueOrDefault();
            startToggle.IsChecked = true; startToggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            checks["startup_toggle_writes_and_reads_registry"] = app.WindowsStartup.Enabled && app.WindowsStartup.UsesCurrentCopy && startToggle.IsChecked == true;
            string registered = app.WindowsStartup.RegisteredCommand;
            var parsed = ParseCommand(registered);
            checks["startup_command_preserves_executable_and_data_path"] = parsed.SequenceEqual(new[] { Environment.ProcessPath!, "--autostart", "--data-dir", app.Store.Root });
            var quoted = new[] { "C:\\Program Files\\澄屿\\ChengyuDock.exe", "--data-dir", "D:\\桌面设置 带空格\\" };
            checks["startup_quoting_handles_spaces_chinese_trailing_slash"] = ParseCommand(string.Join(" ", quoted.Select(StartupRegistration.Quote))).SequenceEqual(quoted);
            startToggle.IsChecked = false; startToggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            checks["startup_toggle_removes_own_entry"] = !app.WindowsStartup.Enabled && startToggle.IsChecked == false;
            using (var temp = Registry.CurrentUser.OpenSubKey(key)) checks["startup_leaves_other_entries_intact"] = Equals(temp?.GetValue("Unrelated"), "preserve-me");
            app.CaptureManager(Path.Combine(app.Store.Root, "settings-system.png"));

            if (!TaskbarGuardService.StandaloneRunning())
            {
                string stub = Path.Combine(app.Store.Root, "TaskbarGuard.ahk");
                File.WriteAllText(stub, "#Requires AutoHotkey v2.0\n#SingleInstance Off\n#NoTrayIcon\nPersistent(true)\n");
                var info = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Assets", "TaskbarGuard", "runtime", "AutoHotkey64.exe")) { UseShellExecute = false, CreateNoWindow = true };
                info.ArgumentList.Add("/ErrorStdOut=UTF-8"); info.ArgumentList.Add(stub);
                conflict = Process.Start(info);
                for (int i = 0; i < 40 && !TaskbarGuardService.StandaloneRunning(); i++) await Task.Delay(50);
            }
            checks["standalone_conflict_detected"] = TaskbarGuardService.StandaloneRunning();
            if (checks["standalone_conflict_detected"])
            {
                guardToggle.IsChecked = true; guardToggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                for (int i = 0; i < 20 && app.TaskbarBusy; i++) await Task.Delay(50);
                checks["standalone_conflict_rolls_switch_back"] = !app.TaskbarGuard.Running && !app.Prefs.TaskbarGuardEnabled && guardToggle.IsChecked == false;
            }
            if (conflict is { HasExited: false }) { conflict.Kill(); await conflict.WaitForExitAsync(); }
            conflict?.Dispose(); conflict = null;
            if (Environment.GetCommandLineArgs().Contains("--live-taskbar-test") && !TaskbarGuardService.StandaloneRunning())
            {
                bool autoHideBefore = TaskbarGuardService.AutoHideEnabled;
                try
                {
                    guardToggle.IsChecked = true; guardToggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    for (int i = 0; i < 120 && app.TaskbarBusy; i++) await Task.Delay(50);
                    checks["live_taskbar_ui_switch_starts_engine"] = app.TaskbarGuard.Running && app.Prefs.TaskbarGuardEnabled && guardToggle.IsChecked == true;
                    checks["live_taskbar_preserves_system_autohide_choice"] = TaskbarGuardService.AutoHideEnabled == autoHideBefore;
                    await Task.Delay(800);
                }
                finally
                {
                    await app.SetTaskbarEnabledAsync(false);
                    var bar = FindWindow("Shell_TrayWnd", null);
                    checks["live_taskbar_off_restores_region_and_clears_fade_marker"] = !app.TaskbarGuard.Running && !app.Prefs.TaskbarGuardEnabled && guardToggle.IsChecked == false &&
                        bar != IntPtr.Zero && RegionKind(bar) == 0 && GetProp(bar, "ChengyuTaskbar_Clipped") == IntPtr.Zero && GetProp(bar, "ChengyuTaskbar_Fading") == IntPtr.Zero;
                }
            }
            fixture = new Window { Title = "澄屿 · 任务栏引擎验证样本", Width = 400, Height = 100, ShowActivated = false, ShowInTaskbar = false, Background = System.Windows.Media.Brushes.MidnightBlue };
            fixture.Show(); var hwnd = new WindowInteropHelper(fixture).Handle;
            service = new TaskbarGuardService(hwnd);
            await service.SetEnabledAsync(true); int pid = service.ProcessId;
            checks["hosted_engine_starts_and_masks_fixture"] = pid != 0 && RegionKind(hwnd) == 1;
            await service.SetEnabledAsync(true);
            checks["repeated_enable_uses_single_child"] = service.ProcessId == pid;
            await service.SetEnabledAsync(false);
            checks["off_exits_child_and_restores_region"] = !service.Running && RegionKind(hwnd) == 0 && !ProcessExists(pid);
            await service.SetEnabledAsync(true); int second = service.ProcessId;
            checks["engine_can_restart_after_disabled"] = second != 0 && second != pid && RegionKind(hwnd) == 1;
            await service.SetEnabledAsync(false); service.Dispose(); service = null;

            string ready = Path.Combine(app.Store.Root, "host-fixture-ready.txt");
            var args = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            foreach (string a in new[] { "--taskbar-host-fixture", hwnd.ToInt64().ToString(), ready }) args.ArgumentList.Add(a);
            host = Process.Start(args)!;
            for (int i = 0; i < 80 && !File.Exists(ready) && !host.HasExited; i++) await Task.Delay(100);
            int orphanPid = int.Parse(File.ReadAllText(ready));
            checks["crash_fixture_was_masked"] = RegionKind(hwnd) == 1;
            host.Kill(); await host.WaitForExitAsync();
            for (int i = 0; i < 60 && ProcessExists(orphanPid); i++) await Task.Delay(50);
            checks["host_crash_exits_child_and_restores_region"] = !ProcessExists(orphanPid) && RegionKind(hwnd) == 0;
        }
        catch (Exception ex) { checks["exception"] = false; File.WriteAllText(Path.Combine(app.Store.Root, "system-test-error.txt"), ex.ToString()); }
        finally
        {
            if (host is { HasExited: false }) host.Kill(); host?.Dispose();
            if (conflict is { HasExited: false }) conflict.Kill(); conflict?.Dispose();
            if (service != null) { await service.SetEnabledAsync(false); service.Dispose(); }
            fixture?.Close(); app.WindowsStartup = startup;
            Registry.CurrentUser.DeleteSubKeyTree(key, false);
            File.WriteAllText(Path.Combine(app.Store.Root, "system-test-result.json"), JsonSerializer.Serialize(new { success = checks.Count >= 16 && checks.Values.All(x => x), checks, version = "0.8.0" }, new JsonSerializerOptions { WriteIndented = true }));
            app.Quit();
        }
    }
    static bool ProcessExists(int pid) { try { using var p = Process.GetProcessById(pid); return !p.HasExited; } catch (ArgumentException) { return false; } }
    static int RegionKind(IntPtr hwnd) { var rgn = CreateRectRgn(0, 0, 0, 0); try { return GetWindowRgn(hwnd, rgn); } finally { DeleteObject(rgn); } }
    static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T item) yield return item;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    static string[] ParseCommand(string command)
    {
        var memory = CommandLineToArgvW(command, out int count);
        if (memory == IntPtr.Zero) throw new InvalidOperationException("CommandLineToArgvW failed");
        try { return Enumerable.Range(0, count).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(memory, i * IntPtr.Size))!).ToArray(); }
        finally { LocalFree(memory); }
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CommandLineToArgvW(string command, out int count);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("gdi32.dll")] static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("user32.dll")] static extern int GetWindowRgn(IntPtr hwnd, IntPtr rgn);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetProp(IntPtr hwnd, string name);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr value);
}
