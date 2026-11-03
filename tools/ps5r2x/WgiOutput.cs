// WgiOutput — drive the real Xbox One's 4 vibration motors via Windows.Gaming.Input.
// Verified usable in the earlier spike (tools/xbox4motor): LeftMotor/RightMotor/
// LeftTrigger/RightTrigger, each 0..1 float. WGI needs an async discovery warm-up.
using System;
using System.Collections.Generic;
using System.Threading;
using Windows.Gaming.Input;

namespace Ps5R2X;

internal sealed class WgiOutput : IDisposable
{
    private readonly List<Gamepad> _pads = new();
    public int Count => _pads.Count;

    /// <summary>Discover gamepads, polling + GamepadAdded for up to waitMs.</summary>
    public static WgiOutput Create(int waitMs = 4000, int index = -1)
    {
        var o = new WgiOutput();
        List<Gamepad> tmp = new();
        void Add(Gamepad g) { if (g != null && !tmp.Contains(g)) tmp.Add(g); }

        foreach (var g in Gamepad.Gamepads) Add(g);
        if (tmp.Count == 0 && waitMs > 0)
        {
            EventHandler<Gamepad> onAdded = (_, g) => Add(g);
            Gamepad.GamepadAdded += onAdded;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < waitMs && tmp.Count == 0)
            {
                Thread.Sleep(100);
                foreach (var g in Gamepad.Gamepads) Add(g);
            }
            Gamepad.GamepadAdded -= onAdded;
        }

        if (index >= 0 && index < tmp.Count) o._pads.Add(tmp[index]);
        else o._pads.AddRange(tmp);
        return o;
    }

    public void Set(float l, float r, float lt, float rt)
    {
        var v = new GamepadVibration
        {
            LeftMotor = Clamp01(l),
            RightMotor = Clamp01(r),
            LeftTrigger = Clamp01(lt),
            RightTrigger = Clamp01(rt),
        };
        foreach (var p in _pads)
        {
            try { p.Vibration = v; } catch { }
        }
    }

    public void Stop() => Set(0, 0, 0, 0);

    /// <summary>Re-scan for gamepads and add any new ones (handles late plug-in).</summary>
    public int Refresh()
    {
        try
        {
            foreach (var g in Gamepad.Gamepads)
            {
                if (g != null && !_pads.Contains(g)) _pads.Add(g);
            }
        }
        catch { }
        return _pads.Count;
    }

    public void Dispose() => Stop();

    private static float Clamp01(float v) => v < 0 ? 0 : (v > 1 ? 1 : v);
}
