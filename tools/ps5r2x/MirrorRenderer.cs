// MirrorRenderer — push captured stereo main audio (ch1/ch2) in real time to a
// real speaker endpoint via raw WASAPI shared mode. A small ring buffer absorbs
// jitter between the capture callback and the render thread; on underrun it
// writes silence so the device never stalls.
using System;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Ps5R2X;

internal sealed class MirrorRenderer : IDisposable
{
    private readonly MMDevice _dev;
    private readonly WaveFormat _fmt;
    private readonly int _outCh;
    private AudioClient? _ac;
    private AudioRenderClient? _rc;
    private Thread? _thread;
    private volatile bool _run;

    private readonly object _lock = new();
    private readonly float[] _ring;
    private readonly int _cap;
    private int _w, _r;

    private long _writtenFrames;
    private float _level;

    public string DeviceName => _dev.FriendlyName;
    public int OutChannels => _outCh;
    public long WrittenFrames => Interlocked.Read(ref _writtenFrames);
    public float Level => Volatile.Read(ref _level);

    public MirrorRenderer(MMDevice dev)
    {
        _dev = dev;
        WaveFormat mf;
        try { mf = dev.AudioClient.MixFormat; }
        catch { mf = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2); }
        _fmt = mf.BitsPerSample == 32 ? mf : WaveFormat.CreateIeeeFloatWaveFormat(mf.SampleRate, Math.Max(1, mf.Channels));
        _outCh = Math.Max(1, _fmt.Channels);
        _cap = 1 << 19;                 // ~5.4 s of stereo @48k, plenty
        _ring = new float[_cap];
    }

    public void Start()
    {
        _ac = _dev.AudioClient;
        _ac.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.None,
            1_000_000, 0, _fmt, Guid.Empty);   // 100 ms engine buffer
        _rc = _ac.AudioRenderClient;
        _ac.Start();
        _run = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "mirror-render" };
        _thread.Start();
    }

    /// <summary>Append interleaved stereo frames (L,R,L,R...) captured from ch1/ch2.</summary>
    public void Push(float[] stereo, int frames)
    {
        lock (_lock)
        {
            int n2 = frames * 2;
            for (int i = 0; i < n2; i++)
            {
                _ring[_w] = stereo[i];
                _w = (_w + 1) % _cap;
            }
        }
    }

    private void Loop()
    {
        if (_ac == null || _rc == null) return;
        int bufFrames = _ac.BufferSize;
        var tmp = new float[4096 * _outCh];
        while (_run)
        {
            int pad = _ac.CurrentPadding;
            int space = bufFrames - pad;
            while (space > 0 && _run)
            {
                int n = Math.Min(space, 1024);
                int availStereo;
                lock (_lock) availStereo = ((_w - _r + _cap) % _cap) / 2;
                int take = Math.Min(n, availStereo);

                double sumsq = 0;
                lock (_lock)
                {
                    for (int i = 0; i < take; i++)
                    {
                        float L = _ring[_r]; _r = (_r + 1) % _cap;
                        float R = _ring[_r]; _r = (_r + 1) % _cap;
                        float mono = (L + R) * 0.5f;
                        for (int c = 0; c < _outCh; c++)
                            tmp[i * _outCh + c] = _outCh == 1 ? mono : (c == 0 ? L : (c == 1 ? R : 0f));
                        sumsq += (double)L * L + (double)R * R;
                    }
                }
                for (int i = take; i < n; i++)
                    for (int c = 0; c < _outCh; c++) tmp[i * _outCh + c] = 0f;

                IntPtr ptr = _rc.GetBuffer(n);
                Marshal.Copy(tmp, 0, ptr, n * _outCh);
                _rc.ReleaseBuffer(n, AudioClientBufferFlags.None);
                Interlocked.Add(ref _writtenFrames, n);
                if (take > 0) _level = (float)Math.Sqrt(sumsq / (take * 2.0));
                space -= n;
            }
            Thread.Sleep(5);
        }
    }

    public void Stop()
    {
        _run = false;
        try { _thread?.Join(500); } catch { }
        try { _ac?.Stop(); } catch { }
    }

    public void Dispose()
    {
        Stop();
        try { _ac?.Dispose(); } catch { }
    }
}
