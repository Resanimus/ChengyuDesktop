using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace ChengyuDock;

public sealed record WindowEntry(IntPtr Handle, string Title, string Exe, bool Minimized, string? AppId = null);
internal static class Native
{
    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x8000000;
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr hwnd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, IntPtr data);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc callback, IntPtr data);
    delegate bool EnumProc(IntPtr hwnd, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder text, ref int size);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
    [DllImport("dwmapi.dll")] public static extern int DwmRegisterThumbnail(IntPtr destination, IntPtr source, out IntPtr thumbnail);
    [DllImport("dwmapi.dll")] public static extern int DwmUnregisterThumbnail(IntPtr thumbnail);
    [DllImport("dwmapi.dll")] public static extern int DwmQueryThumbnailSourceSize(IntPtr thumbnail, out NativeSize size);
    [DllImport("dwmapi.dll")] public static extern int DwmUpdateThumbnailProperties(IntPtr thumbnail, ref ThumbnailProperties props);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] public static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] static extern bool SetWindowCompositionAttribute(IntPtr hwnd, ref CompositionData data);
    [StructLayout(LayoutKind.Sequential)] struct AccentPolicy { public int State, Flags, Color, Animation; }
    [StructLayout(LayoutKind.Sequential)] struct CompositionData { public int Attribute; public IntPtr Data; public int Size; }
    [StructLayout(LayoutKind.Sequential)] public struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct NativeSize { public int Width, Height; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)] public struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] public struct ThumbnailProperties
    {
        public uint Flags; public NativeRect Destination, Source; public byte Opacity;
        [MarshalAs(UnmanagedType.Bool)] public bool Visible;
        [MarshalAs(UnmanagedType.Bool)] public bool ClientOnly;
    }
    public static void MakePassive(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64() | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));
        HwndSource.FromHwnd(hwnd)?.AddHook((IntPtr h, int m, IntPtr w, IntPtr l, ref bool handled) =>
        { if (m == 0x21) { handled = true; return new IntPtr(3); } return IntPtr.Zero; });
    }
    public static List<WindowEntry> EnumerateWindows()
    {
        var entries = new List<WindowEntry>();
        var identities = new Dictionary<uint, (string Path, string? AppId)>();

        (string Path, string? AppId) ReadIdentity(uint pid)
        {
            if (identities.TryGetValue(pid, out var known)) return known;
            IntPtr process = OpenProcess(0x1000, false, pid);
            if (process == IntPtr.Zero) return identities[pid] = ("", null);
            try
            {
                var buffer = new StringBuilder(2048);
                int length = buffer.Capacity;
                string path = QueryFullProcessImageName(process, 0, buffer, ref length) ? buffer.ToString() : "";
                return identities[pid] = (path, PackagedApps.ProcessAppId(process));
            }
            finally { CloseHandle(process); }
        }

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || GetWindow(hwnd, 4) != IntPtr.Zero) return true;
            long style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            if ((style & (WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE)) != 0) return true;
            if (DwmGetWindowAttribute(hwnd, 14, out int cloaked, 4) == 0 && cloaked != 0) return true;
            var title = new StringBuilder(512); GetWindowText(hwnd, title, title.Capacity);
            if (title.Length == 0) return true;
            var cls = new StringBuilder(128); GetClassName(hwnd, cls, cls.Capacity);
            if (cls.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "ApplicationFrameInputSinkWindow") return true;
            GetWindowThreadProcessId(hwnd, out uint pid); if (pid == Environment.ProcessId || pid == 0) return true;
            var identity = ReadIdentity(pid);
            if (System.IO.Path.GetFileName(identity.Path).Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
            {
                EnumChildWindows(hwnd, (child, _) =>
                {
                    GetWindowThreadProcessId(child, out uint childPid);
                    if (childPid == pid || childPid == 0) return true;
                    var candidate = ReadIdentity(childPid);
                    if (string.IsNullOrEmpty(candidate.Path) && candidate.AppId == null) return true;
                    identity = candidate;
                    return false;
                }, IntPtr.Zero);
            }
            entries.Add(new WindowEntry(hwnd, title.ToString(), identity.Path, IsIconic(hwnd), identity.AppId));
            return entries.Count < 256;
        }, IntPtr.Zero);
        return entries;
    }
    public static bool Activate(IntPtr hwnd, bool toggle)
    {
        if (!IsWindow(hwnd)) return false;
        if (toggle && !IsIconic(hwnd) && GetForegroundWindow() == hwnd) return ShowWindow(hwnd, 6);
        if (IsIconic(hwnd)) ShowWindow(hwnd, 9);
        if (SetForegroundWindow(hwnd)) return true;
        uint current = GetCurrentThreadId(), foreground = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        bool attached = foreground != 0 && foreground != current && AttachThreadInput(current, foreground, true);
        try { BringWindowToTop(hwnd); return SetForegroundWindow(hwnd); }
        finally { if (attached) AttachThreadInput(current, foreground, false); }
    }
    public static Rect MonitorWork(Window window, bool cursor = false)
    {
        NativePoint p;
        if (cursor) GetCursorPos(out p); else p = new NativePoint { X = (int)(SystemParameters.PrimaryScreenWidth / 2), Y = 0 };
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromPoint(p, cursor ? 2u : 1u), ref info)) return SystemParameters.WorkArea;
        var source = PresentationSource.FromVisual(window);
        var transform = source?.CompositionTarget?.TransformFromDevice ?? System.Windows.Media.Matrix.Identity;
        var a = transform.Transform(new Point(info.Work.Left, info.Work.Top)); var b = transform.Transform(new Point(info.Work.Right, info.Work.Bottom));
        return new Rect(a, b);
    }
    public static bool Acrylic(Window window, bool enabled, bool frosted = false)
    {
        // Optional OS effect. The drawn glass fallback remains usable if the OS declines it.
        IntPtr pointer = Marshal.AllocHGlobal(Marshal.SizeOf<AccentPolicy>());
        try
        {
            Marshal.StructureToPtr(new AccentPolicy { State = enabled ? (frosted ? 4 : 3) : 0, Flags = 2, Color = frosted ? unchecked((int)0x881A1714) : unchecked((int)0x20100E0C) }, pointer, false);
            var data = new CompositionData { Attribute = 19, Data = pointer, Size = Marshal.SizeOf<AccentPolicy>() };
            return SetWindowCompositionAttribute(new WindowInteropHelper(window).Handle, ref data);
        }
        catch (EntryPointNotFoundException) { return false; }
        finally { Marshal.FreeHGlobal(pointer); }
    }
}
