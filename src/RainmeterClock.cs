using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace ChengyuDock;

internal static class RainmeterClock
{
    static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    static readonly Lazy<Typeface[]> Fonts = new(() =>
    {
        static Typeface Face(string name, FontWeight weight) =>
            new(new FontFamily(name), FontStyles.Normal, weight, FontStretches.Normal);
        return new[]
        {
            Face("Segoe Script", FontWeights.Normal),
            Face("Segoe UI Light", FontWeights.Normal),
            Face("Bahnschrift", FontWeights.Normal),
            Face("Segoe UI", FontWeights.Bold),
            Face("Gabriola", FontWeights.Normal),
            Face("Consolas", FontWeights.Normal)
        };
    });

    internal static bool IsStyle(string style) => style is "welfare" or "mond" or "insideDream";
    internal static Size CanvasSize(string style) => style switch
    {
        "welfare" => new Size(360, 150),
        "mond" => new Size(721, 175),
        "insideDream" => new Size(520, 225),
        _ => new Size(630, 180)
    };

    internal static bool IsTextColor(string value) => value.Length == 7 && value[0] == '#' &&
        uint.TryParse(value.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out _);

    internal static void Draw(DrawingContext d, FrameworkElement view, Preferences prefs)
    {
        if (view.ActualWidth <= 0 || view.ActualHeight <= 0) return;
        var canvas = CanvasSize(prefs.ClockStyle);
        double scale = Math.Min(1, Math.Min(view.ActualWidth / canvas.Width, view.ActualHeight / canvas.Height));
        d.PushTransform(new TranslateTransform((view.ActualWidth - canvas.Width * scale) / 2,
            (view.ActualHeight - canvas.Height * scale) / 2));
        d.PushTransform(new ScaleTransform(scale, scale));
        d.PushOpacity(prefs.ClockOpacity / 100.0);

        var now = DateTime.Now;
        var fonts = Fonts.Value;
        double dpi = VisualTreeHelper.GetDpi(view).PixelsPerDip;
        string day = now.ToString("dddd", English);
        string month = now.ToString("MMMM", English);
        string date = now.ToString("dd", English);
        string time = now.ToString("HH:mm", English);
        Brush primary = prefs.ClockTextColor == null ? Brushes.White : Ui.Brush(prefs.ClockTextColor);

        void Line(string text, double y, Typeface font, double size, double spacing = 0,
            Brush? brush = null, double opacity = 1)
        {
            FormattedText Format(string value) => new(value, English, FlowDirection.LeftToRight,
                font, size, Brushes.White, dpi);
            Geometry ink;
            if (spacing == 0) ink = Format(text).BuildGeometry(new Point());
            else
            {
                var line = new GeometryGroup { FillRule = FillRule.Nonzero };
                double x = 0;
                foreach (char character in text)
                {
                    var glyph = Format(character.ToString());
                    line.Children.Add(glyph.BuildGeometry(new Point(x, 0)));
                    x += glyph.WidthIncludingTrailingWhitespace + spacing;
                }
                ink = line;
            }
            var bounds = ink.Bounds;
            if (bounds.IsEmpty) return;
            double fit = Math.Min(1, canvas.Width / bounds.Width);
            d.PushOpacity(opacity);
            d.PushTransform(new TranslateTransform(canvas.Width / 2 - (bounds.Left + bounds.Width / 2) * fit, y));
            d.PushTransform(new ScaleTransform(fit, fit));
            d.DrawGeometry(brush ?? primary, null, ink);
            d.Pop();
            d.Pop();
            d.Pop();
        }

        switch (prefs.ClockStyle)
        {
            case "welfare":
                Line(day, 0, fonts[0], 93.333, 2, opacity: 90 / 255.0);
                Line(time, 50, fonts[1], 53.333, 2);
                Line($"{date} {month}", 105, fonts[1], 26.667, 2);
                break;
            case "mond":
                Line(day.ToUpperInvariant(), 0, fonts[2], 56.533, 20);
                Line($"{date}  {month},  {now.Year}.".ToUpperInvariant(), 79.5, fonts[3], 19.787);
                Line($"- {time} -", 127.2, fonts[3], 19.787);
                break;
            case "insideDream":
                Line(day, 0, fonts[4], 128);
                Line($"{date} {month},{now.Year}.".ToUpperInvariant(), 160, fonts[5], 16, -2,
                    prefs.ClockTextColor == null ? Ui.Brush("#A8B5DB") : primary);
                Line(now.ToString("hh:mm'|'tt", English), 182, fonts[5], 21.333);
                break;
        }
        d.Pop();
        d.Pop();
        d.Pop();
    }

    internal static bool FontsLoaded() => Array.TrueForAll(Fonts.Value, face => face.TryGetGlyphTypeface(out _));
}

internal sealed class RainmeterClockPreview : FrameworkElement
{
    public RainmeterClockPreview()
    {
        Height = 225;
        ClipToBounds = true;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        int minute = -1;
        timer.Tick += (_, _) =>
        {
            if (!IsVisible || DateTime.Now.Minute == minute) return;
            minute = DateTime.Now.Minute;
            InvalidateVisual();
        };
        Loaded += (_, _) => timer.Start();
        Unloaded += (_, _) => timer.Stop();
    }

    protected override void OnRender(DrawingContext drawingContext) =>
        RainmeterClock.Draw(drawingContext, this, App.Current.Prefs);
}
