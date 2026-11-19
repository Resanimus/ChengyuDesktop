using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ChengyuDock;

// The supplied 1.4.1 engine owns the shell manipulation. A parent handle and a stop event
// ensure it restores the shell both on normal shutdown and after a host crash.
public sealed class TaskbarGuardService : IDisposable
{
    readonly SemaphoreSlim _serial = new(1, 1);
    readonly IntPtr _fixture;
    Process? _process;
    EventWaitHandle? _stop, _ready;
    Semaphore? _session;
    bool _stopping, _disposed;
    public event Action<string>? Failed;
    public bool Running => _process is { HasExited: false };
    internal int ProcessId => Running ? _process!.Id : 0;
    public string Status => Running ? (AutoHideEnabled ? "已运行 · 触底或按 Win 键唤出任务栏" : "等待 Windows 开启「自动隐藏任务栏」") : "关闭后由 Windows 正常管理任务栏";
    public TaskbarGuardService() { }
    internal TaskbarGuardService(IntPtr fixture) => _fixture = fixture;

    public async Task SetEnabledAsync(bool enabled)
    {
        await _serial.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!enabled) { await StopCore(); return; }
            if (Running) return;
            ReleaseHandles();
            if (_fixture == IntPtr.Zero)
            {
                if (StandaloneRunning()) throw new InvalidOperationException("独立版任务栏组件正在运行。请先从它的托盘菜单退出，再开启此开关。");
                _session = new Semaphore(1, 1, @"Local\ChengyuDesktop-TaskbarGuard");
                if (!_session.WaitOne(0)) { _session.Dispose(); _session = null; throw new InvalidOperationException("另一份澄屿已在管理任务栏，请先关闭它的任务栏开关。"); }
            }
            try
            {
                string assets = Path.Combine(AppContext.BaseDirectory, "Assets", "TaskbarGuard");
                string runtime = Path.Combine(assets, "runtime", "AutoHotkey64.exe"), script = Path.Combine(assets, "ChengyuTaskbarGuard.ahk");
                if (!File.Exists(runtime) || !File.Exists(script)) throw new FileNotFoundException("任务栏组件文件不完整，请重新解压整个便携包。");
                string id = Guid.NewGuid().ToString("N"), stopName = @"Local\ChengyuTaskbarStop-" + id, readyName = @"Local\ChengyuTaskbarReady-" + id;
                _stop = new EventWaitHandle(false, EventResetMode.ManualReset, stopName);
                _ready = new EventWaitHandle(false, EventResetMode.ManualReset, readyName);
                var start = new ProcessStartInfo(runtime) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = assets, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (string argument in new[] { "/ErrorStdOut=UTF-8", script, "--host", Environment.ProcessId.ToString(), stopName, readyName }) start.ArgumentList.Add(argument);
                if (_fixture != IntPtr.Zero) { start.ArgumentList.Add("--fixture"); start.ArgumentList.Add(_fixture.ToInt64().ToString()); }
                _stopping = false;
                var process = new Process { StartInfo = start, EnableRaisingEvents = true }; _process = process;
                process.Exited += (_, _) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    if (!_stopping && !_disposed && ReferenceEquals(_process, process))
                        Failed?.Invoke("任务栏组件已退出，遮挡已解除。可重新开启开关。");
                });
                process.Start();
                var ready = _ready;
                using var exit = process.SafeHandleAsWaitHandle();
                bool signaled = await Task.Run(() => WaitHandle.WaitAny(new WaitHandle[] { ready, exit }, 5000) == 0);
                if (!signaled || process.HasExited) throw new IOException("任务栏组件未能启动，请检查便携包是否完整。");
            }
            catch { await StopCore(); throw; }
        }
        finally { _serial.Release(); }
    }
    async Task StopCore()
    {
        _stopping = true; _stop?.Set();
        if (_process is { } process)
        {
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(4)); }
            catch (TimeoutException) { throw new IOException("任务栏组件尚未完成恢复，请稍后再试。"); }
        }
        ReleaseHandles();
    }
    void ReleaseHandles()
    {
        _process?.Dispose(); _process = null;
        _stop?.Dispose(); _stop = null; _ready?.Dispose(); _ready = null;
        if (_session != null) { _session.Release(); _session.Dispose(); _session = null; }
    }
    public void Dispose()
    {
        _disposed = true; _stopping = true; _stop?.Set();
        // The child also owns a handle to this process, so it can clean up after an abrupt exit.
        if (!Running) ReleaseHandles();
    }
    public static bool AutoHideEnabled
    {
        get { var data = new AppBarData { Size = (uint)Marshal.SizeOf<AppBarData>() }; return (SHAppBarMessage(4, ref data).ToUInt64() & 1) != 0; }
    }
    internal static bool StandaloneRunning()
    {
        bool found = false;
        EnumWindows((hwnd, _) =>
        {
            var cls = new StringBuilder(80); GetClassName(hwnd, cls, cls.Capacity);
            if (cls.ToString() != "AutoHotkey") return true;
            var title = new StringBuilder(2048); GetWindowText(hwnd, title, title.Capacity);
            string text = title.ToString(); int suffix = text.IndexOf(" - AutoHotkey", StringComparison.OrdinalIgnoreCase);
            found = suffix > 0 && string.Equals(Path.GetFileName(text[..suffix]), "TaskbarGuard.ahk", StringComparison.OrdinalIgnoreCase);
            return !found;
        }, IntPtr.Zero);
        return found;
    }
    [StructLayout(LayoutKind.Sequential)] struct AppBarData { public uint Size; public IntPtr Hwnd; public uint Callback, Edge; public int Left, Top, Right, Bottom; public IntPtr Param; }
    delegate bool EnumProc(IntPtr hwnd, IntPtr data);
    [DllImport("shell32.dll")] static extern UIntPtr SHAppBarMessage(uint message, ref AppBarData data);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int count);
}

internal static class ProcessWaitHandle
{
    internal static WaitHandle SafeHandleAsWaitHandle(this Process process)
    {
        // Do not transfer ownership of the process handle to the WaitHandle.
        return new BorrowedProcessWaitHandle(process.Handle);
    }
    sealed class BorrowedProcessWaitHandle : WaitHandle
    {
        internal BorrowedProcessWaitHandle(IntPtr handle) => SafeWaitHandle = new Microsoft.Win32.SafeHandles.SafeWaitHandle(handle, false);
    }
}
