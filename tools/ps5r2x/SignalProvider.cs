// SignalProvider — demo/inject signal generator.
// Modes:
//   sweep (default): 40–120 Hz sine sweep with a 1.5 Hz amplitude pulse.
//   pulse: 50 Hz carrier gated into 50 ms bursts every 250 ms (simulates 爆炸/受击 low-freq impacts).
//   sine : continuous 50 Hz sine (simulates sustained music/bass).
//   high : continuous 2 kHz sine (simulates voice / high-frequency sound; should be filtered out).
// Writes to specific channels of the given WaveFormat (e.g. only ch3/4 or only ch1/2).
using System;
using NAudio.Wave;

namespace Ps5R2X;

internal sealed class SignalProvider : ISampleProvider
{
    private readonly int _ch;
    private readonly int _rate;
    private readonly int[]? _active;   // null = all channels; else 0-based indices
    private readonly double _sweepPeriodSec;
    private readonly double _f0, _f1;
    private readonly double _amp;
    private readonly string _mode;
    private double _phase;
    private long _sample;

    public WaveFormat WaveFormat { get; }

    /// <summary>Use the device's exact MixFormat so WASAPI shared mode accepts it.</summary>
    public SignalProvider(WaveFormat format, int[]? activeChannels = null,
        double f0 = 40, double f1 = 120, double sweepPeriodSec = 4.0, double amp = 0.5,
        string mode = "sweep")
    {
        WaveFormat = format;
        _ch = Math.Max(1, format.Channels);
        _rate = format.SampleRate;
        _active = activeChannels;
        _f0 = f0; _f1 = f1; _sweepPeriodSec = sweepPeriodSec; _amp = amp;
        _mode = mode;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int frames = count / _ch;
        for (int i = 0; i < frames; i++)
        {
            double t = (double)_sample / _rate;
            double v;
            switch (_mode)
            {
                case "pulse":
                    {
                        // 50 ms burst every 250 ms, raised-sine envelope, 50 Hz carrier
                        double pos = t % 0.25;
                        double env = 0.0;
                        if (pos < 0.05) env = Math.Sin(Math.PI * (pos / 0.05));
                        v = _amp * 1.0 * env * Math.Sin(2 * Math.PI * 50.0 * t);
                        break;
                    }
                case "sine":
                    v = _amp * 0.8 * Math.Sin(2 * Math.PI * 50.0 * t);
                    break;
                case "high":
                    v = _amp * 0.8 * Math.Sin(2 * Math.PI * 2000.0 * t);
                    break;
                default: // sweep
                    {
                        double frac = (t % _sweepPeriodSec) / _sweepPeriodSec;
                        double f = _f0 + (_f1 - _f0) * frac;
                        _phase += 2.0 * Math.PI * f / _rate;
                        if (_phase > 2 * Math.PI) _phase -= 2 * Math.PI;
                        double pulse = 0.35 + 0.65 * (0.5 + 0.5 * Math.Sin(2 * Math.PI * 1.5 * t));
                        v = _amp * pulse * Math.Sin(_phase);
                        break;
                    }
            }

            int b = offset + i * _ch;
            if (_active == null)
            {
                for (int c = 0; c < _ch; c++) buffer[b + c] = (float)v;
            }
            else
            {
                for (int c = 0; c < _ch; c++) buffer[b + c] = 0f;
                foreach (int c in _active)
                    if (c >= 0 && c < _ch) buffer[b + c] = (float)v;
            }
            _sample++;
        }
        return frames * _ch;
    }
}
