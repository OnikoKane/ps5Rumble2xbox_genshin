// xbox4motor — Spike: drive a real Xbox One controller's 4 vibration motors
// (LeftMotor / RightMotor / LeftTrigger / RightTrigger) via Windows.Gaming.Input (WinRT).
//
// Why: XInput (XInputSetState) only exposes 2 motors (left/right grip). The two
// trigger "impulse" motors are only reachable through the Windows.Gaming.Input
// GamepadVibration struct (4 values) or lower-level GameInput/GIP. This spike
// proves which one actually moves the trigger motors on a real pad.
//
// Usage:
//   dotnet run --project tools/xbox4motor -- --probe          # enumerate + read only, NO vibration
//   dotnet run --project tools/xbox4motor                     # full cycle on ALL WGI pads: L motor -> R motor -> L trigger -> R trigger
//   dotnet run --project tools/xbox4motor -- --index 0        # drive only WGI pad #0
//   dotnet run --project tools/xbox4motor -- --all            # also drive all 4 at once for 1 hold
//   dotnet run --project tools/xbox4motor -- --hold 1500      # per-segment duration (ms)

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Windows.Gaming.Input;

namespace Xbox4Motor;

internal static class Program
{
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    // -1 means "all pads"; >=0 drives a single pad index.
    private static int _index = -1;
    private static int _holdMs = 1500;
    private static int _waitMs = 4000;   // WGI discovery warm-up
    private static bool _probe = false;
    private static bool _all = false;
    private static bool _noWake = false;

