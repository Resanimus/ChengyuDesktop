using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace ChengyuDock;
internal static class DesktopLayer
{
    [StructLayout(LayoutKind.Sequential)] struct Position { public IntPtr Hwnd, After; public int X, Y, W, H; public uint Flags; }
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, IntPtr data);
    delegate bool EnumProc(IntPtr window, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder value, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? title);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint command);
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] internal static extern IntPtr GetStyle(IntPtr h, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] internal static extern IntPtr SetStyle(IntPtr h, int index, IntPtr value);
    // Above Explorer's desktop, below normal app windows; HWND_BOTTOM alone can hide behind Explorer.
    static IntPtr BottomAnchor(IntPtr self)
    {
        IntPtr desktop = IntPtr.Zero;
        EnumWindows((h, _) => { if (FindWindowEx(h, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero) { desktop = h; return false; } return true; }, IntPtr.Zero);
        if (desktop == IntPtr.Zero) return new IntPtr(1);
        IntPtr previous = GetWindow(desktop, 3);
        if (previous == self) previous = GetWindow(self, 3);
        return previous;
    }
    static readonly Dictionary<IntPtr, bool> Layers = new();
    static IntPtr Anchor(IntPtr self)
    {
        if (!Layers.GetValueOrDefault(self)) return BottomAnchor(self);
        IntPtr highest = IntPtr.Zero;
        // Scan upward from Explorer; the last desktop peer encountered is the highest.
        IntPtr desktop = IntPtr.Zero;
        EnumWindows((h, _) => { if (FindWindowEx(h, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero) { desktop = h; return false; } return true; }, IntPtr.Zero);
        for (var h = GetWindow(desktop, 3); h != IntPtr.Zero; h = GetWindow(h, 3)) if (h != self && Layers.ContainsKey(h) && (GetStyle(h, -20).ToInt64() & 8) == 0) highest = h;
        if (highest == IntPtr.Zero) return BottomAnchor(self);
        var previous = GetWindow(highest, 3); return previous == self ? GetWindow(self, 3) : previous;
    }
    public static void Bottom(Window window)
    {
        var h = new WindowInteropHelper(window).Handle; if (h != IntPtr.Zero) SetWindowPos(h, Anchor(h), 0, 0, 0, 0, 0x13);
    }
    public static void Attach(Window window, Func<bool>? enabled = null, bool priority = false)
    {
        Native.MakePassive(window); var h = new WindowInteropHelper(window).Handle; Layers[h] = priority; window.Closed += (_, _) => Layers.Remove(h);
        HwndSource.FromHwnd(h).AddHook((IntPtr hwnd, int m, IntPtr w, IntPtr l, ref bool handled) =>
        {
            if (m == 0x46 && l != IntPtr.Zero && (enabled?.Invoke() ?? true))
            { var p = Marshal.PtrToStructure<Position>(l); p.After = Anchor(hwnd); p.Flags = (p.Flags & ~4u) | 0x10u; Marshal.StructureToPtr(p, l, false); }
            return IntPtr.Zero;
        });
        Bottom(window);
    }
    internal static bool IsBelow(IntPtr window, IntPtr reference)
    {
        for (var h = GetWindow(reference, 2); h != IntPtr.Zero; h = GetWindow(h, 2)) if (h == window) return true;
        return false;
    }
}
