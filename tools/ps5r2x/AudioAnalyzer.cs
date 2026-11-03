// AudioAnalyzer — WASAPI loopback capture of one render endpoint, computing
// per-channel RMS plus the per-side motor envelope.
//  - >=4 channels (a virtual DualSense's USB audio: ch1/2 speaker, ch3/4 haptic L/R):
//      motor L/R come from ch3/ch4 (0-based index 2/3); ALL channels' RMS are kept
//      so we can numerically show ch3/4 rise while ch1/2 stay low.
//  - stereo/mono: low-pass L/R first ("bass extraction") and use their RMS.
using System;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Ps5R2X;

internal sealed class AudioAnalyzer : IDisposable
{
    private readonly WasapiLoopbackCapture _cap;
    private readonly WaveFormat _wf;
    private readonly int _ch;
    private readonly bool _haptic;          // >=4 channels -> use ch3/4
    private readonly bool _useHighpass;
    private readonly double _alphaLo, _alphaHi;
    private double _hpMainL, _hpMainR;   // high-pass stage (band-lo) for ch1/2
    private double _lpMainL, _lpMainR;   // low-pass stage (band-hi) for ch1/2
    private readonly object _lock = new();
    private float _rmsL, _rmsR;
    private float _mainRmsL, _mainRmsR;
    private float[] _rmsCh = Array.Empty<float>();
    private long _blocks;

    /// <summary>Optional sink receiving interleaved stereo (ch1/ch2 = main audio) frames
    /// for real-time mirroring to a real speaker. Called on the capture thread.</summary>
    public Action<float[], int>? MainAudioSink;

    public int Channels => _ch;
    public int SampleRate => _wf.SampleRate;
    public string Mode { get; }
    public long Blocks => Interlocked.Read(ref _blocks);

    public AudioAnalyzer(MMDevice device, double bandLo, double bandHi)
    {
        _cap = new WasapiLoopbackCapture(device);
        _wf = _cap.WaveFormat;
        _ch = Math.Max(1, _wf.Channels);
        _haptic = _ch >= 4;
        int rate = Math.Max(8000, _wf.SampleRate);
        _useHighpass = bandLo > 0;
        _alphaLo = _useHighpass ? Math.Clamp(1.0 - Math.Exp(-2.0 * Math.PI * bandLo / rate), 0.0001, 1.0) : 1.0;
        _alphaHi = Math.Clamp(1.0 - Math.Exp(-2.0 * Math.PI * bandHi / rate), 0.0001, 1.0);
        Mode = _haptic
            ? (_useHighpass ? $"haptic ch3/4 + 主音频带通 {bandLo:F0}-{bandHi:F0}Hz (channels={_ch})"
                            : $"haptic ch3/4 + 主音频低通 {bandHi:F0}Hz (channels={_ch})")
            : (_useHighpass ? $"主音频带通 {bandLo:F0}-{bandHi:F0}Hz (channels={_ch})"
                            : $"主音频低通 {bandHi:F0}Hz (channels={_ch})");
        _cap.DataAvailable += OnData;
    }

    public void Start() => _cap.StartRecording();

    public (float l, float r) GetRms()
    {
        lock (_lock) return (_rmsL, _rmsR);
    }

    /// <summary>RMS per channel (0..1), copy. Index i = channel i+1.</summary>
    public float[] GetChannelRms()
    {
        lock (_lock) return (float[])_rmsCh.Clone();
    }

    /// <summary>Low-passed ch1/ch2 (main audio) RMS, used by --source main/auto.</summary>
    public (float l, float r) GetMainRms()
    {
        lock (_lock) return (_mainRmsL, _mainRmsR);
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0) return;

        int ch = _ch;
        int bps = _wf.BitsPerSample;
        var enc = _wf.Encoding;
        bool isFloat = enc == WaveFormatEncoding.IeeeFloat || (enc == WaveFormatEncoding.Extensible && bps == 32);
        bool isPcm16 = (enc == WaveFormatEncoding.Pcm && bps == 16) || (enc == WaveFormatEncoding.Extensible && bps == 16);
        bool isPcm24 = enc == WaveFormatEncoding.Pcm && bps == 24;
        bool isPcm32 = enc == WaveFormatEncoding.Pcm && bps == 32;

