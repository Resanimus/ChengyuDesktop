using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace ChengyuDock;
internal static class ClockPainter
{
    public static void Draw(DrawingContext d, FrameworkElement view, string style, Size? size = null)
    {
        double w = size?.Width ?? view.ActualWidth, h = size?.Height ?? view.ActualHeight; var now = DateTime.Now;
        void Text(string value, double x, double y, double size, bool bold = false, string color = "#F5F7FC", bool center = false)
        {
            var f = new FormattedText(value, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(new FontFamily("Segoe UI, Microsoft YaHei UI"), FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Light, FontStretches.Normal), size, Ui.Brush(color), VisualTreeHelper.GetDpi(view).PixelsPerDip);
            double origin = x; if (center) { var ink = f.BuildGeometry(new Point()).Bounds; origin -= ink.X + ink.Width / 2; }
            d.DrawText(f, new Point(origin, y));
        }
        var zh = CultureInfo.GetCultureInfo("zh-CN");
        switch (style)
        {
            case "minimal": Text(now.ToString("HH:mm"), w / 2, 6, 88, center: true); Text(now.ToString("M 月 d 日  dddd", zh), w / 2, 116, 16, center: true); break;
            case "flip":
                string digits = now.ToString("HHmmss"); double start = (w - 426) / 2;
                for (int i = 0; i < 6; i++) { double x = start + i * 72; d.DrawRoundedRectangle(Ui.Brush("#A5141820"), new Pen(Ui.Brush("#24FFFFFF"), .7), new Rect(x, 24, 64, 96), 10, 10); Text(digits[i].ToString(), x + 32, 23, 66, true, center: true); d.DrawLine(new Pen(Ui.Brush("#9010151D"), 2), new Point(x, 72), new Point(x + 64, 72)); }
                Text(now.ToString("yyyy.MM.dd · dddd", zh), w / 2, 138, 13, center: true); break;
            case "analog":
                var center = new Point(w / 2, h / 2); double r = Math.Min(76, h / 2 - 10);
                d.DrawEllipse(Ui.Brush("#10141C24"), new Pen(Ui.Brush("#65FFFFFF"), 1), center, r, r);
                for (int i = 0; i < 60; i++) { double a = i * Math.PI / 30; double inner = r - (i % 5 == 0 ? 10 : 4); d.DrawLine(new Pen(Ui.Brush(i % 5 == 0 ? "#E6FFFFFF" : "#60FFFFFF"), i % 5 == 0 ? 2 : 1), new Point(center.X + Math.Sin(a) * inner, center.Y - Math.Cos(a) * inner), new Point(center.X + Math.Sin(a) * (r - 2), center.Y - Math.Cos(a) * (r - 2))); }
                void Hand(double unit, double length, double width, string color) { double a = unit * Math.PI * 2; var pen = new Pen(Ui.Brush(color), width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }; d.DrawLine(pen, center, new Point(center.X + Math.Sin(a) * length, center.Y - Math.Cos(a) * length)); }
                Hand((now.Hour % 12 + now.Minute / 60.0) / 12, r * .48, 5, "#F4F7FC"); Hand((now.Minute + now.Second / 60.0) / 60, r * .72, 3, "#F4F7FC"); Hand(now.Second / 60.0, r * .82, 1.3, "#66CEFF"); d.DrawEllipse(Ui.Brush("#66CEFF"), null, center, 3, 3); break;
            case "chinese": Text(now.ToString("M 月 d 日", zh), w / 2, 10, 32, center: true); Text(now.ToString("HH:mm:ss"), w / 2, 48, 64, center: true); Text(now.ToString("yyyy 年 · dddd", zh), w / 2, 132, 16, center: true); break;
            case "stacked": Text(now.ToString("HH"), w / 2 - 45, 0, 76, true, center: true); Text(now.ToString("mm"), w / 2 + 45, 73, 76, true, center: true); Text(now.ToString("ddd  MM.dd", CultureInfo.InvariantCulture).ToUpperInvariant(), w / 2 + 95, 49, 13, color: "#ADBBD0"); break;
            default:
                Text(string.Join(" ", now.ToString("dddd", CultureInfo.InvariantCulture).ToUpperInvariant().ToCharArray()), w / 2, 7, 42, center: true);
                Text(now.ToString("HH:mm:ss"), w / 2, 78, 25, center: true); Text(now.ToString("MMMM dd, yyyy", CultureInfo.InvariantCulture).ToUpperInvariant(), w / 2, 120, 15, center: true); break;
        }
    }
}
