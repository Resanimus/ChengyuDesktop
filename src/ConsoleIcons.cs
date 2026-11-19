using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ChengyuDock;

internal static class ConsoleIcons
{
    public static Image Draw(string key, double size = 18, string color = "#A8C4D6")
    {
        string path = key switch
        {
            "overview" => "M4,10 L12,4 20,10 20,19 15,19 15,13 9,13 9,19 4,19 Z",
            "components" => "M4,4 H10 V10 H4 Z M14,4 H20 V10 H14 Z M4,14 H10 V20 H4 Z M14,14 H20 V20 H14 Z",
            "dock" => "M6,5 H18 Q20,5 20,7 V14 Q20,16 18,16 H6 Q4,16 4,14 V7 Q4,5 6,5 M7,20 H17 M7,12 H17",
            "island" => "M7,7 H17 A5,5 0 0 1 17,17 H7 A5,5 0 0 1 7,7 M7,12 H8 M13,12 H17",
            "spectrum" => "M3,11 V13 M7.5,7 V17 M12,4 V20 M16.5,8 V16 M21,11 V13",
            "storage" => "M4,9 H20 V19 H4 Z M3,5 H21 V9 H3 Z M9,13 H15",
            "wallpaper" => "M4,5 H20 V19 H4 Z M5,17 L10,11 14,15 17,12 20,15 M15.5,8.5 H16.5",
            "performance" => "M3,12 H7 L10,6 14,18 17,12 H21",
            "calendar" => "M4,6 H20 V20 H4 Z M4,10 H20 M8,4 V8 M16,4 V8 M8,14 H11 M8,17 H14",
            "content" => "M6,4 H14 L18,8 V20 H6 Z M14,4 V8 H18 M9,12 H15 M9,16 H15",
            "settings" => "M5,5 H19 M5,12 H19 M5,19 H19 M9,3 V7 M15,10 V14 M11,17 V21",
            "minimize" => "M6,12 H18",
            "maximize" => "M6,6 H18 V18 H6 Z",
            "close" => "M7,7 L17,17 M17,7 L7,17",
            "arrow" => "M5,12 H19 M14,7 L19,12 14,17",
            _ => "M12,5 V19 M5,12 H19"
        };
        var group = new DrawingGroup();
        using (var drawing = group.Open())
        {
            drawing.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, 24, 24));
            var pen = new Pen(Ui.Brush(color), 1.55) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            drawing.DrawGeometry(null, pen, Geometry.Parse(path));
        }
        group.Freeze();
        return new Image { Source = new DrawingImage(group), Width = size, Height = size, IsHitTestVisible = false };
    }
}
