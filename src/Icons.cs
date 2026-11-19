using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ChengyuDock;

internal static class Icons
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct ShellInfo { public IntPtr Icon; public int Index; public uint Attributes; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Display; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string Type; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SHGetFileInfo(string path, uint attrs, out ShellInfo info, uint size, uint flags);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr icon);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr bitmap);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)] static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory);
    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IShellItemImageFactory
    { [PreserveSig] int GetImage(Native.NativeSize size, uint flags, out IntPtr bitmap); }
    static readonly Dictionary<string, ImageSource> Cache = new(StringComparer.OrdinalIgnoreCase);
    public static ImageSource Get(string path, string name)
    {
        if (Cache.TryGetValue(path, out var cached)) return cached;
        ImageSource image;
        if (path is "@manage" or "@launchpad") image = Brand(path == "@launchpad");
        else
        {
            image = Letter(name, path);
            try
            {
                if (PackagedApps.TryGetId(path, out _) || File.Exists(path) || Directory.Exists(path))
                {
                    Guid iid = typeof(IShellItemImageFactory).GUID;
                    if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory) == 0)
                    {
                        try
                        {
                            if (factory.GetImage(new Native.NativeSize { Width = 96, Height = 96 }, 1 | 4, out var bitmap) == 0 && bitmap != IntPtr.Zero)
                            {
                                try { image = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions()); image.Freeze(); if (Cache.Count < 256) Cache[path] = image; return image; }
                                finally { DeleteObject(bitmap); }
                            }
                        }
                        finally { Marshal.ReleaseComObject(factory); }
                    }
                    SHGetFileInfo(path, 0, out var info, (uint)Marshal.SizeOf<ShellInfo>(), 0x100);
                    if (info.Icon != IntPtr.Zero)
                    { try { image = Imaging.CreateBitmapSourceFromHIcon(info.Icon, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(64, 64)); image.Freeze(); } finally { DestroyIcon(info.Icon); } }
                }
            }
            catch (Exception ex) when (ex is ArgumentException or ExternalException or IOException) { }
        }
        if (Cache.Count < 256) Cache[path] = image;
        return image;
    }
    static ImageSource Letter(string name, string path)
    {
        var palette = new[] { "#4F8EEC", "#5BAF9B", "#AA77D4", "#DB9461", "#CE6C80" };
        uint hash = 2166136261; foreach (char c in path) hash = (hash ^ c) * 16777619;
        var color = (Color)ColorConverter.ConvertFromString(palette[hash % palette.Length]);
        var drawing = new DrawingGroup(); using (var d = drawing.Open())
        {
            var fill = new LinearGradientBrush(color, Color.Multiply(color, 0.65f), 90);
            d.DrawRoundedRectangle(fill, new Pen(new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)), 1), new Rect(3, 3, 58, 58), 13, 13);
            string letter = string.IsNullOrWhiteSpace(name) ? "·" : name[..1].ToUpperInvariant();
            var text = new FormattedText(letter, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI Semibold"), 29, Brushes.White, 1);
            d.DrawText(text, new Point(32 - text.Width / 2, 32 - text.Height / 2));
        }
        drawing.Freeze(); return new DrawingImage(drawing);
    }
    static readonly Lazy<ImageSource> ProductMark = new(() =>
    {
        var image = new BitmapImage(new Uri("pack://application:,,,/ChengyuDock;component/BrandMark.png"));
        image.Freeze();
        return image;
    });

    public static ImageSource Brand(bool launchpad)
    {
        if (!launchpad) return ProductMark.Value;
        var group = new DrawingGroup();
        using (var drawing = group.Open())
        {
            drawing.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, 64, 64));
            for (int y = 0; y < 3; y++)
            for (int x = 0; x < 3; x++)
                drawing.DrawEllipse(Brushes.White, null, new Point(14 + x * 18, 14 + y * 18), 5.2, 5.2);
        }
        group.Freeze();
        var result = new DrawingImage(group);
        result.Freeze();
        return result;
    }
}
