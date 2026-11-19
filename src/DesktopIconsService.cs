using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace ChengyuDock;
internal sealed class DoubleClickTracker
{
    uint _time; Native.NativePoint _point; IntPtr _window; bool _hasClick;
    public bool Push(uint time, Native.NativePoint point, IntPtr window, bool blank, uint delay, int width, int height)
    {
        bool result = _hasClick && blank && window == _window && unchecked(time - _time) <= delay && Math.Abs(point.X - _point.X) <= width / 2 && Math.Abs(point.Y - _point.Y) <= height / 2;
        _hasClick = blank && !result; _time = time; _point = point; _window = window; return result;
    }
}
public sealed class DesktopIconsService : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] struct MouseData { public Native.NativePoint Point; public uint Data, Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] struct LastInput { public uint Size, Time; }
    delegate IntPtr MouseHook(int code, IntPtr message, IntPtr data);
    delegate bool EnumWindow(IntPtr window, IntPtr data);
    [DllImport("user32.dll")] static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindow callback, IntPtr data);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumWindow callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr window, StringBuilder value, int size);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Native.NativePoint point);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] static extern bool ShowWindowAsync(IntPtr window, int command);
    [DllImport("user32.dll")] static extern uint GetDoubleClickTime();
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int metric);
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LastInput input);
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, MouseHook callback, IntPtr module, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);
    [DllImport("oleacc.dll")] static extern int AccessibleObjectFromWindow(IntPtr window, uint objectId, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out Accessibility.IAccessible accessible);
    readonly MouseHook _callback;
    readonly DoubleClickTracker _click = new();
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    IntPtr _hook, _icons, _root; bool _initialVisible, _changed, _autoHidden, _disposed; uint _hiddenAtInput;
    public bool HookInstalled => _hook != IntPtr.Zero;
    public bool IconsFound => Native.IsWindow(_icons);
    public DesktopIconsService() { _callback = OnMouse; App.Current.Changed += Sync; _timer.Tick += (_, _) => Tick(); _timer.Start(); FindIcons(); Sync(); }
    static string Class(IntPtr window) { var b = new StringBuilder(128); GetClassName(window, b, b.Capacity); return b.ToString(); }
    bool FindIcons()
    {
        if (Native.IsWindow(_icons)) return true; _icons = _root = IntPtr.Zero; GetWindowThreadProcessId(GetShellWindow(), out uint explorer); if (explorer == 0) return false;
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out uint pid); if (pid != explorer || Class(h) is not ("Progman" or "WorkerW")) return true;
            var view = DesktopLayer.FindWindowEx(h, IntPtr.Zero, "SHELLDLL_DefView", null); if (view == IntPtr.Zero) return true;
            var list = DesktopLayer.FindWindowEx(view, IntPtr.Zero, "SysListView32", null);
            if (list == IntPtr.Zero) EnumChildWindows(view, (child, _) => { if (Class(child) == "SysListView32") { list = child; return false; } return true; }, IntPtr.Zero);
            if (list == IntPtr.Zero) return true; _icons = list; _root = h; return false;
        }, IntPtr.Zero);
        if (_icons != IntPtr.Zero) { _initialVisible = Native.IsWindowVisible(_icons); _changed = _autoHidden = false; } return _icons != IntPtr.Zero;
    }
    void Sync()
    {
        if (App.Current.Prefs.DesktopIcons.DoubleClick && _hook == IntPtr.Zero) { _hook = SetWindowsHookEx(14, _callback, GetModuleHandle(null), 0); if (_hook == IntPtr.Zero) App.Current.Notify("桌面双击功能未能启用"); }
        else if (!App.Current.Prefs.DesktopIcons.DoubleClick && _hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
    }
    IntPtr OnMouse(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && message.ToInt64() == 0x201 && !_disposed)
        {
            var mouse = Marshal.PtrToStructure<MouseData>(data);
            if ((mouse.Flags & 1) == 0) { var hit = WindowFromPoint(mouse.Point); Application.Current.Dispatcher.BeginInvoke(() => Down(mouse, hit)); }
        }
        return CallNextHookEx(_hook, code, message, data);
    }
    bool Blank(Native.NativePoint point, IntPtr hit)
    {
        if (!FindIcons() || GetAncestor(hit, 2) != _root || WindowFromPoint(point) != hit) return false;
        if (!Native.IsWindowVisible(_icons)) return true;
        if (hit != _icons && Class(hit) is not ("SHELLDLL_DefView" or "WorkerW" or "Progman")) return false;
        Accessibility.IAccessible? acc = null; object? child = null;
        try { Guid iid = new("618736E0-3C3D-11CF-810C-00AA00389B71"); if (AccessibleObjectFromWindow(_icons, unchecked((uint)-4), ref iid, out acc) < 0) return false; child = acc.accHitTest(point.X, point.Y); return child is int id && id == 0; }
        catch (COMException) { return false; }
        finally { if (child != null && Marshal.IsComObject(child) && !ReferenceEquals(child, acc)) Marshal.ReleaseComObject(child); if (acc != null && Marshal.IsComObject(acc)) Marshal.ReleaseComObject(acc); }
    }
    void Down(MouseData mouse, IntPtr hit)
    {
        if (_disposed || !App.Current.Prefs.DesktopIcons.DoubleClick) return;
        if (_click.Push(mouse.Time, mouse.Point, hit, Blank(mouse.Point, hit), GetDoubleClickTime(), GetSystemMetrics(36), GetSystemMetrics(37))) { _autoHidden = false; SetVisible(!Native.IsWindowVisible(_icons)); }
    }
    void SetVisible(bool visible) { if (!FindIcons()) return; ShowWindowAsync(_icons, visible ? 8 : 0); _changed = true; }
    public void Toggle() { if (FindIcons()) { _autoHidden = false; SetVisible(!Native.IsWindowVisible(_icons)); } }
    void Tick()
    {
        if (_disposed) return; var p = App.Current.Prefs.DesktopIcons; if (!p.AutoHide && !_autoHidden) return;
        if (!FindIcons()) return; var input = new LastInput { Size = (uint)Marshal.SizeOf<LastInput>() }; if (!GetLastInputInfo(ref input)) return;
        if (_autoHidden && input.Time != _hiddenAtInput) { if (p.RestoreOnInput) SetVisible(true); _autoHidden = false; return; }
        uint idle = unchecked((uint)Environment.TickCount - input.Time);
        if (p.AutoHide && Native.IsWindowVisible(_icons) && idle >= p.DelaySeconds * 1000) { _hiddenAtInput = input.Time; _autoHidden = true; SetVisible(false); }
    }
    public void Dispose() { _disposed = true; _timer.Stop(); App.Current.Changed -= Sync; if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook); if (_changed && Native.IsWindow(_icons)) ShowWindowAsync(_icons, _initialVisible ? 8 : 0); _hook = IntPtr.Zero; }
}
