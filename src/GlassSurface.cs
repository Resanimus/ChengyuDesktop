using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ChengyuDock;
public sealed record GlassInput(byte[] Pixels, int Width, int Height, int Left, int Top, ulong Revision);

// D3D11 produces the premultiplied pixels. WPF composites them without acrylic.
public sealed class GlassSurface : FrameworkElement
{
    public string Material { get; set; } = "liquid";
    public double Radius { get; set; } = 22;
    public int LiquidKind { get; set; } = 1;
    public int FrostedKind { get; set; } = 1;
    public Func<GlassInput?>? InputProvider { get; set; }
    internal ulong Handle;
    internal bool Registered;
    WriteableBitmap? _image;
    byte[]? _buffer;
    GlassInput? _wallpaper; ulong _wallpaperStamp;
    double _pixelScale = 1;
    double _coverAge, _coverCheck;
    bool _covered;
    internal bool TestVisibility;
    readonly double[] _press = new double[2], _pointerX = new double[2], _pointerY = new double[2];
    readonly double[] _lightX = { -.62, 0 }, _lightY = { -.85, 0 };
    public int LastHResult { get; private set; } = 1;
    public long RenderedFrames { get; private set; }
    public GlassSurface()
    {
        IsHitTestVisible = false;
        Loaded += (_, _) => { Registered = true; GlassPump.Register(this); };
        Unloaded += (_, _) => { Registered = false; GlassPump.Unregister(this); _image = null; _buffer = null; _wallpaper = null; };
    }
    public void Light(double x, double y, double strength) { }
    public void Refresh() => InvalidateVisual();
    static void Spring(double[] state, double target, double omega, double dt, bool snap)
    {
        if (snap) { state[0] = target; state[1] = 0; return; }
        double delta = state[0] - target, b = state[1] + omega * delta, decay = Math.Exp(-omega * dt);
        state[0] = (delta + b * dt) * decay + target; state[1] = (state[1] - omega * b * dt) * decay;
        if (Math.Abs(state[0] - target) < .0001 && Math.Abs(state[1]) < .002) { state[0] = target; state[1] = 0; }
    }
    internal GlassRequest? Request(double dt, Native.NativePoint cursor)
    {
        if (Material is "none" or "solid") { if (Handle != 0) GlassPump.Release(this); _image = null; _buffer = null; return null; }
        if (!Registered || !IsVisible || ActualWidth < 4 || ActualHeight < 4) return null;
        _coverCheck -= dt;
        if (_coverCheck <= 0) { _coverCheck = .4; _covered = !TestVisibility && Occlusion.Covered(this); }
        if (_covered) { _coverAge += dt; if (_coverAge > 2 && Handle != 0) { GlassPump.Release(this); _buffer = null; _image = null; } return null; }
        _coverAge = 0; if (Handle == 0) GlassPump.Allocate(this);
        var dpi = VisualTreeHelper.GetDpi(this); double scale = dpi.DpiScaleX, pad = 34 * scale;
        var point = PointToScreen(new Point()); int w = (int)Math.Ceiling(ActualWidth * scale + pad * 2), h = (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY + pad * 2);
        w = (w + 31) / 32 * 32; h = (h + 31) / 32 * 32;
        if (w > 8192 || h > 8192) return null;
        bool inside = cursor.X >= point.X && cursor.X <= point.X + ActualWidth * scale && cursor.Y >= point.Y && cursor.Y <= point.Y + ActualHeight * scale;
        double x = inside ? Math.Clamp((cursor.X - point.X) / (ActualWidth * scale) * 2 - 1, -1, 1) : -.62;
        double y = inside ? Math.Clamp((cursor.Y - point.Y) / (ActualHeight * scale) * 2 - 1, -1, 1) : -.85;
        bool snap = App.Current.Prefs.ReducedMotion || !SystemParameters.ClientAreaAnimation;
        Spring(_press, inside && Mouse.LeftButton == MouseButtonState.Pressed ? 1 : 0, 24, dt, snap);
        Spring(_pointerX, x, 24, dt, snap); Spring(_pointerY, y, 24, dt, snap);
        Spring(_lightX, x, 5.5, dt, snap); Spring(_lightY, y, 5.5, dt, snap);
        bool frost = Material == "frosted"; int kind = frost ? FrostedKind : LiquidKind;
        float[] c = new float[36];
        c[0] = (float)(point.X - pad); c[1] = (float)(point.Y - pad); c[2] = w; c[3] = h;
        c[8] = (float)point.X; c[9] = (float)point.Y; c[10] = (float)(ActualWidth * scale); c[11] = (float)(ActualHeight * scale);
        c[12] = frost ? -1 : .69f; c[13] = .18f; c[14] = frost ? (float)(-Radius * scale) : 1.1f; c[15] = (float)scale;
        if (!frost) { c[20] = (float)_press[0]; c[21] = (float)_pointerX[0]; c[22] = (float)_pointerY[0]; c[23] = (float)_press[0]; c[26] = (float)_lightX[0]; c[27] = (float)_lightY[0]; c[28] = c[29] = 1; }
        c[33] = kind; c[34] = kind >= 3 ? .55f : kind == 2 ? .92f : 1; c[35] = (float)(Radius * scale);
        if (_buffer == null || _buffer.Length != w * h * 4) _buffer = new byte[w * h * 4];
        var input = InputProvider?.Invoke();
        if (input == null) {
            int bx = (int)Math.Floor(point.X - pad - 48), by = (int)Math.Floor(point.Y - pad - 48), bw = w + 96, bh = h + 96; ulong revision = WallpaperBackdrop.Revision;
            if (_wallpaper == null || _wallpaperStamp != revision || _wallpaper.Left != bx || _wallpaper.Top != by || _wallpaper.Width != bw || _wallpaper.Height != bh) { _wallpaper = WallpaperBackdrop.Sample(bx, by, bw, bh, (ulong)Stopwatch.GetTimestamp()); _wallpaperStamp = revision; }
            input = _wallpaper;
        }
        return new GlassRequest(this, Handle, c, w, h, frost ? 2 : 1, scale, input, _buffer);
    }
    internal void Present(GlassResult result)
    {
        LastHResult = result.Result;
        if (!Registered || result.Result != 0) return;
        if (_image == null || _image.PixelWidth != result.Request.Width || _image.PixelHeight != result.Request.Height)
            _image = new WriteableBitmap(result.Request.Width, result.Request.Height, 96, 96, PixelFormats.Pbgra32, null);
        _image.WritePixels(new Int32Rect(0, 0, _image.PixelWidth, _image.PixelHeight), result.Request.Pixels, _image.PixelWidth * 4, 0);
        _pixelScale = result.Request.Scale; RenderedFrames++; InvalidateVisual();
    }
    protected override void OnRender(DrawingContext d)
    {
        if (Material == "none") return;
        if (Material == "solid") { d.DrawRoundedRectangle(Brushes.Black, null, new Rect(0, 0, ActualWidth, ActualHeight), Radius, Radius); return; }
        if (_image != null) { d.DrawImage(_image, new Rect(-34, -34, _image.PixelWidth / _pixelScale, _image.PixelHeight / _pixelScale)); return; }
        d.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(Material == "frosted" ? (byte)210 : (byte)70, 12, 14, 18)), new Pen(new SolidColorBrush(Color.FromArgb(48, 255, 255, 255)), .7), new Rect(0, 0, ActualWidth, ActualHeight), Radius, Radius);
    }
}
internal sealed record GlassRequest(GlassSurface Surface, ulong Id, float[] Constants, int Width, int Height, int Shader, double Scale, GlassInput? Input, byte[] Pixels);
internal sealed record GlassResult(GlassRequest Request, int Result);

