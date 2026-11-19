using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace ChengyuDock;
internal sealed class WindowEventMonitor : IDisposable
{
    delegate void EventCallback(IntPtr hook, uint type, IntPtr window, int objectId, int childId, uint thread, uint time);
    [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint first, uint last, IntPtr module, EventCallback callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
    readonly EventCallback _callback; readonly List<IntPtr> _hooks = new();
    readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(100) };
    public WindowEventMonitor(Action refresh)
    {
        _debounce.Tick += (_, _) => { _debounce.Stop(); refresh(); };
        _callback = (_, type, hwnd, obj, child, _, _) => { if (hwnd == IntPtr.Zero || type >= 0x8000 && (obj != 0 || child != 0)) return; App.Current.Dispatcher.BeginInvoke(() => { if (!_disposed && !_debounce.IsEnabled) _debounce.Start(); }); };
        foreach (var range in new[] { (3u, 3u), (0x8000u, 0x8003u), (0x16u, 0x17u) }) { var hook = SetWinEventHook(range.Item1, range.Item2, IntPtr.Zero, _callback, 0, 0, 2); if (hook != IntPtr.Zero) _hooks.Add(hook); }
    }
    bool _disposed;
    public void Dispose() { _disposed = true; _debounce.Stop(); foreach (var h in _hooks) UnhookWinEvent(h); _hooks.Clear(); }
}