    private static int Main(string[] args)
    {
        if (!ParseArgs(args))
        {
            PrintHelp();
            return 2;
        }

        Console.OutputEncoding = System.Text.Encoding.UTF8;
        // Windows.Gaming.Input's provider has been reported to need a focused
        // window (SDL#4859). Bring our console window forward (best effort).
        try
        {
            var hwnd = GetConsoleWindow();
            if (hwnd != IntPtr.Zero) SetForegroundWindow(hwnd);
        }
        catch { }

        Console.WriteLine("=== xbox4motor (Windows.Gaming.Input 4-motor spike) ===");
        Console.WriteLine($"OS: {Environment.OSVersion}  Runtime: {Environment.Version}");

        // ---- 0) XInput side: proves the "only 2 motors" API and slot occupancy ----
        XInputProbe.PrintSlots();

        // ---- 1) Identity via RawGameController (VID/PID/name), if available ----
        try
        {
            var raws = new List<RawGameController>(RawGameController.RawGameControllers);
            Console.WriteLine($"\nRawGameController 枚举到 {raws.Count} 个：");
            for (int i = 0; i < raws.Count; i++)
            {
                var r = raws[i];
                string vidpid = "?";
                try { vidpid = $"VID_{r.HardwareVendorId:X4}&PID_{r.HardwareProductId:X4}"; } catch { }
                Console.WriteLine($"  [raw {i}] {r.DisplayName}  ({vidpid})  buttons={r.ButtonCount} switches={r.SwitchCount} axes={r.AxisCount}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n[WARN] RawGameController 枚举失败（不影响 Gamepad 路径）: {ex.GetType().Name}: {ex.Message}");
        }

        // ---- 2) Gamepad (WGI's vibration-capable view) ----
        // WGI discovery is asynchronous: the list can be empty right after process
        // start. Poll + GamepadAdded for up to _waitMs to give it time.
        List<Gamepad> pads;
        try
        {
            pads = SnapshotPads(_waitMs);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERR] Gamepad.Gamepads 枚举失败: {ex.GetType().Name}: {ex.Message}");
            return 3;
        }

        Console.WriteLine($"\nGamepad (WGI) 枚举到 {pads.Count} 个：");
        for (int i = 0; i < pads.Count; i++)
        {
            var g = pads[i];
            GamepadVibration v = default;
            try { v = g.Vibration; } catch { }
            Console.WriteLine($"  [pad {i}] Vibration 当前 = L={v.LeftMotor:F2} R={v.RightMotor:F2} LT={v.LeftTrigger:F2} RT={v.RightTrigger:F2}");
        }

        if (pads.Count == 0)
        {
            Console.WriteLine("\n[FAIL] WGI 没有枚举到任何 Gamepad。");
            Console.WriteLine("  - 确认 Xbox One 手柄已用数据线连接且被 Windows 识别（joy.cpl / 设备管理器 XboxComposite）。");
            Console.WriteLine("  - 若物理手柄被 HidHide 隐藏，需要把本程序加入 HidHide 白名单（否则 WGI 也看不到它）。");
            Console.WriteLine("  - 若仅 XInput 暴露 / WGI 为空，走次选方案（GameInput API / hidapi 0x03）。");
            return 10;
        }

        // Decide which pads to drive.
        List<Gamepad> targets = new();
        if (_index >= 0 && _index < pads.Count)
        {
            targets.Add(pads[_index]);
            Console.WriteLine($"\n只驱动 pad {_index}。");
        }
        else
        {
            if (_index >= pads.Count) Console.WriteLine($"\n[WARN] --index {_index} 越界，改为驱动全部。");
            targets.AddRange(pads);
            Console.WriteLine($"\n驱动全部 {targets.Count} 个 WGI Gamepad（WGI 索引顺序不等于 XInput slot，全部驱动可确保物理手柄被覆盖；虚拟手柄无物理马达，驱动它无副作用）。");
        }

        if (_probe)
        {
            Console.WriteLine("\n--probe：只枚举与读取，不发送震动。结束。");
            return 0;
        }

        GC.KeepAlive(pads);

        // Per SDL#4859: WGI rumble may not actuate until the pad has emitted some
        // input first. Wait for a button/axis change (up to 12 s) before driving.
        if (!_noWake)
            WaitForPadActivity(targets, 12000);

        Console.WriteLine($"\n开始分段驱动，每段约 {_holdMs} ms。请用手感受震动的部位：\n");

        // Order required: Left motor -> Right motor -> Left trigger -> Right trigger
        Hold(targets, "① 左马达  LeftMotor   (握把左 / 大马达)", 1f, 0f, 0f, 0f);
        Hold(targets, "② 右马达  RightMotor  (握把右 / 小马达)", 0f, 1f, 0f, 0f);
        Hold(targets, "③ 左扳机  LeftTrigger (左扳机脉冲马达)", 0f, 0f, 1f, 0f);
        Hold(targets, "④ 右扳机  RightTrigger(右扳机脉冲马达)", 0f, 0f, 0f, 1f);

        if (_all)
            Hold(targets, "⑤ 全部同时 L+R+LT+RT", 1f, 1f, 1f, 1f);

        StopAll(targets);
        Console.WriteLine("\n已停止震动。");
        Console.WriteLine("\n判读建议：");
        Console.WriteLine("  - ① ② 有震、③ ④ 完全无感 => WGI 的 trigger 值在该驱动/固件上被忽略（只做了 2 马达）。");
        Console.WriteLine("  - ③ ④ 也能感到扳机处震动 => WGI 4 值可直接驱动脉冲扳机，主程序应采用 WGI。");
        return 0;
    }

