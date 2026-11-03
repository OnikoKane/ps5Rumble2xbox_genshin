// ps5r2x — 核心引擎 spike：音频触觉(虚拟 DualSense ch3/4 或立体声低频) → 真实 Xbox 4 马达
//
// 模式：
//   live（默认）  ：WASAPI loopback 采集所选端点，实时把 ch3/4（或立体声低频）算成 L/R 包络，
//                   映射为 Xbox LeftMotor/RightMotor（可 --triggers 同时驱动 LT/RT），每秒打印。
//   --demo       ：向所选端点播放 40–120Hz 扫频正弦+脉冲，同时 loopback 采集并驱动 Xbox，
//                   做“声音→震动”闭环自测（用户靠手感判定）。
//
// 用法：
//   dotnet run -c Release -- --list
//   dotnet run -c Release -- --demo                       # 默认扬声器闭环自测（10s）
//   dotnet run -c Release -- --endpoint "DualSense" --demo
//   dotnet run -c Release -- --endpoint "Wireless Controller"    # 指向虚拟 DS5 音频端点（接入原神触觉）
//   dotnet run -c Release -- --gain 3 --sensitivity 70 --smooth 0.6 --lowpass 120
//   dotnet run -c Release -- --triggers                  # 同时驱动扳机脉冲马达

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Ps5R2X;

internal static class Program
{
    [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);

    // ---- options ----
    private static string? _endpoint;
    private static double _gain = 2.0;
    private static double _sensitivity = 60.0;   // 0..100
    private static double _smooth = 0.55;        // 0..0.95
    private static double _lowpass = 120.0;      // Hz (legacy alias for band-hi)
    private static double _bandLo = 30.0;        // Hz, high-pass edge of main band
    private static double _bandHi = 90.0;        // Hz, low-pass edge of main band
    private static bool _bandHiExplicit;
    private static double _gateDb = -30.0;       // noise gate (dB)
    private static double _attackMs = 10.0;      // transient attack (ms)
    private static double _releaseMs = 100.0;    // transient release (ms)
    private static string _signal = "sweep";     // demo/inject signal: sweep|pulse|sine|high
    private static bool _demo = false;
    private static double _duration = 10.0;
    private static bool _triggers = false;
    private static int _index = -1;              // WGI pad index; -1 = all
    private static int _waitMs = 4000;
    private static bool _list = false;
    private static string? _inject;   // e.g. "ch3,ch4" — play test tone only, no capture
    private static string? _mirror;   // mirror ch1/2 to this real speaker (FriendlyName substring)
    private static bool _mirrorAuto;  // auto-pick a non-DualSense real speaker
    private static bool _run;         // production combo mode
    private static bool _selftestMirror; // inject ch1/2 + mirror, one process
    private static string _source = "haptic"; // haptic|main|auto
    private static bool _sourceExplicit;
    private static double _seconds = 0;   // live (non-demo) auto-stop

    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        try { var h = GetConsoleWindow(); if (h != IntPtr.Zero) SetForegroundWindow(h); } catch { }

        if (!Parse(args)) { PrintHelp(); return 2; }

        Console.WriteLine("=== ps5r2x · 音频触觉 → Xbox 4 马达 核心引擎 ===");
        Console.WriteLine($"OS: {Environment.OSVersion}  Runtime: {Environment.Version}");

        // ---- convenience modes: --run / --selftest-mirror ----
        if (_run)
        {
            if (string.IsNullOrWhiteSpace(_endpoint)) _endpoint = "DualSense";
            if (string.IsNullOrWhiteSpace(_mirror)) _mirrorAuto = true;
            if (!_sourceExplicit) _source = "auto";   // 生产默认 auto：有 HD 触觉用之，否则主音频兜底
        }
        if (_selftestMirror)
        {
            if (string.IsNullOrWhiteSpace(_endpoint)) _endpoint = "DualSense";
            _mirrorAuto = true;
            _demo = true;                 // inject a tone into the capture endpoint
            if (_duration <= 10) _duration = 8;
        }

