using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using NAudio.Wave;
using NAudio.CoreAudioApi;

namespace ChengyuDock;
internal sealed class SpectrumDsp
{
    public const int Size = 8192;
    readonly float[] _ring = new float[Size];
    readonly double[] _real = new double[Size], _imag = new double[Size], _hann = new double[Size];
    readonly double[] _bins = new double[Size / 2];
    readonly object _gate = new();
    int _position, _stride; long _last; public long Transforms { get; private set; }
    public int SampleRate { get; private set; } = 48000;
    public SpectrumDsp() { for (int i = 0; i < Size; i++) _hann[i] = .5 * (1 - Math.Cos(2 * Math.PI * i / (Size - 1))); }
    public void Push(float sample, int rate)
    {
        _ring[_position] = float.IsFinite(sample) ? Math.Clamp(sample, -1, 1) : 0; _position = (_position + 1) % Size; SampleRate = rate;
        if (++_stride < 2048) return; _stride = 0;
        for (int i = 0; i < Size; i++) { _real[i] = _ring[(_position + i) % Size] * _hann[i]; _imag[i] = 0; }
        for (int i = 1, j = 0; i < Size; i++) { int bit = Size >> 1; for (; (j & bit) != 0; bit >>= 1) j ^= bit; j ^= bit; if (i < j) { (_real[i], _real[j]) = (_real[j], _real[i]); } }
        for (int length = 2; length <= Size; length <<= 1)
        {
            double angle = -2 * Math.PI / length, cosine = Math.Cos(angle), sine = Math.Sin(angle);
            for (int i = 0; i < Size; i += length)
            {
                double wr = 1, wi = 0;
                for (int j = 0; j < length / 2; j++) { int a = i + j, b = a + length / 2; double tr = wr * _real[b] - wi * _imag[b], ti = wr * _imag[b] + wi * _real[b]; _real[b] = _real[a] - tr; _imag[b] = _imag[a] - ti; _real[a] += tr; _imag[a] += ti; double next = wr * cosine - wi * sine; wi = wr * sine + wi * cosine; wr = next; }
            }
        }
        lock (_gate) { for (int i = 0; i < _bins.Length; i++) _bins[i] = 4 * Math.Sqrt(_real[i] * _real[i] + _imag[i] * _imag[i]) / Size; _last = Stopwatch.GetTimestamp(); Transforms++; }
    }
    public void Read(double[] output, bool expire = true)
    {
        lock (_gate)
        {
            if (_last == 0 || expire && Stopwatch.GetElapsedTime(_last).TotalSeconds > .3) { Array.Clear(output); return; }
            double max = Math.Min(10000, SampleRate / 2.0);
            for (int i = 0; i < output.Length; i++) { double ratio = i / (double)output.Length; int from = Math.Clamp((int)(50 * Math.Pow(max / 50, ratio) * Size / SampleRate), 1, _bins.Length - 1), to = Math.Clamp((int)Math.Ceiling(50 * Math.Pow(max / 50, (i + 1.0) / output.Length) * Size / SampleRate), from + 1, _bins.Length); double peak = 0; for (int b = from; b < to; b++) peak = Math.Max(peak, _bins[b]); double db = 20 * Math.Log10(Math.Max(1e-10, peak)); output[i] = Math.Clamp((db + 70) / 70 * (1 + Math.Pow(ratio, 1.3) * .4), 0, 1); }
        }
    }
    internal double PeakFrequency() { lock (_gate) { int best = 1; for (int i = 2; i < _bins.Length; i++) if (_bins[i] > _bins[best]) best = i; return best * SampleRate / (double)Size; } }
}
public sealed class SpectrumService : IDisposable
{
    double _rmsDb = -120; long _levelAt;
    public double LevelDb => !Active || System.Diagnostics.Stopwatch.GetElapsedTime(System.Threading.Interlocked.Read(ref _levelAt)).TotalSeconds > .35 ? -120 : System.Threading.Volatile.Read(ref _rmsDb);
    WasapiCapture? _capture; readonly SpectrumDsp _dsp = new(); int _generation;
    string? _source;
    public bool Active => _capture != null;
    public string Status { get; private set; } = "未开启";
    public long Packets { get; private set; }
    public event Action? Changed;
    internal void Start(string source)
    {
        if (Active && _source == source) return;
        Stop(); int generation = ++_generation;
        try
        {
            bool microphone = source == "microphone";
            var capture = microphone ? new WasapiCapture() : new WasapiLoopbackCapture();
            _capture = capture;
            _source = source;
            capture.DataAvailable += (_, e) =>
            {
                if (generation != _generation) return; var format = capture.WaveFormat; int channels = format.Channels, bytes = format.BitsPerSample / 8, frame = format.BlockAlign;
                bool floating = format.Encoding == WaveFormatEncoding.IeeeFloat || format is WaveFormatExtensible extensible && extensible.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71");
                double sumSquares = 0; int samples = 0;
                for (int offset = 0; offset + frame <= e.BytesRecorded; offset += frame)
                {
                    double sample = 0;
                    for (int channel = 0; channel < channels; channel++) { int at = offset + channel * bytes; double value = floating && bytes == 4 ? BitConverter.ToSingle(e.Buffer, at) : floating && bytes == 8 ? BitConverter.ToDouble(e.Buffer, at) : bytes == 2 ? BitConverter.ToInt16(e.Buffer, at) / 32768.0 : bytes == 3 ? ((e.Buffer[at] | e.Buffer[at + 1] << 8 | e.Buffer[at + 2] << 16) << 8 >> 8) / 8388608.0 : bytes == 4 ? BitConverter.ToInt32(e.Buffer, at) / 2147483648.0 : (e.Buffer[at] - 128) / 128.0; if (!double.IsFinite(value)) value = 0; sample += value; sumSquares += value * value; samples++; }
                    _dsp.Push((float)(sample / channels), format.SampleRate);
                }
                System.Threading.Volatile.Write(ref _rmsDb, 20 * Math.Log10(Math.Max(1e-6, Math.Sqrt(sumSquares / Math.Max(1, samples))))); System.Threading.Interlocked.Exchange(ref _levelAt, Stopwatch.GetTimestamp());
                Packets++;
            };
            capture.RecordingStopped += (_, e) => { if (generation != _generation || e.Exception == null) return; Application.Current.Dispatcher.BeginInvoke(() => { if (generation != _generation) return; Stop(); Status = "声音设备不可用"; App.Current.Notify("频谱采集已停止：" + e.Exception.Message); Changed?.Invoke(); }); };
            capture.StartRecording(); Status = microphone ? "麦克风" : "系统声音"; Changed?.Invoke();
        }
        catch (Exception ex) { Stop(); Status = "无法开启"; App.Current.Notify("无法开启实时频谱：" + ex.Message); Changed?.Invoke(); }
    }
    public void Stop() { _generation++; System.Threading.Volatile.Write(ref _rmsDb, -120); var capture = _capture; _capture = null; if (capture != null) { try { capture.StopRecording(); } finally { capture.Dispose(); } } Status = "未开启"; Changed?.Invoke(); }
    public void Read(double[] output) { if (Active) _dsp.Read(output); else Array.Clear(output); }
    public void Dispose() => Stop();
}
internal sealed class SpectrumVisual : FrameworkElement
{
    internal double[]? VerificationAmplitudes;
    readonly AudioVisibilityGate _visibility = new();
    readonly DispatcherTimer _timer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(1000.0 / 60) };
    double[] _raw = Array.Empty<double>(), _smooth = Array.Empty<double>(), _peak = Array.Empty<double>(), _velocity = Array.Empty<double>();
    double _last, _coverClock; bool _covered;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    static readonly Brush[] Rainbow = ColorsForBars();
    static Brush[] ColorsForBars() { var brushes = new Brush[512]; for (int i = 0; i < brushes.Length; i++) { double h = (240 + 160 * i / 511.0) % 360 / 60, c = .95, x = c * (1 - Math.Abs(h % 2 - 1)); (double r, double g, double b) = h switch { < 1 => (c, x, 0.0), < 2 => (x, c, 0.0), < 3 => (0.0, c, x), < 4 => (0.0, x, c), < 5 => (x, 0.0, c), _ => (c, 0.0, x) }; double m = .18; var brush = new SolidColorBrush(Color.FromRgb((byte)(Math.Min(1, r * .8 + m) * 255), (byte)(Math.Min(1, g * .8 + m) * 255), (byte)(Math.Min(1, b * .8 + m) * 255))); brush.Freeze(); brushes[i] = brush; } return brushes; }
    public SpectrumVisual()
    {
        IsHitTestVisible = false; Loaded += (_, _) => { App.Current.Spectrum.Changed += Changed; App.Current.Changed += Changed; Changed(); }; Unloaded += (_, _) => { _timer.Stop(); App.Current.Spectrum.Changed -= Changed; App.Current.Changed -= Changed; }; _timer.Tick += (_, _) => Step();
        SizeChanged += (_, _) => { Resize(); InvalidateVisual(); };
    }
    void Changed() { if (App.Current.Spectrum.Active) { _last = _clock.Elapsed.TotalSeconds; _timer.Start(); } else if (_smooth.Length > 0) _timer.Start(); InvalidateVisual(); }
    void Resize() { var p = App.Current.Prefs.Spectrum; int count = Math.Clamp((int)Math.Floor((Math.Max(16, ActualWidth) - 16 + p.BarGap) / (p.BarWidth + p.BarGap)), 1, 512); if (_raw.Length == count) return; _raw = new double[count]; _smooth = new double[count]; _peak = new double[count]; _velocity = new double[count]; }
    void Step()
    {
        Resize(); double now = _clock.Elapsed.TotalSeconds, dt = Math.Clamp(now - _last, .001, .1); _last = now;
        if ((_coverClock -= dt) <= 0) { _coverClock = .4; _covered = Occlusion.Covered(this); }
        _timer.Interval = TimeSpan.FromMilliseconds(_covered ? 200 : 1000.0 / 60); if (_covered) return;
        App.Current.Spectrum.Read(_raw); bool moving = false;
        for (int i = 0; i < _raw.Length; i++) { if (_raw[i] >= _peak[i]) { _peak[i] = _raw[i]; _velocity[i] = 0; } else { _velocity[i] += .028 * dt * 60; _peak[i] *= Math.Max(0, 1 - 2 * _velocity[i] * _velocity[i]); if (_peak[i] < .001) _peak[i] = _velocity[i] = 0; } _smooth[i] = Math.Pow(.77, dt * 60) * _smooth[i] + (1 - Math.Pow(.77, dt * 60)) * _peak[i]; moving |= _smooth[i] > .001; }
        _visibility.Step(App.Current.Spectrum.LevelDb, dt, App.Current.Prefs.Spectrum.SilenceThresholdDb);
        InvalidateVisual(); if (!App.Current.Spectrum.Active && !moving && _visibility.Gain == 0) _timer.Stop();
    }
    protected override void OnRender(DrawingContext d)
    {
        Resize(); if (VerificationAmplitudes is { Length: > 0 } fixture) for (int i = 0; i < _smooth.Length; i++) _smooth[i] = _peak[i] = fixture[i * fixture.Length / _smooth.Length]; var p = App.Current.Prefs.Spectrum; int count = _smooth.Length; double step = p.BarWidth + p.BarGap, start = Math.Floor((ActualWidth - count * step + p.BarGap) / 2), bottom = ActualHeight - 8, height = Math.Max(2, ActualHeight - 16);
        Brush ColorAt(int index) => p.Color == "white" ? Brushes.White : Rainbow[Math.Clamp(index, 0, 511)];
        bool fade = VerificationAmplitudes == null && p.HideWhenSilent;
        if (fade && _visibility.Gain <= .002) return;
        if (fade) d.PushOpacity(_visibility.Gain);
        if (p.Style == "blocks") for (int i = 0; i < count; i++) { double bar = Math.Max(2, _smooth[i] * height); var color = ColorAt(i * 511 / Math.Max(1, count - 1)); double segment = Math.Max(1.5, (bar - 10) / 6); for (int j = 0; j < 6 && j * (segment + 2) < bar; j++) d.DrawRoundedRectangle(color, null, new Rect(start + i * step, bottom - Math.Min(bar, j * (segment + 2) + segment), p.BarWidth, Math.Min(segment, bar - j * (segment + 2))), 1.5, 1.5); if (_peak[i] > .05) d.DrawRectangle(Brushes.White, null, new Rect(start + i * step, Math.Max(2, bottom - _peak[i] * height - 4), p.BarWidth, 2)); }
        else if (p.Style is "bars" or "mirror" or "dots")
        {
            for (int i = 0; i < count; i++) { double bar = Math.Max(2, _smooth[i] * (p.Style == "mirror" ? height * .48 : height)); var brush = ColorAt(i * 511 / Math.Max(1, count - 1)); double x = start + i * step;
                if (p.Style == "dots") { double radius = Math.Max(1, p.BarWidth / 2.0); for (double y = 0; y < bar; y += radius * 2 + 3) d.DrawEllipse(brush, null, new Point(x + p.BarWidth / 2.0, bottom - y - radius), radius, radius); }
                else d.DrawRoundedRectangle(brush, null, new Rect(x, p.Style == "mirror" ? ActualHeight / 2 - bar : bottom - bar, p.BarWidth, p.Style == "mirror" ? bar * 2 : bar), p.BarWidth / 2.0, p.BarWidth / 2.0);
            }
        }
        else if (p.Style == "ring")
        {
            double radius = Math.Min(ActualHeight, ActualWidth) * .23, maxBar = Math.Min(ActualHeight, ActualWidth) * .22; int n = Math.Min(count, 128);
            for (int i = 0; i < n; i++) { double a = i * 2 * Math.PI / n, amp = _smooth[i * count / n] * maxBar; var pen = new Pen(ColorAt(i * 511 / Math.Max(1, n - 1)), Math.Max(2, Math.Min(5, p.BarWidth))) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }; d.DrawLine(pen, new Point(ActualWidth / 2 + Math.Cos(a) * radius, ActualHeight / 2 + Math.Sin(a) * radius), new Point(ActualWidth / 2 + Math.Cos(a) * (radius + Math.Max(1, amp)), ActualHeight / 2 + Math.Sin(a) * (radius + Math.Max(1, amp)))); }
        }
        else if (p.Style == "mountains")
        {
            for (int layer = 2; layer >= 0; layer--) { var shape = new StreamGeometry(); using (var path = shape.Open()) { path.BeginFigure(new Point(start, bottom), true, true); for (int i = 0; i < count; i++) { double energy = (_smooth[Math.Max(0, i - 1)] + _smooth[i] * 2 + _smooth[Math.Min(count - 1, i + 1)]) / 4; path.LineTo(new Point(start + i * step, bottom - Math.Max(1, energy * height * (1 - layer * .2))), true, false); } path.LineTo(new Point(start + (count - 1) * step, bottom), true, false); } shape.Freeze(); d.PushOpacity(.35 + layer * .14); d.DrawGeometry(ColorAt(100 + layer * 170), null, shape); d.Pop(); }
        }

        else
        {
            double center = ActualHeight / 2; var gradient = new LinearGradientBrush(); gradient.StartPoint = new Point(0, .5); gradient.EndPoint = new Point(1, .5); for (int i = 0; i < 5; i++) gradient.GradientStops.Add(new GradientStop(((SolidColorBrush)ColorAt(i * 127)).Color, i / 4.0)); gradient.Freeze();
            var geometry = new StreamGeometry(); using (var path = geometry.Open()) { path.BeginFigure(new Point(start, center), p.Style == "waves", false); for (int i = 0; i < count; i++) path.LineTo(new Point(start + i * step + p.BarWidth / 2.0, center - _smooth[i] * height * .46), true, false); for (int i = count - 1; i >= 0; i--) path.LineTo(new Point(start + i * step + p.BarWidth / 2.0, center + _smooth[i] * height * .46), true, false); } geometry.Freeze(); d.DrawGeometry(p.Style == "waves" ? gradient.CloneCurrentValue() : null, new Pen(gradient, p.Style == "waves" ? 1 : 2), geometry);
        }
        if (fade) d.Pop();
    }
}
