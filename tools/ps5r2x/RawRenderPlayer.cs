// RawRenderPlayer — minimal WASAPI shared-mode renderer built directly on
// NAudio's AudioClient/IAudioClient, used instead of WasapiOut because WasapiOut
// rejects a WaveFormatExtensible ("Must be already floating point") even though
// its SubFormat is IEEE float. Here we Initialize() with the endpoint's exact
// MixFormat (e.g. 4ch/48k/32-bit float) and push float32 frames ourselves.
using System;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Ps5R2X;

internal sealed class RawRenderPlayer : IDisposable
{
    private readonly MMDevice _dev;
    private readonly WaveFormat _fmt;
    private readonly SignalProvider _gen;
    private readonly int _ch;
    private readonly int _framesPerWrite;
    private AudioClient? _ac;
    private AudioRenderClient? _rc;
    private Thread? _thread;
    private volatile bool _run;

    public RawRenderPlayer(MMDevice dev, WaveFormat fmt, SignalProvider gen, int framesPerWrite = 480)
    {
        _dev = dev;
        _fmt = fmt;
        _gen = gen;
        _ch = Math.Max(1, fmt.Channels);
        _framesPerWrite = Math.Max(64, framesPerWrite);
    }

    public void Start()
    {
        _ac = _dev.AudioClient;
        // shared mode, 200 ms engine buffer, periodicity 0 (required in shared mode)
        _ac.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.None,
            2_000_000, 0, _fmt, Guid.Empty);
        _rc = _ac.AudioRenderClient;
        _ac.Start();

        _run = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "raw-render" };
        _thread.Start();
    }

    private void Loop()
    {
        if (_ac == null || _rc == null) return;
        int bufFrames = _ac.BufferSize;
        var tmp = new float[_framesPerWrite * _ch];
        while (_run)
        {
            int pad = _ac.CurrentPadding;
            int avail = bufFrames - pad;
            while (avail > 0 && _run)
            {
                int n = Math.Min(avail, _framesPerWrite);
                _gen.Read(tmp, 0, n * _ch);
                IntPtr ptr = _rc.GetBuffer(n);
                Marshal.Copy(tmp, 0, ptr, n * _ch);
                _rc.ReleaseBuffer(n, AudioClientBufferFlags.None);
                avail -= n;
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
