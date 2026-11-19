using System;

namespace ChengyuDock;
internal sealed class MotionChannel
{
    public double Value, Velocity, Target;
    readonly double _k, _damping;
    public MotionChannel(double k, double damping, double value) { _k = k; _damping = damping; Value = Target = value; }
    public bool Step(double dt, bool reduced)
    {
        if (reduced) { Value = Target; Velocity = 0; return false; }
        for (double remaining = Math.Clamp(dt, 0, .1); remaining > 0;) { double step = Math.Min(.002, remaining); Velocity += (-_k * (Value - Target) - _damping * Velocity) * step; Value += Velocity * step; remaining -= step; }
        if (Math.Abs(Value - Target) < .015 && Math.Abs(Velocity) < .06) { Value = Target; Velocity = 0; return false; } return true;
    }
}
internal sealed class IslandMotion
{
    public readonly MotionChannel Width = new(210, 21, 120), Height = new(260, 24, 30), Content = new(260, 30, 1), Slide = new(260, 30, 0), Press = new(300, 34, 0);
    public readonly MotionChannel Hide = new(170, 24, 1);
    double _revealUntil; string _mediaKey = ""; bool _playing;
    public bool Expanded { get; private set; }
    public bool Hovered, HasMedia, HoverOpened, MenuOpen;
    public int Page { get; private set; }
    public int PreviousPage { get; private set; }
    double _openAt = double.PositiveInfinity, _closeAt = double.PositiveInfinity;
    public double Expansion => Math.Clamp((Height.Value - 34) / 122, 0, 1);
    public void Enter(double time) { Hovered = true; _closeAt = double.PositiveInfinity; _openAt = Expanded ? double.PositiveInfinity : time + .320; Retarget(); }
    public void Leave(double time) { _revealUntil = Math.Max(_revealUntil, time + 1.2); Hovered = false; _openAt = double.PositiveInfinity; if (Expanded) _closeAt = time + (HoverOpened ? .450 : 3.5); Retarget(); }
    public void Click(double time) { Expanded = !Expanded; HoverOpened = false; _openAt = double.PositiveInfinity; _closeAt = Hovered || !Expanded ? double.PositiveInfinity : time + .450; Retarget(); }
    public void Wheel(int direction, double time) { if (!Expanded) { Expanded = true; HoverOpened = false; SetPage(0, direction); } else SetPage((Page + direction + 6) % 6, direction); _closeAt = Hovered ? double.PositiveInfinity : time + .450; _openAt = double.PositiveInfinity; Retarget(); }
    public void SetPage(int page, int direction) { if (Page == page) return; PreviousPage = Page; Page = (page + 6) % 6; Content.Value = Math.Min(Content.Value, .22); Content.Target = 1; Slide.Value += direction * 26; Slide.Value = Math.Clamp(Slide.Value, -42, 42); Slide.Target = 0; Retarget(); }
    public void Retarget()
    {
        Width.Target = Expanded ? Page == 0 ? 372 : 404 : HasMedia ? Hovered ? 248 : 236 : Hovered ? 126 : 120;
        Height.Target = Expanded ? Page == 0 ? 156 : 208 : HasMedia ? Hovered ? 37 : 34 : Hovered ? 31.5 : 30;
    }
    public void Media(bool hasMedia, bool playing, string key, double time)
    {
        if (playing && (!_playing || key != _mediaKey)) _revealUntil = time + 3;
        HasMedia = hasMedia; _playing = playing; _mediaKey = key; Retarget();
    }
    public bool Step(double time, double dt, bool reduced)
    {
        if (!MenuOpen && Hovered && time >= _openAt) { Expanded = true; HoverOpened = true; _openAt = double.PositiveInfinity; Retarget(); }
        if (!MenuOpen && !Hovered && time >= _closeAt) { Expanded = false; HoverOpened = false; _closeAt = double.PositiveInfinity; Retarget(); }
        Hide.Target = !Hovered && !Expanded && !MenuOpen && time >= _revealUntil ? 1 : 0;
        bool active = Width.Step(dt, reduced) | Height.Step(dt, reduced) | Content.Step(dt, reduced) | Slide.Step(dt, reduced) | Press.Step(dt, reduced) | Hide.Step(dt, reduced);
        return active || double.IsFinite(_openAt) || double.IsFinite(_closeAt) || time < _revealUntil;
    }
}
