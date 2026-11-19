using System;

namespace ChengyuDock;
// A display-only noise gate: separate thresholds, hold, attack and release.
// It never modifies the captured audio or the system volume.
internal sealed class AudioVisibilityGate
{
    double _quiet;
    public bool Open { get; private set; }
    public double Gain { get; private set; }
    public double Step(double db, double dt, double closeDb)
    {
        dt = Math.Clamp(dt, 0, .25);
        if (!double.IsFinite(db)) db = -120;
        if (db >= closeDb + 6) { Open = true; _quiet = 0; }
        else if (Open && db < closeDb) { _quiet += dt; if (_quiet >= .2) Open = false; }
        else _quiet = 0;
        double target = Open ? 1 : 0;
        Gain += (target - Gain) * (1 - Math.Exp(-dt / (Open ? .035 : .16)));
        if (!Open && Gain < .002) Gain = 0;
        return Gain;
    }
}