    /// <summary>
    /// Wait until the pad reports some input (a button or an axis beyond a small
    /// deadzone), which per SDL#4859 "frees up" the WGI rumble path. Bounded wait.
    /// </summary>
    private static void WaitForPadActivity(List<Gamepad> targets, int timeoutMs)
    {
        if (targets.Count == 0) return;
        Console.WriteLine($"\n唤醒检查：请按一下手柄任意按键，或推动摇杆/扳机（最多等 {timeoutMs / 1000} 秒）...");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            foreach (var pad in targets)
            {
                GamepadReading rd;
                try { rd = pad.GetCurrentReading(); }
                catch { continue; }
                bool active =
                    rd.Buttons != GamepadButtons.None ||
                    Math.Abs(rd.LeftThumbstickX) > 0.15 || Math.Abs(rd.LeftThumbstickY) > 0.15 ||
                    Math.Abs(rd.RightThumbstickX) > 0.15 || Math.Abs(rd.RightThumbstickY) > 0.15 ||
                    rd.LeftTrigger > 0.05 || rd.RightTrigger > 0.05;
                if (active)
                {
                    Console.WriteLine($"  检测到手柄输入，继续。");
                    return;
                }
            }
            Thread.Sleep(80);
        }
        Console.WriteLine("  [WARN] 未检测到手柄输入（超时）。仍继续尝试震动。");
    }

    /// <summary>
    /// Collect WGI Gamepads, giving the asynchronous provider a bounded warm-up
    /// window (Gamepad.Gamepads can be empty immediately after process start).
    /// </summary>
    private static List<Gamepad> SnapshotPads(int waitMs)
    {
        var list = new List<Gamepad>();
        void Add(Gamepad g)
        {
            if (g != null && !list.Contains(g)) list.Add(g);
        }

        foreach (var g in Gamepad.Gamepads) Add(g);
        if (list.Count == 0 && waitMs > 0)
        {
            EventHandler<Gamepad> onAdded = (_, g) => Add(g);
            Gamepad.GamepadAdded += onAdded;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < waitMs)
            {
                Thread.Sleep(100);
                foreach (var g in Gamepad.Gamepads) Add(g);
                if (list.Count > 0) break;
            }
            Gamepad.GamepadAdded -= onAdded;
        }
        return list;
    }

    /// <summary>Set the 4 values on every target pad and hold, re-asserting periodically.</summary>
    private static void Hold(List<Gamepad> targets, string label, float l, float r, float lt, float rt)
    {
        Console.WriteLine($"--- {label}  =>  Vibration{{ L={l:F2} R={r:F2} LT={lt:F2} RT={rt:F2} }}");
        var vib = new GamepadVibration
        {
            LeftMotor = l,
            RightMotor = r,
            LeftTrigger = lt,
            RightTrigger = rt,
        };

        // Re-send every 150 ms: the driver clears vibration on focus changes and WGI
        // has no explicit "stop" (it conveys off by ceasing to re-send).
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < _holdMs)
        {
            foreach (var pad in targets)
            {
                try { pad.Vibration = vib; }
                catch (Exception ex)
                {
                    Console.WriteLine($"    [WARN] set Vibration threw: {ex.GetType().Name}: {ex.Message}");
                }
            }
            Thread.Sleep(150);
        }

        if (targets.Count > 0)
        {
            try
            {
                var back = targets[0].Vibration;
                Console.WriteLine($"    回读(pad0): L={back.LeftMotor:F2} R={back.RightMotor:F2} LT={back.LeftTrigger:F2} RT={back.RightTrigger:F2}");
            }
            catch { }
        }
    }

    private static void StopAll(List<Gamepad> targets)
    {
        var off = new GamepadVibration { LeftMotor = 0, RightMotor = 0, LeftTrigger = 0, RightTrigger = 0 };
        foreach (var pad in targets)
        {
            try { pad.Vibration = off; } catch { }
        }
    }

    private static bool ParseArgs(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--probe": _probe = true; break;
                case "--all": _all = true; break;
                case "--no-wake": _noWake = true; break;
                case "--index":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out var idx)) _index = idx;
                    else return false;
                    break;
                case "--hold":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out var ms)) _holdMs = Math.Max(200, ms);
                    else return false;
                    break;
                case "--wait":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out var w)) _waitMs = Math.Max(0, w);
                    else return false;
                    break;
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

    private static void PrintHelp()
    {
        Console.WriteLine("xbox4motor — Windows.Gaming.Input 4 马达震动 spike");
        Console.WriteLine("  --probe          只枚举/读取，不震动");
        Console.WriteLine("  --all            额外做一段 4 马达同时");
        Console.WriteLine("  --index <n>      只驱动第 n 个 Gamepad（默认驱动全部）");
        Console.WriteLine("  --hold <ms>      每段时长（默认 1500）");
        Console.WriteLine("  --wait <ms>      WGI 设备发现预热时长（默认 4000）");
        Console.WriteLine("  --no-wake        跳过“等待手柄输入唤醒”检查");
    }
}
