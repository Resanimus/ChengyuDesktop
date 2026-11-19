using System;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace ChengyuDock;
internal static class IslandPainter
{
    static readonly string[] Titles = { "本地音乐", "日历", "今日待办", "天气", "时钟", "专注" };
    public static void Draw(DrawingContext d, FrameworkElement view, WidgetWindow owner)
    {
        double w = view.ActualWidth, h = view.ActualHeight, blend = owner.Island.Expansion; var model = owner.Data;
        var weather = model.GetProperty("weather"); var music = model.GetProperty("music"); var media = owner.PresentedMedia; double dpi = VisualTreeHelper.GetDpi(view).PixelsPerDip;
        void Text(string value, double x, double y, double size = 13, string color = "#F2F5FA", double max = 0, bool bold = false, bool centered = false)
        { var text = new FormattedText(value, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(new FontFamily("Segoe UI, Microsoft YaHei UI"), FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal), size, Ui.Brush(color), dpi); if (max > 0) { text.MaxTextWidth = max; text.MaxLineCount = 1; text.Trimming = TextTrimming.CharacterEllipsis; } if (centered) { var ink = text.BuildGeometry(new Point()).Bounds; x -= ink.X + ink.Width / 2; } d.DrawText(text, new Point(x, y)); }
        string Get(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind != JsonValueKind.Null ? v.ToString() : "--";
        string Temp(string key) => double.TryParse(Get(weather, key), out double n) ? Math.Round(n) + "°" : "--°";
        void Cover(double x, double y, double size)
        {
            d.PushClip(new RectangleGeometry(new Rect(x, y, size, size), size * .3, size * .3));
            if (media.Cover != null) d.DrawImage(media.Cover, new Rect(x, y, size, size));
            else { d.DrawRoundedRectangle(Ui.Brush("#24FFFFFF"), null, new Rect(x, y, size, size), size * .3, size * .3); Text("♫", x + size * .24, y + size * .07, size * .68, "#C4D6EC"); } d.Pop();
        }
        void Meter(double x, double cy, double maxHeight)
        {
            var brush = new SolidColorBrush(media.Accent ?? Color.FromRgb(48, 209, 88)); brush.Freeze();
            d.PushOpacity(media.Playing ? 1 : .45);
            for (int i = 0; i < 5; i++) { double level = media.Playing ? App.Current.Media.Levels[i] : .2; double bar = Math.Max(2.5, level * maxHeight); d.DrawRoundedRectangle(brush, null, new Rect(x + i * 3.9, cy - bar / 2, 2.5, bar), 1.25, 1.25); } d.Pop();
        }
        d.PushOpacity(Math.Clamp(1 - 3 * blend, 0, 1));
        if (!string.IsNullOrEmpty(media.Title)) { double size = Math.Max(12, h - 12); Cover(8, 6, size); Text(media.Title + (string.IsNullOrEmpty(media.Artist) ? "" : " – " + media.Artist), size + 16, h / 2 - 8, 12, max: Math.Max(20, w - size - 55), bold: true); Meter(w - 29, h / 2, Math.Max(6, h - 14)); }
        else { d.DrawEllipse(Ui.Brush("#43E996"), null, new Point(18, h / 2), 3, 3); Text(DateTime.Now.ToString("HH:mm"), w / 2, Math.Max(6, h / 2 - 8), 12, bold: true, centered: true); } d.Pop();
        double expandedAlpha = Math.Clamp((blend - .55) / .45, 0, 1); if (expandedAlpha < .002) return;
        d.PushOpacity(expandedAlpha * Math.Clamp(owner.Island.Content.Value, 0, 1)); d.PushTransform(new TranslateTransform(owner.Island.Slide.Value, (1 - blend) * 10));
        Text(Titles[owner.Page], 22, 14, 12, "#B3C5DC", bold: true);

        switch (owner.Page)
        {
            case 0:
                Cover(23, 43, 43); Text(string.IsNullOrEmpty(media.Title) ? "选择音乐，开始聆听" : media.Title, 81, 44, 15, max: 248, bold: true);
                Text(string.IsNullOrEmpty(media.Artist) ? media.Playing ? "正在播放" : "本地音乐" : media.Artist, 81, 70, 11, "#96A9C2", max: 240); Meter(w - 32, 65, 24);
                d.DrawRoundedRectangle(Ui.Brush("#25FFFFFF"), null, new Rect(24, 92, Math.Max(1, w - 48), 2), 1, 1);
                if (media.Duration > 0) d.DrawRoundedRectangle(new SolidColorBrush(media.Accent ?? Color.FromRgb(48, 209, 88)), null, new Rect(24, 92, Math.Max(1, (w - 48) * Math.Clamp(media.Position / media.Duration, 0, 1)), 2), 1, 1); break;
            case 1:
                Text(DateTime.Today.ToString("yyyy 年 M 月"), 23, 45, 17, bold: true); string[] days = { "日", "一", "二", "三", "四", "五", "六" }; int start = (int)new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).DayOfWeek;
                for (int i = 0; i < 7; i++) Text(days[i], 27 + i * 51, 78, 10, "#90A3BD");
                int week = (DateTime.Today.Day - 1 + start) / 7; for (int i = 0; i < 7; i++) { int day = week * 7 + i - start + 1; if (day < 1 || day > DateTime.DaysInMonth(DateTime.Today.Year, DateTime.Today.Month)) continue; double x = 27 + i * 51; if (day == DateTime.Today.Day) d.DrawRoundedRectangle(Ui.Brush("#367CF4"), null, new Rect(x - 7, 103, 32, 32), 10, 10); Text(day.ToString(), x, 110, 13); } Text("本周", 25, 150, 11, "#90A3BD"); break;
            case 2:
                var items = App.Current.Prefs.Todos.Where(t => t.Date == DateTime.Today.ToString("yyyy-MM-dd")).ToArray();
                if (items.Length == 0) { Text("今天没有待办", 24, 59, 22, bold: true); Text("留一点空白给自己", 25, 97, 12, "#91A5C0"); }
                else { int row = 0; foreach (var todo in items.Take(3)) { Text((todo.Done ? "✓  " : "○  ") + todo.Text, 25, 53 + row++ * 29, 13, todo.Done ? "#91A5C0" : "#EDF2FA", max: 350); } } break;
            case 3:
                Text(Get(weather, "Icon") + "  " + Temp("Temperature"), 25, 51, 42); Text(Get(weather, "City"), 224, 57, 16, max: 155, bold: true); Text(Get(weather, "Condition"), 224, 86, 12, "#B7C7DD"); Text("最高 " + Temp("High") + "   最低 " + Temp("Low"), 25, 129, 12, "#9FB2CE"); break;
            case 4:
                Text(DateTime.Now.ToString("HH:mm:ss"), 25, 48, 48, bold: true); Text(DateTime.Today.ToString("M 月 d 日 · dddd", CultureInfo.GetCultureInfo("zh-CN")), 26, 119, 14, "#A9BCD7"); break;
            case 5:
                Text($"{Math.Max(0, (int)owner.FocusRemaining.TotalMinutes):00}:{Math.Max(0, owner.FocusRemaining.Seconds):00}", 25, 49, 48, bold: true); Text(owner.FocusRunning ? "专注进行中" : "给自己一段专注时间", 230, 78, 12, "#A9BCD7"); break;
        }
        d.Pop(); d.Pop();
    }
}
