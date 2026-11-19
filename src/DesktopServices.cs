using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace ChengyuDock;
public sealed class WeatherData
{
    public string City { get; set; } = "济南";
    public double? Temperature { get; set; }
    public double? High { get; set; }
    public double? Low { get; set; }
    public string Condition { get; set; } = "获取中";
    public string Icon { get; set; } = "☁";
    public string Updated { get; set; } = "";
    public string Error { get; set; } = "";
    public List<WeatherHour> Hours { get; set; } = new();
}
public sealed class WeatherHour { public string Time { get; set; } = ""; public double Temperature { get; set; } public string Icon { get; set; } = "☁"; }

public sealed class DesktopServices : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] struct MemoryInfo { public uint Length, Load; public ulong Total, Available, PageTotal, PageAvailable, VirtualTotal, VirtualAvailable, Extended; }
    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MemoryInfo info);
    [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    readonly HttpClient _http = new(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(12) };
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly IGpuTelemetry _gpu = new WindowsGpuTelemetry();
    bool _gpuPending;
    internal GpuReading[] GpuReadings { get; private set; } = Array.Empty<GpuReading>();
    internal GpuReading? SelectedGpu
    {
        get
        {
            string id = App.Current.Prefs.GpuAdapterId;
            if (!string.IsNullOrEmpty(id)) return GpuReadings.FirstOrDefault(g => g.Id == id);
            return GpuReadings.OrderByDescending(g => g.Usage ?? -1)
                .ThenByDescending(g => g.DedicatedBytes ?? 0).ThenByDescending(g => g.CapacityBytes).FirstOrDefault();
        }
    }

    internal async void RequestGpuSample()
    {
        if (_gpuPending || _disposed) return;
        _gpuPending = true;
        try
        {
            var readings = await Task.Run(_gpu.Read);
            if (_disposed) return;
            GpuReadings = readings;
            Signal();
        }
        catch (Exception ex)
        {
            if (!_disposed) App.Current.ReportBackgroundError("显卡采样", ex);
        }
        finally
        {
            _gpuPending = false;
            if (_disposed) _gpu.Dispose();
        }
    }

    MediaPlayer? _player;
    readonly Dictionary<string, string> _icons = new();
    long _idle, _kernel, _user, _sent, _received; bool _weatherBusy, _disposed;
    DateTime _networkTime = DateTime.UtcNow, _lastWeather = DateTime.MinValue;
    int _track; string? _opened; bool _playing; object _performance = new { cpu = (int?)null, memory = 0, memoryDetail = "", upload = "--", download = "--", drives = Array.Empty<object>() };
    public WeatherData Weather { get; private set; } = new();
    JsonElement _nativeModel;
    public JsonElement NativeModel => _nativeModel.ValueKind == JsonValueKind.Undefined ? _nativeModel = JsonSerializer.SerializeToElement(new { performance = _performance, weather = Weather, music = new { title = _opened == null ? "" : Path.GetFileNameWithoutExtension(_opened), playing = _playing, position = _opened == null ? 0 : (_player?.Position.TotalSeconds ?? 0), duration = _player?.NaturalDuration.HasTimeSpan == true ? _player.NaturalDuration.TimeSpan.TotalSeconds : 0 } }) : _nativeModel;
    void Signal() { _nativeModel = default; Changed?.Invoke(); }
    public event Action? Changed;
    public DesktopServices()
    {
        try { var file = Path.Combine(App.Current.Store.Root, "weather-cache.json"); if (File.Exists(file)) Weather = JsonSerializer.Deserialize<WeatherData>(File.ReadAllText(file)) ?? Weather; } catch { }
        _tick.Tick += (_, _) => { UpdatePerformance(); if (App.Current.Prefs.Widgets["performance"].Enabled || App.Current.Manager?.IsPerformancePage == true) RequestGpuSample(); if ((DateTime.UtcNow - _lastWeather).TotalMinutes >= 15) _ = RefreshWeather(); Signal(); };
        UpdatePerformance(); _tick.Start(); _ = RefreshWeather();
    }
    static (string Text, string Icon) Condition(int code) => code switch { 0 => ("晴", "☀"), 1 or 2 => ("多云", "⛅"), 3 => ("阴", "☁"), 45 or 48 => ("雾", "🌫"), >= 51 and <= 67 => ("雨", "🌧"), >= 71 and <= 77 => ("雪", "❄"), >= 95 => ("雷雨", "⛈"), >= 80 and <= 82 => ("阵雨", "🌦"), >= 85 and <= 86 => ("阵雪", "❄"), _ => ("多云", "☁") };
    public async Task RefreshWeather()
    {
        if (_weatherBusy || _disposed) return; _weatherBusy = true; _lastWeather = DateTime.UtcNow;
        try
        {
            var p = App.Current.Prefs; string lat = p.WeatherLatitude.ToString(CultureInfo.InvariantCulture), lon = p.WeatherLongitude.ToString(CultureInfo.InvariantCulture);
            string url = $"https://api.open-meteo.com/v1/forecast?latitude={lat}&longitude={lon}&current=temperature_2m,weather_code&hourly=temperature_2m,weather_code&daily=temperature_2m_max,temperature_2m_min&forecast_days=2&timezone=auto";
            using var json = JsonDocument.Parse(await _http.GetStringAsync(url)); var root = json.RootElement; var current = root.GetProperty("current"); var daily = root.GetProperty("daily"); var hourly = root.GetProperty("hourly");
            var condition = Condition(current.GetProperty("weather_code").GetInt32());
            var weather = new WeatherData { City = p.WeatherCity, Temperature = current.GetProperty("temperature_2m").GetDouble(), High = daily.GetProperty("temperature_2m_max")[0].GetDouble(), Low = daily.GetProperty("temperature_2m_min")[0].GetDouble(), Condition = condition.Text, Icon = condition.Icon, Updated = current.GetProperty("time").GetString()![11..] };
            string at = current.GetProperty("time").GetString()!;
            var times = hourly.GetProperty("time"); int begin = 0; while (begin + 1 < times.GetArrayLength() && string.CompareOrdinal(times[begin + 1].GetString(), at) <= 0) begin++;
            for (int i = begin; i < Math.Min(begin + 5, times.GetArrayLength()); i++) weather.Hours.Add(new WeatherHour { Time = times[i].GetString()![11..13] + "时", Temperature = hourly.GetProperty("temperature_2m")[i].GetDouble(), Icon = Condition(hourly.GetProperty("weather_code")[i].GetInt32()).Icon });
            Weather = weather; File.WriteAllText(Path.Combine(App.Current.Store.Root, "weather-cache.json"), JsonSerializer.Serialize(weather));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException) { Weather.City = App.Current.Prefs.WeatherCity; Weather.Error = Weather.Temperature == null ? "天气暂不可用" : "离线 · 上次更新 " + Weather.Updated; }
        finally { _weatherBusy = false; if (!_disposed) Signal(); }
    }
    public async Task<bool> SetWeatherCity(string city)
    {
        if (string.IsNullOrWhiteSpace(city) || city.Length > 60) return false;
        using var json = JsonDocument.Parse(await _http.GetStringAsync("https://geocoding-api.open-meteo.com/v1/search?name=" + Uri.EscapeDataString(city.Trim()) + "&count=1&language=zh&format=json"));
        if (!json.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0) return false;
        var first = results[0]; if (!App.Current.Apply(p => { p.WeatherCity = city.Trim(); p.WeatherLatitude = first.GetProperty("latitude").GetDouble(); p.WeatherLongitude = first.GetProperty("longitude").GetDouble(); })) return false;
        await RefreshWeather(); return true;
    }
    void UpdatePerformance()
    {
        int? cpu = null; if (GetSystemTimes(out long idle, out long kernel, out long user)) { long total = kernel - _kernel + user - _user; if (_kernel != 0 && total > 0) cpu = (int)Math.Clamp(Math.Round((total - (idle - _idle)) * 100.0 / total), 0, 100); _idle = idle; _kernel = kernel; _user = user; }
        var memory = new MemoryInfo { Length = (uint)Marshal.SizeOf<MemoryInfo>() }; GlobalMemoryStatusEx(ref memory);
        long sent = 0, received = 0; try { foreach (var n in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)) { var s = n.GetIPv4Statistics(); sent += s.BytesSent; received += s.BytesReceived; } } catch { }
        double seconds = Math.Max(.1, (DateTime.UtcNow - _networkTime).TotalSeconds); string up = _sent == 0 ? "--" : Rate(Math.Max(0, sent - _sent) / seconds), down = _received == 0 ? "--" : Rate(Math.Max(0, received - _received) / seconds); _sent = sent; _received = received; _networkTime = DateTime.UtcNow;
        var drives = new List<object>(); foreach (string letter in new[] { "C", "D" }) try { var d = new DriveInfo(letter + ":\\"); if (d.IsReady) drives.Add(new { name = letter + ":", percent = (int)Math.Round((1 - d.TotalFreeSpace / (double)d.TotalSize) * 100) }); } catch { }
        _performance = new { cpu, memory = memory.Load, memoryDetail = $"{(memory.Total - memory.Available) / 1073741824.0:F1} / {memory.Total / 1073741824.0:F1} GB", upload = up, download = down, drives };
    }
    static string Rate(double rate) => rate >= 1048576 ? $"{rate / 1048576:F1} MB/s" : $"{rate / 1024:F1} KB/s";
    public object Model()
    {
        var prefs = App.Current.Prefs; var shelf = prefs.ShelfBoxes.SelectMany(b => b.Pins).Select(pin => new { pin.Id, pin.Name, icon = Icon(pin) }).ToArray();
        return new { performance = _performance, weather = Weather, todos = prefs.Todos, shelf, reducedMotion = prefs.ReducedMotion, music = new { title = _opened == null ? "" : Path.GetFileNameWithoutExtension(_opened), playing = _playing, position = _opened == null ? 0 : (_player?.Position.TotalSeconds ?? 0), duration = _player?.NaturalDuration.HasTimeSpan == true ? _player.NaturalDuration.TimeSpan.TotalSeconds : 0 } };
    }
    string Icon(Pin pin)
    {
        if (_icons.TryGetValue(pin.Path, out string? cached)) return cached;
        var visual = new DrawingVisual(); using (var d = visual.RenderOpen()) d.DrawImage(Icons.Get(pin.Path, pin.Name), new System.Windows.Rect(0, 0, 32, 32));
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(32, 32, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap)); using var ms = new MemoryStream(); encoder.Save(ms);
        return _icons[pin.Path] = "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
    }
    void EnsurePlayer()
    {
        if (_player != null) return;
        _player = new MediaPlayer();
        _player.Volume = .65; _player.MediaEnded += (_, _) => Next(1);
        _player.MediaFailed += (_, _) => { _playing = false; _opened = null; Signal(); };
        _player.MediaOpened += (_, _) => Signal();
    }
    public void OpenMusic()
    {
        var dialog = new OpenFileDialog { Title = "选择本地音乐", Multiselect = true, Filter = "音频文件|*.mp3;*.wav;*.m4a;*.wma;*.aac|所有文件|*.*" };
        if (dialog.ShowDialog(App.Current.Manager) != true) return;
        if (App.Current.Apply(p => p.MusicFiles = dialog.FileNames.Take(200).ToList())) { _track = 0; PlayTrack(); }
    }
    void PlayTrack()
    {
        EnsurePlayer();
        var files = App.Current.Prefs.MusicFiles; if (files.Count == 0) { OpenMusic(); return; }
        _track = (_track + files.Count) % files.Count; string path = files[_track]; if (!File.Exists(path)) { App.Current.Notify("音乐文件不存在，请重新选择"); return; }
        _opened = path; _player!.Open(new Uri(path)); _player.Play(); _playing = true; Signal();
    }
    public void ToggleMusic() { if (_opened == null) { PlayTrack(); return; } if (_playing) _player!.Pause(); else _player!.Play(); _playing = !_playing; Signal(); }
    public void Next(int direction) { if (App.Current.Prefs.MusicFiles.Count == 0) { OpenMusic(); return; } _track += direction; PlayTrack(); }
    public void Dispose() { _disposed = true; _tick.Stop(); if (!_gpuPending) _gpu.Dispose(); _player?.Close(); _http.Dispose(); }
}
