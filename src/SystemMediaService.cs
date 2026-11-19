using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace ChengyuDock;
internal sealed record IslandMedia(string Title, string Artist, bool Playing, bool External, double Position = 0, double Duration = 0, ImageSource? Cover = null, Color? Accent = null)
{
    internal static readonly IslandMedia Empty = new("", "", false, false);
}
// GSMTC supplies real session data and commands. The output meter is read-only;
// it drives the bars for the selected media without opening a recording stream.
public sealed class SystemMediaService : IDisposable
{
    GlobalSystemMediaTransportControlsSessionManager? _manager;
    GlobalSystemMediaTransportControlsSession? _session;
    MMDeviceEnumerator? _enumerator; MMDevice? _device;
    readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly DispatcherTimer _meter = new() { Interval = TimeSpan.FromMilliseconds(80) };
    readonly AudioVisibilityGate _gate = new();
    internal readonly double[] Levels = new double[5];
    internal IslandMedia Current { get; private set; } = IslandMedia.Empty;
    internal bool Audible { get; private set; }
    internal bool SessionManagerAvailable => _manager != null;
    bool _busy, _disposed, _enabled;
    long _lastMeter; string _coverKey = "";
    ImageSource? _cover; Color? _accent;
    public event Action? Changed;
    public SystemMediaService()
    {
        App.Current.Changed += Sync;
        _poll.Tick += async (_, _) => await Refresh();
        _meter.Tick += (_, _) => ReadMeter(); Sync();
    }
    void Sync()
    {
        bool enabled = App.Current.Prefs.Widgets["island"].Enabled;
        if (_enabled == enabled) return; _enabled = enabled;
        if (enabled) { _poll.Start(); _meter.Start(); _ = Refresh(); }
        else { _poll.Stop(); _meter.Stop(); _device?.Dispose(); _device = null; _enumerator?.Dispose(); _enumerator = null; _session = null; _manager = null; Current = IslandMedia.Empty; Audible = false; Array.Clear(Levels); }
    }
    async Task Refresh()
    {
        if (_busy || _disposed || !_enabled) return; _busy = true;
        try
        {
            _manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            var sessions = _manager.GetSessions();
            var selected = _manager.GetCurrentSession();
            var session = selected?.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing ? selected : sessions.FirstOrDefault(s => s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing) ?? selected;
            if (session == null) { bool changed = Current != IslandMedia.Empty; _session = null; Current = IslandMedia.Empty; if (changed) Changed?.Invoke(); return; }
            var properties = await session.TryGetMediaPropertiesAsync();
            if (_disposed || !_enabled) return;
            string key = session.SourceAppUserModelId + "|" + properties.Title + "|" + properties.AlbumTitle;
            if (key != _coverKey)
            {
                _coverKey = key; _cover = null; _accent = null;
                try
                {
                    if (properties.Thumbnail != null)
                    {
                        using var stream = await properties.Thumbnail.OpenReadAsync();
                        if (stream.Size > 0 && stream.Size <= 4 * 1024 * 1024)
                        {
                            using var reader = new DataReader(stream.GetInputStreamAt(0)); await reader.LoadAsync((uint)stream.Size);
                            var bytes = new byte[(int)stream.Size]; reader.ReadBytes(bytes);
                            using var input = new MemoryStream(bytes); var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.DecodePixelWidth = 64; image.StreamSource = input; image.EndInit(); image.Freeze(); _cover = image; _accent = Accent(image);
                        }
                    }
                }
                catch (Exception) { /* A missing cover must not prevent playback state. */ }
            }
            var timeline = session.GetTimelineProperties();
            bool playing = session.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            double duration = Math.Max(0, (timeline.EndTime - timeline.StartTime).TotalSeconds), position = Math.Max(0, (timeline.Position - timeline.StartTime).TotalSeconds);
            var next = new IslandMedia(string.IsNullOrWhiteSpace(properties.Title) ? "系统媒体" : properties.Title, properties.Artist ?? "", playing, true, position, duration, _cover, _accent);
            _session = session; if (next != Current) { Current = next; Changed?.Invoke(); }
        }
        catch (Exception) { if (_disposed || !_enabled) return; _manager = null; _session = null; if (Current != IslandMedia.Empty) { Current = IslandMedia.Empty; Changed?.Invoke(); } }
        finally { _busy = false; }
    }
    void ReadMeter()
    {
        if (_disposed || !_enabled) return;
        var now = System.Diagnostics.Stopwatch.GetTimestamp(); double dt = _lastMeter == 0 ? .08 : Math.Clamp(System.Diagnostics.Stopwatch.GetElapsedTime(_lastMeter).TotalSeconds, .001, .25); _lastMeter = now;
        double peak = 0;
        try { _enumerator ??= new MMDeviceEnumerator(); _device ??= _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia); peak = _device.AudioMeterInformation.MasterPeakValue; }
        catch (Exception) { _device?.Dispose(); _device = null; }
        bool old = Audible; _gate.Step(20 * Math.Log10(Math.Max(1e-6, peak)), dt, -60); Audible = _gate.Open;
        double level = Math.Clamp(Math.Sqrt(Math.Max(0, peak)) * 1.5, 0, 1), previous = Levels[0];
        for (int i = Levels.Length - 1; i > 0; i--) Levels[i] = Levels[i - 1]; Levels[0] += (level - Levels[0]) * (1 - Math.Exp(-dt / .10));
        _meter.Interval = TimeSpan.FromMilliseconds(Audible || Current.Playing ? 80 : 250);
        if (old != Audible || Math.Abs(previous - Levels[0]) > .002 || Audible) Changed?.Invoke();
    }
    internal async void Command(int command)
    {
        var session = _session; if (session == null) return;
        try { bool success = command == 0 ? await session.TryTogglePlayPauseAsync() : command < 0 ? await session.TrySkipPreviousAsync() : await session.TrySkipNextAsync(); if (!success) App.Current.Notify("当前播放器不支持此媒体操作"); await Refresh(); }
        catch (Exception) { App.Current.Notify("当前媒体会话已结束"); }
    }
    static Color Accent(BitmapSource source)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0); int stride = converted.PixelWidth * 4; var bytes = new byte[stride * converted.PixelHeight]; converted.CopyPixels(bytes, stride, 0);
        double r = 0, g = 0, b = 0, total = 0;
        for (int i = 0; i < bytes.Length; i += 4) { if (bytes[i + 3] < 128) continue; double max = Math.Max(bytes[i], Math.Max(bytes[i + 1], bytes[i + 2])), min = Math.Min(bytes[i], Math.Min(bytes[i + 1], bytes[i + 2])), weight = (max - min) / 255 + .05; b += bytes[i] * weight; g += bytes[i + 1] * weight; r += bytes[i + 2] * weight; total += weight; }
        if (total == 0) return Color.FromRgb(48, 209, 88); r /= total; g /= total; b /= total; double high = Math.Max(r, Math.Max(g, b)); if (high - Math.Min(r, Math.Min(g, b)) < 24) return Colors.White;
        return Color.FromRgb((byte)(r * 235 / high), (byte)(g * 235 / high), (byte)(b * 235 / high));
    }
    public void Dispose() { _disposed = true; App.Current.Changed -= Sync; _poll.Stop(); _meter.Stop(); _device?.Dispose(); _enumerator?.Dispose(); _session = null; _manager = null; }
}