internal static class GlassPump
{
    [DllImport("ChengyuGlass.dll", CallingConvention = CallingConvention.Cdecl)] static extern ulong GlassSurfaceCreate();
    [DllImport("ChengyuGlass.dll", CallingConvention = CallingConvention.Cdecl)] static extern void GlassSurfaceDestroy(ulong id);
    [DllImport("ChengyuGlass.dll", CallingConvention = CallingConvention.Cdecl)] static extern int GlassSurfaceRender(ulong id, [In] float[] c, int w, int h, int shader, [Out] byte[] pixels, int capacity, [In] byte[]? source, int sw, int sh, int sx, int sy, ulong revision);
    [DllImport("ChengyuGlass.dll", CallingConvention = CallingConvention.Cdecl)] static extern int GlassCapturePixels(int x, int y, int w, int h, [Out] byte[] pixels, int capacity);
    [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    static readonly HashSet<GlassSurface> Surfaces = new();
    static readonly HashSet<IntPtr> Capturable = new();
    static DispatcherTimer? _timer;
    static bool _busy, _paused;
    
    static readonly Stopwatch Clock = Stopwatch.StartNew(); static double _last;
    public static bool ExclusionSupported { get; private set; } = true;
    public static long Frames { get; private set; }
    public static void Register(GlassSurface surface)
    {
        Surfaces.Add(surface);
        if (_timer == null) { _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(33) }; _timer.Tick += Tick; }
        _timer.Start();
    }
    internal static void Allocate(GlassSurface surface) { if (surface.Handle == 0) surface.Handle = GlassSurfaceCreate(); }
    internal static void Release(GlassSurface surface) { if (surface.Handle != 0) GlassSurfaceDestroy(surface.Handle); surface.Handle = 0; }
    public static void Unregister(GlassSurface surface) { Surfaces.Remove(surface); if (surface.Handle != 0) GlassSurfaceDestroy(surface.Handle); surface.Handle = 0; if (Surfaces.Count == 0) _timer?.Stop(); }
    public static void AllowCapture(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        ExclusionSupported &= SetWindowDisplayAffinity(hwnd, 0u); Capturable.Add(hwnd);
        window.Closed += (_, _) => Capturable.Remove(hwnd);
    }
    static async void Tick(object? sender, EventArgs args)
    {
        if (_busy || _paused) return; _busy = true;
        try
        {
            double now = Clock.Elapsed.TotalSeconds, dt = _last == 0 ? 1.0 / 30 : Math.Clamp(now - _last, .0005, .05); _last = now;
            Native.GetCursorPos(out var cursor);
            var requests = Surfaces.ToArray().Select(s => s.Request(dt, cursor)).Where(r => r != null).Cast<GlassRequest>().ToArray();
            if (requests.Length == 0) return;
            var results = await Task.Run(() => requests.Select(request =>
            {
                var input = request.Input;
                int code = GlassSurfaceRender(request.Id, request.Constants, request.Width, request.Height, request.Shader, request.Pixels, request.Pixels.Length, input?.Pixels, input?.Width ?? 0, input?.Height ?? 0, input?.Left ?? 0, input?.Top ?? 0, input?.Revision ?? 0);
                return new GlassResult(request, code);
            }).ToArray());
            foreach (var result in results) { result.Request.Surface.Present(result); if (result.Result == 0) Frames++; }
        }
        catch (Exception ex) { App.Current.ReportBackgroundError("材质渲染", ex); }
        finally { _busy = false; }
    }
    public static async Task<GlassInput?> CaptureVisibleScene(Window owner)
    {
        _paused = true; while (_busy) await Task.Delay(10);
        var handles = Capturable.Where(Native.IsWindow).ToArray();
        try
        {
            foreach (var hwnd in handles) SetWindowDisplayAffinity(hwnd, 0);
            await Task.Delay(100);
            var work = Native.MonitorWork(owner, true); var dpi = VisualTreeHelper.GetDpi(owner); double s = dpi.DpiScaleX;
            int x = (int)Math.Round(work.X * s), y = (int)Math.Round(work.Y * s), w = (int)Math.Round(work.Width * s), h = (int)Math.Round(work.Height * s);
            byte[] pixels = new byte[w * h * 4];
            int code = await Task.Run(() => GlassCapturePixels(x, y, w, h, pixels, pixels.Length));
            return code == 0 ? new GlassInput(pixels, w, h, x, y, (ulong)Stopwatch.GetTimestamp()) : null;
        }
        finally { foreach (var hwnd in handles) SetWindowDisplayAffinity(hwnd, 0u); await Task.Delay(65); _paused = false; }
    }
    public static void ScreenshotMode() => App.Current.Notify("截图与录屏已默认包含桌面组件，无需切换模式");
    public static void Stop() { _timer?.Stop(); }
}
