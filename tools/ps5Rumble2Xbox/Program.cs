// ps5Rumble2Xbox 鈥?WinForms 璋冭瘯 GUI + 涓€閿惎鍔ㄥ櫒
// 鍏ュ彛 + 璺緞瑙ｆ瀽 + WGI 璁惧瑙傚療鍣ㄣ€?using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using Windows.Gaming.Input;

namespace Ps5Rumble2Xbox;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        WgiWatcher.Start();
        Application.Run(new MainForm());
    }
}

/// <summary>瑙ｆ瀽宸ヤ綔鍖烘牴鐩綍涓庡悇缁勪欢璺緞锛堝彲鐢ㄧ幆澧冨彉閲?PS5R2X_ROOT 瑕嗙洊锛夈€?/summary>
internal static class AppPaths
{
    public static string Root { get; } = FindRoot();

    private static string FindRoot()
    {
        var env = Environment.GetEnvironmentVariable("PS5R2X_ROOT");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env)) return env!;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir != null; i++)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "tools", "ps5r2x")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return AppContext.BaseDirectory; // fallback: 运行目录（可用环境变量 PS5R2X_ROOT 指定根目录）
    }

    private const string R2XBin = @"tools\ps5r2x\bin\Release\net8.0-windows10.0.19041.0";
    public static string Ps5r2xDir => Path.Combine(Root, R2XBin);
    public static string Ps5r2xExe => Path.Combine(Ps5r2xDir, "ps5r2x.exe");
    public static string Ps5r2xDll => Path.Combine(Ps5r2xDir, "ps5r2x.dll");
    public static string FlexInputExe => Path.Combine(Root, @"_ref\FlexInput-bin\flexinput.exe");
    public static string FlexInputDir => Path.Combine(Root, @"_ref\FlexInput-bin");
}

/// <summary>
/// Windows.Gaming.Input 璁惧瑙傚療鍣細WGI 鍙戠幇鏄紓姝ョ殑锛岃繖閲屾寔缁妸鍑虹幇鍦?/// Gamepad.Gamepads / GamepadAdded 閲岀殑鎵嬫焺鏀堕泦璧锋潵锛屼緵 GUI 鐘舵€佹爮鏌ヨ銆?/// </summary>
internal static class WgiWatcher
{
    private static readonly object Gate = new();
    private static readonly List<Gamepad> Pads = new();
    private static bool _started;

    public static void Start()
    {
        lock (Gate) { if (_started) return; _started = true; }
        try
        {
            Gamepad.GamepadAdded += (_, g) => { lock (Gate) if (!Pads.Contains(g)) Pads.Add(g); };
            foreach (var g in Gamepad.Gamepads) lock (Gate) if (!Pads.Contains(g)) Pads.Add(g);
            // 棰勭儹锛歐GI 鍒楄〃鍒氬惎鍔ㄥ父涓虹┖锛屾寔缁疆璇㈣嫢骞茬銆?            _ = Task.Run(async () =>
            {
                for (int i = 0; i < 20; i++)
                {
                    await Task.Delay(400);
                    try { foreach (var g in Gamepad.Gamepads) lock (Gate) if (!Pads.Contains(g)) Pads.Add(g); } catch { }
                }
            });
        }
        catch { }
    }

    public static int Count() { lock (Gate) return Pads.Count; }
    public static string Describe() { lock (Gate) return Pads.Count == 0 ? "鏈娴嬪埌 Xbox" : $"{Pads.Count} 涓?Xbox"; }
}
