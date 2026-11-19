using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace ChengyuDock;

// Use a named pop-out so existing Wallpaper Engine monitor assignments remain independent.
internal sealed class EngineWallpaper : IDisposable
{
    readonly string _name = "ChengyuWallpaper-" + Guid.NewGuid().ToString("N");
    IntPtr _window;
    string _engine = "";
    string _playback = "";
    CancellationTokenSource? _properties;
    bool _disposed;
    bool _openIssued;
    internal bool Attached => _window != IntPtr.Zero && Native.IsWindow(_window);
    internal IntPtr WindowHandle => _window;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string? cls, string name);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr window, uint message, IntPtr w, IntPtr l);

    internal static bool ValidExecutable(string path) => File.Exists(path) &&
        System.IO.Path.GetFileName(path).ToLowerInvariant() is "wallpaper32.exe" or "wallpaper64.exe";

    internal static string[] Arguments(string project, string name, int width, int height) => new[]
    {
        "-control", "openWallpaper", "-file", project, "-playInWindow", name,
        "-width", width.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "-height", height.ToString(System.Globalization.CultureInfo.InvariantCulture), "-borderless"
    };

    internal async Task Open(string engine, string project, CancellationToken cancellation)
    {
        if (!ValidExecutable(engine)) throw new FileNotFoundException("请先设置 Wallpaper Engine 程序路径。");
        engine = System.IO.Path.GetFullPath(engine);
        _engine = engine;
        bool running = false;
        foreach (var process in Process.GetProcessesByName(System.IO.Path.GetFileNameWithoutExtension(engine)))
            using (process)
            {
                try { running |= string.Equals(process.MainModule?.FileName, engine, StringComparison.OrdinalIgnoreCase); }
                catch (System.ComponentModel.Win32Exception) { }
            }
        if (!running)
        {
            using var started = Process.Start(new ProcessStartInfo(engine)
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = System.IO.Path.GetDirectoryName(engine)!
            });
            await Task.Delay(1800, cancellation);
        }
        double scale = App.Current.MainWindow == null ? 1 :
            System.Windows.Media.VisualTreeHelper.GetDpi(App.Current.MainWindow).DpiScaleX;
        int width = (int)Math.Round(SystemParameters.PrimaryScreenWidth * scale);
        int height = (int)Math.Round(SystemParameters.PrimaryScreenHeight * scale);
        _openIssued = true;
        await Command(Arguments(project, _name, width, height), cancellation);
        for (int i = 0; i < 100; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            _window = FindWindow(null, _name);
            if (_window != IntPtr.Zero)
            {
                WallpaperWindow.AttachToDesktop(_window, width, height);
                return;
            }
            await Task.Delay(100, cancellation);
        }
        throw new TimeoutException("Wallpaper Engine 尚未创建壁纸窗口，请确认它已启动且该项目可以正常播放。");
    }

    async Task Command(string[] arguments, CancellationToken cancellation)
    {
        var start = new ProcessStartInfo(_engine)
        {
            UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = System.IO.Path.GetDirectoryName(_engine)!
        };
        if (arguments.Contains("-properties"))
            start.Arguments = string.Join(" ", arguments.Select(argument =>
                argument.StartsWith("RAW~(", StringComparison.Ordinal) ? argument : StartupRegistration.Quote(argument)));
        else
            foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var commandProcess = Process.Start(start) ?? throw new IOException("无法联系 Wallpaper Engine。");
        await commandProcess.WaitForExitAsync(cancellation).WaitAsync(TimeSpan.FromSeconds(8), cancellation);
        if (commandProcess.ExitCode != 0) throw new IOException("Wallpaper Engine 控制命令失败，返回码 " + commandProcess.ExitCode + "。");
    }

    internal async void ApplyPlayback(WallpaperPreference preference)
    {
        _properties?.Cancel();
        var cancellation = new CancellationTokenSource();
        _properties = cancellation;
        try
        {
            await Task.Delay(150, cancellation.Token);
            await ApplyPlaybackAsync(preference, cancellation.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { App.Current.Notify("Wallpaper Engine 播放设置未能应用：" + ex.Message); }
        finally { if (ReferenceEquals(_properties, cancellation)) _properties = null; cancellation.Dispose(); }
    }

    internal async Task ApplyPlaybackAsync(WallpaperPreference preference, CancellationToken cancellation)
    {
        if (_disposed) return;
        string json = JsonSerializer.Serialize(new { rate = preference.PlaybackRate, volume = preference.AudioEnabled ? preference.Volume : 0 });
        if (_playback == json) return;
        await Command(new[] { "-control", "applyProperties", "-properties", "RAW~(" + json + ")~END", "-location", _name }, cancellation);
        _playback = json;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _properties?.Cancel();
        if (_openIssued && ValidExecutable(_engine))
        {
            try
            {
                var close = new ProcessStartInfo(_engine) { UseShellExecute = false, CreateNoWindow = true };
                foreach (string argument in new[] { "-control", "closeWallpaper", "-location", _name })
                    close.ArgumentList.Add(argument);
                Process.Start(close)?.Dispose();
            }
            catch (Exception ex) { App.Current.ReportBackgroundError("关闭 Wallpaper Engine 壁纸", ex); }
        }
        IntPtr window = _window != IntPtr.Zero ? _window : FindWindow(null, _name);
        if (window != IntPtr.Zero && Native.IsWindow(window)) PostMessage(window, 0x10, IntPtr.Zero, IntPtr.Zero);
        _window = IntPtr.Zero;
    }
}
