using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace ChengyuDock;
// Port of the supplied snap-engine.js. All coordinates are device-independent pixels.
internal sealed class SnapEngine
{
    internal sealed record Match(double Position, int Kind, double Distance, double Gap, double Center, double Guide);
    Match? _x, _y;
    public void Reset() { _x = _y = null; }
    internal static Match? Solve(double pos, double size, double other, double otherSize, IEnumerable<Rect> peers, double start, double end, bool x, Match? latch)
    {
        if (latch != null && Math.Abs(pos - latch.Position) <= 12) return latch;
        var candidates = new List<Match>();
        void Add(double target, int kind, double gap, double center, double guide) { double d = Math.Abs(target - pos); if (d <= 8) candidates.Add(new(target, kind, d, gap, center, guide)); }
        foreach (var p in peers)
        {
            double a = x ? p.Left : p.Top, n = x ? p.Width : p.Height, b = x ? p.Top : p.Left, m = x ? p.Height : p.Width;
            double gap = Math.Max(0, Math.Max(other - b - m, b - other - otherSize));
            if (gap > 10) continue;
            double center = Math.Abs(other + otherSize / 2 - b - m / 2);
            Add(a, 0, gap, center, a); Add(a + n - size, 0, gap, center, a + n);
            Add(a - 8 - size, 1, gap, center, a - 4); Add(a + n + 8, 1, gap, center, a + n + 4);
        }
        Add(start, 2, 0, 0, start); Add(end - size, 2, 0, 0, end);
        return candidates.OrderBy(c => Math.Round(c.Distance, 3)).ThenBy(c => c.Kind).ThenBy(c => c.Gap).ThenBy(c => c.Center).FirstOrDefault();
    }
    public (Point Point, double? X, double? Y) Move(Rect raw, IEnumerable<Rect> peers, Rect work)
    {
        var list = peers.ToArray(); _x = Solve(raw.X, raw.Width, raw.Y, raw.Height, list, work.Left, work.Right, true, _x);
        _y = Solve(raw.Y, raw.Height, raw.X, raw.Width, list, work.Top, work.Bottom, false, _y);
        return (new Point(_x?.Position ?? raw.X, _y?.Position ?? raw.Y), _x?.Guide, _y?.Guide);
    }
}