        float[]? fbuf = null; short[]? sbuf = null; int[]? ibuf = null; byte[]? b24 = null;
        int frames;
        if (isFloat && bps == 32)
        {
            int n = e.BytesRecorded / 4;
            fbuf = new float[n];
            Buffer.BlockCopy(e.Buffer, 0, fbuf, 0, n * 4);
            frames = n / ch;
        }
        else if (isPcm16)
        {
            int n = e.BytesRecorded / 2;
            sbuf = new short[n];
            Buffer.BlockCopy(e.Buffer, 0, sbuf, 0, n * 2);
            frames = n / ch;
        }
        else if (isPcm32)
        {
            int n = e.BytesRecorded / 4;
            ibuf = new int[n];
            Buffer.BlockCopy(e.Buffer, 0, ibuf, 0, n * 4);
            frames = n / ch;
        }
        else if (isPcm24)
        {
            b24 = e.Buffer;
            frames = e.BytesRecorded / (3 * ch);
        }
        else
        {
            return;
        }

        if (frames <= 0) return;

        float Sample(int idx)
        {
            if (fbuf != null) return idx < fbuf.Length ? fbuf[idx] : 0f;
            if (sbuf != null) return idx < sbuf.Length ? sbuf[idx] / 32768f : 0f;
            if (ibuf != null) return idx < ibuf.Length ? ibuf[idx] / 2147483648f : 0f;
            if (b24 != null)
            {
                int o = idx * 3;
                if (o + 2 >= b24.Length) return 0f;
                int v = b24[o] | (b24[o + 1] << 8) | (b24[o + 2] << 16);
                if ((v & 0x800000) != 0) v |= unchecked((int)0xFF000000);
                return v / 8388608f;
            }
            return 0f;
        }

        var sums = new double[ch];
        double sumsMainL = 0, sumsMainR = 0;
        int ridx = Math.Min(1, ch - 1);
        for (int i = 0; i < frames; i++)
        {
            int b = i * ch;
            if (_haptic)
            {
                for (int c = 0; c < ch; c++)
                {
                    float v = Sample(b + c);
                    sums[c] += (double)v * v;
                }
            }
            // ch1/ch2 band-pass (band-lo high-pass -> band-hi low-pass) for --source main/auto
            float rawL = Sample(b);
            float rawR = Sample(b + ridx);
            float hpL = rawL, hpR = rawR;
            if (_useHighpass)
            {
                _hpMainL += _alphaLo * (rawL - _hpMainL);
                _hpMainR += _alphaLo * (rawR - _hpMainR);
                hpL = rawL - _hpMainL;
                hpR = rawR - _hpMainR;
            }
            _lpMainL += _alphaHi * (hpL - _lpMainL);
            _lpMainR += _alphaHi * (hpR - _lpMainR);
            sumsMainL += (double)_lpMainL * _lpMainL;
            sumsMainR += (double)_lpMainR * _lpMainR;
        }
        if (!_haptic)
        {
            // stereo/mono: the motor envelope is the low-passed main audio
            sums[0] = sumsMainL;
            if (ch > 1) sums[1] = sumsMainR;
        }

        var rmsCh = new float[ch];
        for (int c = 0; c < ch; c++) rmsCh[c] = (float)Math.Sqrt(sums[c] / frames);

        float mainRmsL = (float)Math.Sqrt(sumsMainL / frames);
        float mainRmsR = (float)Math.Sqrt(sumsMainR / frames);

        float rmsL = _haptic ? (ch > 2 ? rmsCh[2] : 0f) : rmsCh[0];
        float rmsR = _haptic ? (ch > 3 ? rmsCh[3] : 0f) : rmsCh[ridx];

        // Mirror ch1/ch2 (main audio) to a real speaker if a sink is attached.
        var sink = MainAudioSink;
        if (sink != null)
        {
            var st = new float[frames * 2];
            for (int i = 0; i < frames; i++)
            {
                int b = i * ch;
                st[2 * i] = Sample(b);
                st[2 * i + 1] = Sample(b + ridx);
            }
            try { sink(st, frames); } catch { }
        }

        lock (_lock)
        {
            _rmsL = rmsL;
            _rmsR = rmsR;
            _rmsCh = rmsCh;
            _mainRmsL = mainRmsL;
            _mainRmsR = mainRmsR;
        }
        Interlocked.Increment(ref _blocks);
    }

    public void Dispose()
    {
        try { _cap.StopRecording(); } catch { }
        try { _cap.Dispose(); } catch { }
    }
}