        var en = new MMDeviceEnumerator();
        List<MMDevice> renders = new();
        foreach (MMDevice d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)) renders.Add(d);

        string defId = "";
        try { defId = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID; } catch { }

        Console.WriteLine($"\n可用音频渲染端点（WASAPI loopback 采集目标）：");
        for (int i = 0; i < renders.Count; i++)
        {
            var d = renders[i];
            string chs = "?", rate = "?";
            try { var mf = d.AudioClient.MixFormat; chs = mf.Channels.ToString(); rate = mf.SampleRate.ToString(); } catch { }
            string mark = (d.ID == defId) ? " *默认*" : "";
            Console.WriteLine($"  [{i}] {d.FriendlyName}  ch={chs} rate={rate}{mark}");
        }

        if (_list) return 0;

        // ---- select endpoint ----
        MMDevice? dev = null;
        if (!string.IsNullOrWhiteSpace(_endpoint))
        {
            dev = renders.FirstOrDefault(d => d.FriendlyName.Contains(_endpoint!, StringComparison.OrdinalIgnoreCase));
            if (dev == null)
            {
                Console.WriteLine($"\n[ERR] 未找到 FriendlyName 含 \"{_endpoint}\" 的渲染端点。用 --list 查看可用端点。");
                return 3;
            }
        }
        else
        {
            try { dev = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia); }
            catch (Exception ex) { Console.WriteLine($"[ERR] 取默认端点失败: {ex.Message}"); return 3; }
        }

        WaveFormat? mixFmt = null;
        try { mixFmt = dev.AudioClient.MixFormat; } catch { }
        int mixCh = mixFmt?.Channels ?? 2, mixRate = mixFmt?.SampleRate ?? 48000;

        Console.WriteLine($"\n选中端点: {dev.FriendlyName}  (mix ch={mixCh} rate={mixRate})");
        if (mixFmt != null)
        {
            Console.WriteLine($"  MixFormat: encoding={mixFmt.Encoding} bits={mixFmt.BitsPerSample} ch={mixFmt.Channels} rate={mixFmt.SampleRate} averageBytesPerSecond={mixFmt.AverageBytesPerSecond}");
        }
        Console.WriteLine($"参数: gain={_gain} sensitivity={_sensitivity} smooth={_smooth} lowpass={_lowpass}Hz triggers={_triggers} source={_source}");

        // ---- inject-only mode: play test tone to the endpoint, no capture ----
        if (_inject != null)
        {
            RunInject(dev, mixFmt, mixCh, mixRate, _inject, _duration);
            return 0;
        }

        // ---- WGI 4-motor output ----
        var wgi = WgiOutput.Create(_waitMs, _index);
        Console.WriteLine($"WGI Gamepad: {wgi.Count} 个{(wgi.Count == 0 ? "（[WARN] 无手柄，仅做音频分析；确认 Xbox 已连接且未被 HidHide 隐藏）" : "")}");

        // ---- analyzer ----
        using var analyzer = new AudioAnalyzer(dev, _bandLo, _bandHi);
        Console.WriteLine($"采集模式: {analyzer.Mode}  采集格式: {analyzer.SampleRate}Hz");

        // ---- optional mirror: ch1/2 (main audio) -> a real speaker ----
        MirrorRenderer? mirror = null;
        if (_mirrorAuto || !string.IsNullOrWhiteSpace(_mirror))
        {
            MMDevice? mdev = ResolveMirror(renders, dev, _mirror, _mirrorAuto);
            if (mdev == null)
            {
                Console.WriteLine("[WARN] 未找到可镜像的真实扬声器端点，取消镜像。");
            }
            else
            {
                try
                {
                    mirror = new MirrorRenderer(mdev);
                    mirror.Start();
                    Console.WriteLine($"镜像: 采集 ch1/2 -> \"{mdev.FriendlyName}\" (outCh={mirror.OutChannels})");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WARN] 镜像初始化失败（{ex.GetType().Name}: {ex.Message}）");
                    mirror?.Dispose(); mirror = null;
                }
            }
        }
        if (mirror != null) analyzer.MainAudioSink = (buf, frames) => mirror.Push(buf, frames);

        analyzer.Start();

        // ---- demo playback (play + capture in one process) ----
        RawRenderPlayer? player = null;
        if (_demo)
        {
            WaveFormat fmt = BuildRenderFormat(mixFmt, mixRate, mixCh);
            int[]? active = _selftestMirror ? new[] { 0, 1 } : (mixCh >= 4 ? new[] { 2, 3 } : null);
            Console.WriteLine($"--demo: 渲染格式={fmt.Encoding}/{fmt.BitsPerSample}bit/{fmt.Channels}ch/{fmt.SampleRate}Hz; 活动通道={(active == null ? "全部" : string.Join(",", active.Select(i => "ch" + (i + 1))))}");
            try
            {
                var gen = new SignalProvider(fmt, active, 40, 120, 4.0, 0.5, _signal);
                player = new RawRenderPlayer(dev, fmt, gen);
                player.Start();
                Console.WriteLine($"--demo: 已向 \"{dev.FriendlyName}\" 播放 40–120Hz 扫频+脉冲（{_duration:F0}s）");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WARN] demo 播放初始化失败（{ex.GetType().Name}: {ex.Message}），改为纯采集模式（可用 --inject 单独注入）。");
                player?.Dispose(); player = null;
            }
        }

        // ---- run loop ----
        bool running = true;
        Console.CancelKeyPress += (s, e) => { e.Cancel = true; running = false; };

        var sw = Stopwatch.StartNew();
        double lastPrint = -1;
        double lastRefresh = 0;
        int shownPads = wgi.Count;
        float smL = 0, smR = 0;
        var dB = 1e-9;
        int nch = Math.Max(2, analyzer.Channels);
        float[] peak = new float[nch];
        float[] mainPeak = new float[2];
        string activeSrc = _source == "main" ? "main" : "haptic";
        double lastAutoCheck = -1;
        // main-source transient DSP state
        double slowDbL = -90, slowDbR = -90;
        float envMainL = 0, envMainR = 0;
        double lastTick = 0;
        float motorPeakL = 0, motorPeakR = 0;
        long onTicksL = 0, onTicksR = 0, totalTicks = 0;
        bool gateOpenL = false, gateOpenR = false;
        bool firstTick = true;

        Console.WriteLine("\n开始实时转换（Ctrl+C 结束）：");
        while (running)
        {
            double tt = sw.Elapsed.TotalSeconds;
            // Resilient discovery: re-scan for a gamepad that appears after start.
            if (tt - lastRefresh >= 1.0)
            {
                lastRefresh = tt;
                int n = wgi.Refresh();
                if (n != shownPads)
                {
                    shownPads = n;
                    Console.WriteLine($"  [WGI] 手柄数量变化 -> {n}");
                }
            }

            var cr = analyzer.GetChannelRms();
            var (mainL, mainR) = analyzer.GetMainRms();
            float hapL = cr.Length > 2 ? cr[2] : 0f;
            float hapR = cr.Length > 3 ? cr[3] : 0f;
            double hapDb = 20 * Math.Log10(Math.Max(hapL, hapR) + dB);

            // 源选择：haptic=仅 ch3/4；main=仅 ch1/2 低频；auto=每 0.5s 判断 ch3/4 有无能量
            if (_source == "main") activeSrc = "main";
            else if (_source == "haptic") activeSrc = "haptic";
            else if (tt - lastAutoCheck >= 0.5)
            {
                lastAutoCheck = tt;
                string want = hapDb > -45.0 ? "haptic" : "main";
                if (want != activeSrc)
                {
                    activeSrc = want;
                    Console.WriteLine($"  [source] auto -> {activeSrc} (ch3/4 能量 {hapDb:F1}dB, 门限 -45dB)");
                }
            }

            // ---- main 源 DSP：带通(已在 analyzer) + 噪声门 + 瞬态(超过慢基线才触发) + attack/release 包络 ----
            double dt = firstTick ? 0.02 : Math.Max(0.001, tt - lastTick);
            lastTick = tt;
            double mainLdb = 20 * Math.Log10(mainL + dB);
            double mainRdb = 20 * Math.Log10(mainR + dB);
            if (firstTick) { slowDbL = mainLdb; slowDbR = mainRdb; firstTick = false; }
            // 慢基线：~0.8s 时间常数跟踪持续能量（持续音乐→基线追平→不再触发）
            double baseUp = 1 - Math.Exp(-dt / 0.8);
            slowDbL += (mainLdb > slowDbL ? baseUp : baseUp) * (mainLdb - slowDbL); // toward
            slowDbR += (mainRdb > slowDbR ? baseUp : baseUp) * (mainRdb - slowDbR);
            double onsetLdb = mainLdb - slowDbL;
            double onsetRdb = mainRdb - slowDbR;
            gateOpenL = mainLdb >= _gateDb;
            gateOpenR = mainRdb >= _gateDb;
            float mainTargetL = 0f, mainTargetR = 0f;
            if (gateOpenL)
            {
                double overGate = Math.Clamp((mainLdb - _gateDb) / 20.0, 0, 1);
                double onsetNorm = Math.Clamp((onsetLdb - 3.0) / 12.0, 0, 1);
                mainTargetL = (float)Math.Clamp(onsetNorm * overGate * _gain, 0, 1);
            }
            if (gateOpenR)
            {
                double overGate = Math.Clamp((mainRdb - _gateDb) / 20.0, 0, 1);
                double onsetNorm = Math.Clamp((onsetRdb - 3.0) / 12.0, 0, 1);
                mainTargetR = (float)Math.Clamp(onsetNorm * overGate * _gain, 0, 1);
            }
            double atk = 1 - Math.Exp(-dt / Math.Max(0.001, _attackMs / 1000.0));
            double rel = 1 - Math.Exp(-dt / Math.Max(0.001, _releaseMs / 1000.0));
            envMainL = (float)(mainTargetL > envMainL ? envMainL + atk * (mainTargetL - envMainL) : envMainL + rel * (mainTargetL - envMainL));
            envMainR = (float)(mainTargetR > envMainR ? envMainR + atk * (mainTargetR - envMainR) : envMainR + rel * (mainTargetR - envMainR));

            for (int c = 0; c < cr.Length && c < peak.Length; c++)
                if (cr[c] > peak[c]) peak[c] = cr[c];
            if (mainL > mainPeak[0]) mainPeak[0] = mainL;
            if (mainR > mainPeak[1]) mainPeak[1] = mainR;

            float hapticTargetL = MapToMotor(hapL, _gain, _sensitivity);
            float hapticTargetR = MapToMotor(hapR, _gain, _sensitivity);
            smL = (float)(_smooth * smL + (1 - _smooth) * hapticTargetL);
            smR = (float)(_smooth * smR + (1 - _smooth) * hapticTargetR);

            float mL = activeSrc == "haptic" ? smL : envMainL;
            float mR = activeSrc == "haptic" ? smR : envMainR;
            float lt = _triggers ? mL : 0f;
            float rt = _triggers ? mR : 0f;
            wgi.Set(mL, mR, lt, rt);

            totalTicks++;
            if (mL > 0.1f) onTicksL++;
            if (mR > 0.1f) onTicksR++;
            if (mL > motorPeakL) motorPeakL = mL;
            if (mR > motorPeakR) motorPeakR = mR;

            double tt2 = sw.Elapsed.TotalSeconds;
            if (tt2 - lastPrint >= 1.0)
            {
                lastPrint = tt2;
                string chStr = "";
                if (analyzer.Channels >= 4 && cr.Length >= 4)
                    chStr = $" | ch1={20 * Math.Log10(cr[0] + dB),6:F1} ch2={20 * Math.Log10(cr[1] + dB),6:F1} ch3={20 * Math.Log10(cr[2] + dB),6:F1} ch4={20 * Math.Log10(cr[3] + dB),6:F1}";
                string mirrorStr = mirror != null
                    ? $" | mirror[{mirror.DeviceName}] lvl={20 * Math.Log10(mirror.Level + dB),6:F1}dB frames={mirror.WrittenFrames}"
                    : "";
                Console.WriteLine(
                    $"[t={tt,5:F1}s] src={activeSrc} mainL={mainLdb,7:F1}dB mainR={mainRdb,7:F1}dB hap={hapDb,6:F1}dB gate={((gateOpenL) ? "1" : "0")}/{((gateOpenR) ? "1" : "0")} | motor L={mL:F3} R={mR:F3}" +
                    (_triggers ? $" LT={lt:F3} RT={rt:F3}" : "") +
                    chStr + mirrorStr +
                    $" | blocks={analyzer.Blocks}");
            }

            if ((_demo && tt >= _duration) || (!_demo && _seconds > 0 && tt >= _seconds)) break;
            Thread.Sleep(20);
        }

        // ---- stop ----
        wgi.Stop();
        try { player?.Stop(); } catch { }
        player?.Dispose();
        mirror?.Dispose();
        if (peak.Length > 0)
            Console.WriteLine("峰值(逐声道, dB): " + string.Join("  ",
                Enumerable.Range(0, peak.Length).Select(i => $"ch{i + 1}={20 * Math.Log10(peak[i] + 1e-9):F1}")));
        Console.WriteLine($"主音频低频峰值: mainL={20 * Math.Log10(mainPeak[0] + 1e-9):F1}dB mainR={20 * Math.Log10(mainPeak[1] + 1e-9):F1}dB  (source={_source})");
        double dutyL = totalTicks > 0 ? 100.0 * onTicksL / totalTicks : 0;
        double dutyR = totalTicks > 0 ? 100.0 * onTicksR / totalTicks : 0;
        Console.WriteLine($"马达峰值: L={motorPeakL:F3} R={motorPeakR:F3} | 占空比(>0.1): L={dutyL:F1}% R={dutyR:F1}% (ticks={totalTicks}, gate -{Math.Abs(_gateDb):F0}dB, attack={_attackMs}ms release={_releaseMs}ms band {_bandLo}-{_bandHi}Hz)");
        Console.WriteLine("\n已停止。马达归零。");
        return 0;
    }

    /// <summary>
    /// Build a render format WASAPI shared mode will accept: use the endpoint's
    /// exact MixFormat when it is 32-bit float (the usual WASAPI mix), else fall
    /// back to a 32-bit float format at the same rate/channels.
    /// </summary>
    private static WaveFormat BuildRenderFormat(WaveFormat? mix, int rate, int ch)
    {
        if (mix != null && mix.BitsPerSample == 32) return mix;
        return WaveFormat.CreateIeeeFloatWaveFormat(rate, Math.Max(2, ch));
    }

    /// <summary>
    /// Pick a real speaker endpoint to mirror ch1/2 to: exclude the capture
    /// endpoint; if `substr` given, match it; else prefer Realtek -> Steam ->
    /// first non-capture endpoint with >=2 channels.
    /// </summary>
    private static MMDevice? ResolveMirror(List<MMDevice> renders, MMDevice capture, string? substr, bool auto)
    {
        var candidates = renders.Where(d => d.ID != capture.ID).ToList();
        if (candidates.Count == 0) return null;

        if (!string.IsNullOrWhiteSpace(substr))
        {
            var hit = candidates.FirstOrDefault(d => d.FriendlyName.Contains(substr!, StringComparison.OrdinalIgnoreCase));
            if (hit == null)
            {
                Console.WriteLine($"[WARN] --mirror \"{substr}\" 未匹配到非采集端点（用 --list 查看）。");
                return null;
            }
            return hit;
        }
        if (!auto) return null;

        // Auto: prefer a real speaker with >=2 channels.
        var real = candidates.Where(d =>
        {
            try { return d.AudioClient.MixFormat.Channels >= 2; }
            catch { return false; }
        }).ToList();
        var pool = real.Count > 0 ? real : candidates;

        return pool.FirstOrDefault(d => d.FriendlyName.Contains("Realtek", StringComparison.OrdinalIgnoreCase))
            ?? pool.FirstOrDefault(d => d.FriendlyName.Contains("Steam", StringComparison.OrdinalIgnoreCase))
            ?? pool[0];
    }

    /// <summary>--inject: play the test sweep to the endpoint (only the named channels) and exit.</summary>
    private static void RunInject(MMDevice dev, WaveFormat? mix, int mixCh, int mixRate, string spec, double duration)
    {
        var active = ParseChannels(spec, mixCh);
        var fmt = BuildRenderFormat(mix, mixRate, mixCh);
        Console.WriteLine($"--inject: 端点=\"{dev.FriendlyName}\" 渲染格式={fmt.Encoding}/{fmt.BitsPerSample}bit/{fmt.Channels}ch/{fmt.SampleRate}Hz " +
                          $"活动通道={string.Join(",", active.Select(i => "ch" + (i + 1)))} 时长={duration:F0}s");
        try
        {
            var gen = new SignalProvider(fmt, active, 40, 120, 4.0, 0.5, _signal);
            using var player = new RawRenderPlayer(dev, fmt, gen);
            player.Start();
            Console.WriteLine("  播放中（Ctrl+C 结束）...");
            bool run = true;
            Console.CancelKeyPress += (s, e) => { e.Cancel = true; run = false; };
            var sw = Stopwatch.StartNew();
            while (run && sw.Elapsed.TotalSeconds < duration) Thread.Sleep(200);
            player.Stop();
            Console.WriteLine("  注入结束。");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERR] --inject 播放初始化失败（{ex.GetType().Name}: {ex.Message}）");
        }
    }

    /// <summary>Parse "ch3,ch4" / "3,4" into 0-based channel indices (clamped to `ch`).</summary>
    private static int[] ParseChannels(string spec, int ch)
    {
        var list = new List<int>();
        foreach (var part in spec.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string p = part.Trim().ToLowerInvariant();
            if (p.StartsWith("ch")) p = p[2..];
            if (int.TryParse(p, out var n))
            {
                int idx = n - 1;
                if (idx >= 0 && idx < ch) list.Add(idx);
            }
        }
        if (list.Count == 0) list.Add(Math.Min(2, ch - 1));
        return list.ToArray();
    }

    /// <summary>RMS(0..1) -> motor(0..1) via dB window + gain. sensitivity lowers the gate.</summary>
    private static float MapToMotor(double rms, double gain, double sens)
    {
        double db = 20 * Math.Log10(rms + 1e-9);
        double gate = -30.0 - 0.4 * sens;   // sens 0..100 -> -30..-70 dB
        double top = -6.0;
        double n = (db - gate) / (top - gate);
        n = Math.Clamp(n, 0.0, 1.0);
        return (float)Math.Clamp(n * gain, 0.0, 1.0);
    }

    private static bool Parse(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i].ToLowerInvariant();
            switch (a)
            {
                case "--endpoint": _endpoint = Next(args, ref i); break;
                case "--gain": _gain = ParseD(Next(args, ref i), _gain); break;
                case "--sensitivity": _sensitivity = ParseD(Next(args, ref i), _sensitivity); break;
                case "--smooth": _smooth = Math.Clamp(ParseD(Next(args, ref i), _smooth), 0, 0.95); break;
                case "--lowpass": _lowpass = ParseD(Next(args, ref i), _lowpass); if (!_bandHiExplicit) _bandHi = _lowpass; break;
                case "--band-lo": _bandLo = ParseD(Next(args, ref i), _bandLo); break;
                case "--band-hi": _bandHi = ParseD(Next(args, ref i), _bandHi); _bandHiExplicit = true; break;
                case "--gate": _gateDb = ParseD(Next(args, ref i), _gateDb); break;
                case "--attack": _attackMs = Math.Max(1, ParseD(Next(args, ref i), _attackMs)); break;
                case "--release": _releaseMs = Math.Max(1, ParseD(Next(args, ref i), _releaseMs)); break;
                case "--signal": _signal = Next(args, ref i).ToLowerInvariant(); break;
                case "--demo": _demo = true; break;
                case "--duration": _duration = Math.Max(1, ParseD(Next(args, ref i), _duration)); break;
                case "--triggers": _triggers = true; break;
                case "--index": _index = (int)ParseD(Next(args, ref i), _index); break;
                case "--wait": _waitMs = (int)Math.Max(0, ParseD(Next(args, ref i), _waitMs)); break;
                case "--list": _list = true; break;
                case "--inject": _inject = Next(args, ref i); break;
                case "--mirror": _mirror = Next(args, ref i); break;
                case "--run": _run = true; break;
                case "--selftest-mirror": _selftestMirror = true; break;
                case "--source":
                    {
                        string v = Next(args, ref i).ToLowerInvariant();
                        _source = (v == "main" || v == "auto" || v == "haptic") ? v : "haptic";
                        _sourceExplicit = true;
                    }
                    break;
                case "--seconds": _seconds = Math.Max(0, ParseD(Next(args, ref i), _seconds)); break;
                case "-h":
                case "--help":
                    return false;
                default:
                    Console.WriteLine($"未知参数: {args[i]}");
                    return false;
            }
        }
        return true;
    }

    private static string Next(string[] args, ref int i)
    {
        if (i + 1 >= args.Length) return "";
        return args[++i];
    }

    private static double ParseD(string s, double def) => double.TryParse(s, out var v) ? v : def;

    private static void PrintHelp()
    {
        Console.WriteLine("ps5r2x — 音频触觉 -> Xbox 4 马达");
        Console.WriteLine("  --list                列出音频渲染端点");
        Console.WriteLine("  --endpoint <子串>     选择端点（默认系统默认发声设备）；如 \"DualSense\"/\"Wireless Controller\"");
        Console.WriteLine("  --demo                播放 40–120Hz 扫频+脉冲并 loopback 闭环自测");
        Console.WriteLine("  --inject <ch3,ch4>    仅向端点注入测试音（指定通道），供另一进程采集验证");
        Console.WriteLine("  --mirror <子串>       采集期间把 ch1/2 主音频实时镜像到该真实扬声器（如 \"Realtek\"）");
        Console.WriteLine("  --run                 生产模式：默认 --endpoint DualSense + 自动 --mirror 真实扬声器 + 马达输出");
        Console.WriteLine("  --selftest-mirror     自测：向 DualSense ch1/2 注入测试音并镜像到真实扬声器（单进程）");
        Console.WriteLine("  --source <haptic|main|auto>  震动来源：ch3/4 触觉 / ch1/2 低频 / 自动(默认 --run=auto)");
        Console.WriteLine("  --seconds <秒>        live 模式自动停止时长（便于测试；默认 0=直到 Ctrl+C）");
        Console.WriteLine("  --duration <秒>       demo/inject 时长（默认 10）");
        Console.WriteLine("  --gain <x>            输出增益（默认 2.0）");
        Console.WriteLine("  --sensitivity <0-100> 灵敏度/噪声门（默认 60）");
        Console.WriteLine("  --smooth <0-0.95>     平滑（越大越平滑，默认 0.55）");
        Console.WriteLine("  --lowpass <Hz>        立体声低频提取截止（默认 120）");
        Console.WriteLine("  --triggers            同时把 L/R 映射到扳机脉冲马达 LT/RT");
        Console.WriteLine("  --index <n>           WGI pad 序号（默认全部）");
        Console.WriteLine("  --wait <ms>           WGI 发现预热（默认 4000）");
    }
}
