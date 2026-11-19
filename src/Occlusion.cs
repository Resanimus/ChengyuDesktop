using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace ChengyuDock;
internal static class Occlusion
{
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out Native.NativeRect rect);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindow callback, IntPtr parameter);
    delegate bool EnumWindow(IntPtr hwnd, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder name, int size);
    [DllImport("user32.dll")] static extern bool GetLayeredWindowAttributes(IntPtr hwnd, out uint key, out byte alpha, out uint flags);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] static extern int GetFrameBounds(IntPtr hwnd, int attribute, out Native.NativeRect rect, int size);
    [DllImport("gdi32.dll")] static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] static extern int CombineRgn(IntPtr result, IntPtr a, IntPtr b, int mode);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr value);

    internal static double WallpaperCoverage()
    {
        var info = new Native.MonitorInfo { Size = Marshal.SizeOf<Native.MonitorInfo>() };
        if (!Native.GetMonitorInfo(Native.MonitorFromPoint(new Native.NativePoint(), 1), ref info)) return 0;

        var windows = new List<Native.NativeRect>();
        var className = new StringBuilder(64);
        EnumWindows((hwnd, parameter) =>
        {
            if (!Native.IsWindowVisible(hwnd) || Native.IsIconic(hwnd)) return true;
            long style = DesktopLayer.GetStyle(hwnd, -20).ToInt64();
            if ((style & 0x08000020) != 0 ||
                Native.DwmGetWindowAttribute(hwnd, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            if ((style & 0x00080000) != 0 &&
                (!GetLayeredWindowAttributes(hwnd, out _, out byte alpha, out uint flags) || flags != 2 || alpha < 255)) return true;
            className.Clear();
            GetClassName(hwnd, className, className.Capacity);
            if (className.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return true;
            if (GetFrameBounds(hwnd, 9, out var rect, Marshal.SizeOf<Native.NativeRect>()) == 0 || GetWindowRect(hwnd, out rect))
                windows.Add(rect);
            return true;
        }, IntPtr.Zero);
        return Coverage(info.Work, windows);
    }

    // Sample the union, so overlapping windows count once and snapped windows count together.
    internal static double Coverage(Native.NativeRect work, IReadOnlyList<Native.NativeRect> windows)
    {
        const int columns = 32, rows = 18;
        double width = work.Right - work.Left, height = work.Bottom - work.Top;
        if (width <= 0 || height <= 0) return 0;
        int covered = 0;
        for (int row = 0; row < rows; row++)
        for (int column = 0; column < columns; column++)
        {
            double x = work.Left + (column + .5) * width / columns;
            double y = work.Top + (row + .5) * height / rows;
            foreach (var rect in windows)
            {
                if (x < rect.Left || x >= rect.Right || y < rect.Top || y >= rect.Bottom) continue;
                covered++;
                break;
            }
        }
        return covered / (double)(columns * rows);
    }
    public static bool Covered(FrameworkElement element)
    {
        var owner = Window.GetWindow(element); if (owner == null || !owner.IsVisible || owner.WindowState == WindowState.Minimized) return true;
        var point = element.PointToScreen(new Point()); var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(element);
        var region = CreateRectRgn((int)point.X, (int)point.Y, (int)Math.Ceiling(point.X + element.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(point.Y + element.ActualHeight * dpi.DpiScaleY));
        if (region == IntPtr.Zero) return false;
        try
        {
            var hwnd = new WindowInteropHelper(owner).Handle;
            for (var h = GetWindow(hwnd, 3); h != IntPtr.Zero; h = GetWindow(h, 3))
            {
                if (!Native.IsWindowVisible(h) || Native.IsIconic(h) || (DesktopLayer.GetStyle(h, -20).ToInt64() & 0x80020) != 0) continue;
                if (Native.DwmGetWindowAttribute(h, 14, out int cloaked, 4) == 0 && cloaked != 0) continue;
                if (!GetWindowRect(h, out var rect)) continue; var mask = CreateRectRgn(rect.Left, rect.Top, rect.Right, rect.Bottom);
                try { if (CombineRgn(region, region, mask, 4) == 1) return true; } finally { DeleteObject(mask); }
            }
            return false;
        }
        finally { DeleteObject(region); }
    }
}
