// ps5Rumble2Xbox — 主窗口：状态检测 / 引擎启停（ps5r2x）/ 参数 / 端点 / FlexInput 启动器
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Management;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Ps5Rumble2Xbox;

internal sealed class MainForm : Form
{
    // ---- status ----
    private readonly Panel[] _dots = new Panel[4];
    private readonly Label[] _stat = new Label[4];

    // ---- engine ----
    private Button _btnStart = null!, _btnStop = null!, _btnRefresh = null!;
    private RadioButton _rbRun = null!, _rbDemo = null!;
    private TextBox _log = null!;
    private readonly ProgressBar[] _bars = new ProgressBar[6];
    private Process? _proc;

    // ---- params ----
    private TrackBar _tbGain = null!, _tbSmooth = null!, _tbSens = null!;
    private Label _lblGain = null!, _lblSmooth = null!, _lblSens = null!;
    private CheckBox _chkTriggers = null!;
    private ComboBox _cmbCapture = null!, _cmbMirror = null!;
    private Label _lblPaths = null!;

    private readonly System.Windows.Forms.Timer _statusTimer = new() { Interval = 2000 };
    private readonly System.Windows.Forms.Timer _restartTimer = new() { Interval = 600 };
    private bool _uiReady;
    private bool _restarting;

    private static readonly Regex MotorRe = new(@"motor\s+L=([0-9.]+)\s+R=([0-9.]+)", RegexOptions.Compiled);
    private static readonly Regex TrigRe = new(@"LT=([0-9.]+)\s+RT=([0-9.]+)", RegexOptions.Compiled);
    private static readonly Regex ChRe = new(@"ch3=\s*(-?[0-9.]+)\s+ch4=\s*(-?[0-9.]+)", RegexOptions.Compiled);

    public MainForm()
    {
        Text = "ps5Rumble2Xbox — 调试 GUI / 一键启动器";
        ClientSize = new Size(1120, 780);
        MinimumSize = new Size(940, 680);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 9F);

        BuildUi();
        _uiReady = true;

        _statusTimer.Tick += (_, _) => RefreshStatuses();
        _restartTimer.Tick += (_, _) => { _restartTimer.Stop(); RestartIfRunning(); };
        _statusTimer.Start();

        Load += (_, _) => { RefreshStatuses(); LoadEndpoints(); AddLog("[GUI] 就绪。ps5r2x=" + AppPaths.Ps5r2xExe); };
        FormClosing += (_, _) => { try { StopEngine(); } catch { } };
    }

    // ───────────────────────────── UI 构建 ─────────────────────────────
    private void BuildUi()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(8) };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 128));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 220));

        root.Controls.Add(BuildStatus(), 0, 0);

        var mid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        mid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        mid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 380));
        mid.Controls.Add(BuildEngine(), 0, 0);
        mid.Controls.Add(BuildParams(), 1, 0);
        root.Controls.Add(mid, 0, 1);

        root.Controls.Add(BuildLog(), 0, 2);
        Controls.Add(root);
    }

    private Control BuildStatus()
    {
        var gb = new GroupBox { Text = "状态（自动刷新）", Dock = DockStyle.Fill, Padding = new Padding(8) };
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4 };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 30));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        string[] names = { "flexinput.exe 运行", "虚拟 DualSense (VID_054C&0CE6)", "DualSense 4ch 音频端点", "Xbox 手柄 (Windows.Gaming.Input)" };
        for (int i = 0; i < 4; i++)
        {
            var dot = new Panel { Width = 14, Height = 14, BackColor = Color.Gray, Margin = new Padding(6, 4, 0, 0), Anchor = AnchorStyles.Left | AnchorStyles.Top };
            var lb = new Label { Text = names[i] + "：…", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
            _dots[i] = dot; _stat[i] = lb;
            t.Controls.Add(dot, 0, i);
            t.Controls.Add(lb, 1, i);
        }
        gb.Controls.Add(t);
        return gb;
    }

    private Control BuildEngine()
    {
        var gb = new GroupBox { Text = "引擎（ps5r2x 实时转换）", Dock = DockStyle.Fill, Padding = new Padding(8) };
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        t.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoSize = false };
        _btnStart = new Button { Text = "▶ 开始", Width = 84, Height = 30 };
        _btnStop = new Button { Text = "■ 停止", Width = 84, Height = 30, Enabled = false };
        _rbRun = new RadioButton { Text = "生产 --run", AutoSize = true, Checked = true, Padding = new Padding(8, 6, 0, 0) };
        _rbDemo = new RadioButton { Text = "自测 --demo", AutoSize = true, Padding = new Padding(6, 6, 0, 0) };
        _btnRefresh = new Button { Text = "刷新", Width = 70, Height = 30 };
        _btnStart.Click += (_, _) => StartEngine();
        _btnStop.Click += (_, _) => StopEngine();
        _btnRefresh.Click += (_, _) => { RefreshStatuses(); LoadEndpoints(); };
        bar.Controls.AddRange(new Control[] { _btnStart, _btnStop, _rbRun, _rbDemo, _btnRefresh });
        t.Controls.Add(bar, 0, 0);

        var lv = new GroupBox { Text = "实时电平（4 马达 + 2 触觉声道）", Dock = DockStyle.Fill, Padding = new Padding(6) };
        var lt = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 6 };
        lt.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
        lt.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        string[] lname = { "左马达 L", "右马达 R", "左扳机 LT", "右扳机 RT", "触觉 ch3", "触觉 ch4" };
        for (int i = 0; i < 6; i++)
        {
            lt.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            lt.Controls.Add(new Label { Text = lname[i], Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, i);
            var pb = new ProgressBar { Height = 18, Anchor = AnchorStyles.Left | AnchorStyles.Right, Maximum = 100, Minimum = 0, Value = 0, Style = ProgressBarStyle.Continuous, Margin = new Padding(2, 6, 2, 6) };
            _bars[i] = pb;
            lt.Controls.Add(pb, 1, i);
        }
        lv.Controls.Add(lt);
        t.Controls.Add(lv, 0, 1);
        gb.Controls.Add(t);
        return gb;
    }

    private Control BuildParams()
    {
        var gb = new GroupBox { Text = "参数 / 端点 / 启动器", Dock = DockStyle.Fill, Padding = new Padding(8) };
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };

        _lblGain = new Label { Text = "总增益 --gain：2.00", Width = 340, Height = 20 };
        _tbGain = new TrackBar { Minimum = 0, Maximum = 800, Value = 200, Width = 340, SmallChange = 10, LargeChange = 50, TickFrequency = 100 };
        _lblSmooth = new Label { Text = "平滑 --smooth：0.55", Width = 340, Height = 20 };
        _tbSmooth = new TrackBar { Minimum = 0, Maximum = 95, Value = 55, Width = 340, SmallChange = 5, LargeChange = 10, TickFrequency = 10 };
        _lblSens = new Label { Text = "灵敏度 --sensitivity：60", Width = 340, Height = 20 };
        _tbSens = new TrackBar { Minimum = 0, Maximum = 100, Value = 60, Width = 340, SmallChange = 5, LargeChange = 10, TickFrequency = 10 };

        _chkTriggers = new CheckBox { Text = "启用扳机脉冲输出 (--triggers)", Width = 340, Height = 26 };

        var lblCap = new Label { Text = "采集端点（DualSense 4ch）：", Width = 340, Height = 20, Margin = new Padding(3, 10, 3, 0) };
        _cmbCapture = new ComboBox { Width = 340, DropDownStyle = ComboBoxStyle.DropDownList };
        var lblMir = new Label { Text = "镜像扬声器（真实，如 Realtek）：", Width = 340, Height = 20, Margin = new Padding(3, 8, 3, 0) };
        _cmbMirror = new ComboBox { Width = 340, DropDownStyle = ComboBoxStyle.DropDownList };
        var btnReload = new Button { Text = "刷新端点列表", Width = 340, Height = 30 };
        btnReload.Click += (_, _) => LoadEndpoints();
        var btnApply = new Button { Text = "应用参数并重启引擎", Width = 340, Height = 32 };
        btnApply.Click += (_, _) => RestartIfRunning(true);

        var lblSep = new Label { Text = "— FlexInput 启动器 —", Width = 340, Height = 20, Margin = new Padding(3, 14, 3, 2) };
        var btnFlex = new Button { Text = "启动 FlexInput（已在运行则不重复）", Width = 340, Height = 34 };
        btnFlex.Click += (_, _) => LaunchFlexInput();
        var btnPatch = new Button { Text = "打开 patch 所在目录", Width = 340, Height = 30 };
        btnPatch.Click += (_, _) => OpenPatchDir();
        _lblPaths = new Label { Width = 340, Height = 90, ForeColor = Color.DimGray, Text = "" };

        _tbGain.ValueChanged += (_, _) => { _lblGain.Text = $"总增益 --gain：{GainVal():0.00}"; ScheduleRestart(); };
        _tbSmooth.ValueChanged += (_, _) => { _lblSmooth.Text = $"平滑 --smooth：{SmoothVal():0.00}"; ScheduleRestart(); };
        _tbSens.ValueChanged += (_, _) => { _lblSens.Text = $"灵敏度 --sensitivity：{SensVal():0}"; ScheduleRestart(); };
        _chkTriggers.CheckedChanged += (_, _) => ScheduleRestart();
        _cmbCapture.SelectedIndexChanged += (_, _) => ScheduleRestart();
        _cmbMirror.SelectedIndexChanged += (_, _) => ScheduleRestart();

        flow.Controls.AddRange(new Control[] {
            _lblGain, _tbGain, _lblSmooth, _tbSmooth, _lblSens, _tbSens, _chkTriggers,
            lblCap, _cmbCapture, lblMir, _cmbMirror, btnReload, btnApply,
            lblSep, btnFlex, btnPatch, _lblPaths
        });
        gb.Controls.Add(flow);
        return gb;
    }

    private Control BuildLog()
    {
        var gb = new GroupBox { Text = "引擎日志（ps5r2x stdout 实时）", Dock = DockStyle.Fill, Padding = new Padding(6) };
        _log = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = false, Font = new Font("Consolas", 9F), BackColor = Color.FromArgb(24, 24, 24), ForeColor = Color.Gainsboro };
        gb.Controls.Add(_log);
        return gb;
    }

    // ───────────────────────────── 参数取值 ─────────────────────────────
    private double GainVal() => _tbGain.Value / 100.0;
    private double SmoothVal() => _tbSmooth.Value / 100.0;
    private int SensVal() => _tbSens.Value;

    // ───────────────────────────── 状态刷新 ─────────────────────────────
    private void RefreshStatuses()
    {
        try
        {
            var fx = Process.GetProcessesByName("flexinput");
            Set(0, fx.Length > 0, fx.Length > 0 ? $"运行中 (PID {string.Join(",", fx.Select(p => p.Id))})" : "未运行");

            var (ds, dsInfo) = DetectVirtualDualSense();
            Set(1, ds, dsInfo);

            var (audio, aInfo) = DetectDualSenseAudio4ch();
            Set(2, audio, aInfo);

            int wgi = WgiWatcher.Count();
            Set(3, wgi > 0, WgiWatcher.Describe());

            if (_lblPaths != null)
                _lblPaths.Text = "ps5r2x: " + AppPaths.Ps5r2xExe + "\r\nflexinput: " + AppPaths.FlexInputExe;
        }
        catch (Exception ex) { AddLog("[GUI] 状态刷新异常: " + ex.Message); }
    }

    private void Set(int i, bool ok, string text)
    {
        if (InvokeRequired) { BeginInvoke(new Action(() => Set(i, ok, text))); return; }
        _dots[i].BackColor = ok ? Color.LimeGreen : Color.IndianRed;
        _stat[i].Text = text;
    }

    private (bool, string) DetectVirtualDualSense()
    {
        try
        {
            using var s1 = new ManagementObjectSearcher("SELECT DeviceID FROM Win32_PnPEntity WHERE DeviceID LIKE '%VID_054C&PID_0CE6%'");
            foreach (ManagementObject o in s1.Get())
            {
                var id = o["DeviceID"]?.ToString() ?? "";
                return (true, id);
            }
            using var s2 = new ManagementObjectSearcher("SELECT DeviceID,Name FROM Win32_PnPEntity WHERE DeviceID LIKE 'ROOT\\\\HIDCLASS%' AND Name LIKE '%Wireless Controller%'");
            foreach (ManagementObject o in s2.Get())
                return (true, "ROOT\\HIDCLASS Wireless Controller");
            return (false, "未发现虚拟 DualSense");
        }
        catch (Exception ex) { return (false, "WMI 查询失败: " + ex.Message); }
    }

    private (bool, string) DetectDualSenseAudio4ch()
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            foreach (MMDevice d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                string name;
                int ch;
                try { name = d.FriendlyName; ch = d.AudioClient.MixFormat.Channels; } catch { continue; }
                if (name.Contains("DualSense", StringComparison.OrdinalIgnoreCase))
                    return (ch >= 4, $"{name} ch={ch}{(ch >= 4 ? "" : "（需 4ch）")}");
            }
            return (false, "无含 \"DualSense\" 的音频渲染端点");
        }
        catch (Exception ex) { return (false, "音频枚举失败: " + ex.Message); }
    }

    // ───────────────────────────── 端点列表（ps5r2x --list） ─────────────────────────────
    private void LoadEndpoints()
    {
        var names = RunList();
        if (names.Count == 0) { AddLog("[GUI] 端点列表为空（ps5r2x --list 无输出）"); return; }

        var capSel = _cmbCapture.SelectedItem as string;
        var mirSel = _cmbMirror.SelectedItem as string;

        _cmbCapture.Items.Clear(); _cmbMirror.Items.Clear();
        foreach (var n in names) { _cmbCapture.Items.Add(n); _cmbMirror.Items.Add(n); }

        _cmbCapture.SelectedItem = names.FirstOrDefault(n => n.Contains("DualSense", StringComparison.OrdinalIgnoreCase))
                                   ?? capSel ?? names[0];
        _cmbMirror.SelectedItem = names.FirstOrDefault(n => n.Contains("Realtek", StringComparison.OrdinalIgnoreCase) && !n.Contains("DualSense", StringComparison.OrdinalIgnoreCase))
                                   ?? mirSel
                                   ?? names.FirstOrDefault(n => !n.Contains("DualSense", StringComparison.OrdinalIgnoreCase))
                                   ?? names[0];
        AddLog($"[GUI] 端点: 采集=\"{_cmbCapture.SelectedItem}\" 镜像=\"{_cmbMirror.SelectedItem}\"（共 {names.Count} 个）");
    }

    private List<string> RunList()
    {
        var list = new List<string>();
        string file; string args;
        if (File.Exists(AppPaths.Ps5r2xExe)) { file = AppPaths.Ps5r2xExe; args = "--list"; }
        else if (File.Exists(AppPaths.Ps5r2xDll)) { file = "dotnet"; args = $"\"{AppPaths.Ps5r2xDll}\" --list"; }
        else { AddLog("[GUI] 找不到 ps5r2x.exe/dll"); return list; }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = file, Arguments = args, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8,
                WorkingDirectory = AppPaths.Ps5r2xDir
            };
            using var p = Process.Start(psi)!;
            string outp = p.StandardOutput.ReadToEnd();
            p.WaitForExit(4000);
            foreach (var raw in outp.Split('\n'))
            {
                var m = Regex.Match(raw, @"^\s*\[\d+\]\s+(.+?)\s+ch=(\d+)\s+rate=\d+");
                if (m.Success) list.Add(m.Groups[1].Value.Trim());
            }
        }
        catch (Exception ex) { AddLog("[GUI] 运行 --list 失败: " + ex.Message); }
        return list;
    }

    // ───────────────────────────── 引擎控制 ─────────────────────────────
    private void StartEngine()
    {
        if (_proc != null && !_proc.HasExited) { AddLog("[GUI] 引擎已在运行"); return; }

        string file; string prefix = "";
        if (File.Exists(AppPaths.Ps5r2xExe)) file = AppPaths.Ps5r2xExe;
        else if (File.Exists(AppPaths.Ps5r2xDll)) { file = "dotnet"; prefix = $"\"{AppPaths.Ps5r2xDll}\" "; }
        else { AddLog("[ERR] 找不到 ps5r2x.exe / ps5r2x.dll（先构建 tools/ps5r2x）"); return; }

        string args = prefix + BuildArgs();
        var psi = new ProcessStartInfo
        {
            FileName = file, Arguments = args, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = AppPaths.Ps5r2xDir
        };
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => { if (e.Data != null) OnLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) OnLine("[err] " + e.Data); };
        p.Exited += (_, _) => BeginInvoke(new Action(() =>
        {
            AddLog("[GUI] 引擎进程已退出");
            _btnStart.Enabled = true; _btnStop.Enabled = false;
        }));
        try
        {
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            _proc = p;
            AddLog($"[GUI] 启动: {file} {args}");
            _btnStart.Enabled = false; _btnStop.Enabled = true;
        }
        catch (Exception ex) { AddLog("[ERR] 启动失败: " + ex.Message); _proc = null; }
    }

    private string BuildArgs()
    {
        var a = new List<string>();
        if (_rbDemo.Checked) { a.Add("--demo"); a.Add("--duration"); a.Add("3600"); }
        else a.Add("--run");

        if (_cmbCapture.SelectedItem is string cap && !string.IsNullOrWhiteSpace(cap)) { a.Add("--endpoint"); a.Add(Quote(cap)); }
        if (_cmbMirror.SelectedItem is string mir && !string.IsNullOrWhiteSpace(mir)) { a.Add("--mirror"); a.Add(Quote(mir)); }
        a.Add("--gain"); a.Add(GainVal().ToString("0.##"));
        a.Add("--smooth"); a.Add(SmoothVal().ToString("0.##"));
        a.Add("--sensitivity"); a.Add(SensVal().ToString("0"));
        if (_chkTriggers.Checked) a.Add("--triggers");
        return string.Join(" ", a);
    }

    private static string Quote(string s) => "\"" + s.Replace("\"", "") + "\"";

    private void StopEngine()
    {
        try
        {
            if (_proc != null && !_proc.HasExited)
            {
                _proc.Kill(entireProcessTree: true);
                _proc.WaitForExit(2000);
            }
        }
        catch { }
        _proc = null;
        _btnStart.Enabled = true; _btnStop.Enabled = false;
        ClearBars();
        AddLog("[GUI] 引擎已停止");
    }

    private void RestartIfRunning(bool manual = false)
    {
        if (_restarting) return;
        if (_proc == null || _proc.HasExited) { if (manual) AddLog("[GUI] 引擎未运行，参数将在下次启动生效"); return; }
        _restarting = true;
        try
        {
            AddLog("[GUI] 参数变更 → 重启引擎生效");
            StopEngine();
            StartEngine();
        }
        finally { _restarting = false; }
    }

    private void ScheduleRestart()
    {
        if (!_uiReady) return;
        if (_proc == null || _proc.HasExited) return;
        _restartTimer.Stop();
        _restartTimer.Start();
    }

    // ───────────────────────────── 日志 / 电平解析 ─────────────────────────────
    private void OnLine(string line)
    {
        if (IsDisposed) return;
        try { BeginInvoke(new Action(() => { AppendLog(line); ParseLine(line); })); } catch { }
    }

    private void AppendLog(string s)
    {
        if (_log.TextLength > 300_000) _log.Clear();
        _log.AppendText(s + "\r\n");
    }

    private void AddLog(string s)
    {
        if (InvokeRequired)
        {
            try { BeginInvoke(new Action(() => AddLog(s))); } catch { }
            return;
        }
        AppendLog(s);
    }

    private void ParseLine(string line)
    {
        var m = MotorRe.Match(line);
        if (m.Success)
        {
            SetBar(0, (int)Math.Round(double.Parse(m.Groups[1].Value) * 100));
            SetBar(1, (int)Math.Round(double.Parse(m.Groups[2].Value) * 100));
            var t = TrigRe.Match(line);
            if (t.Success)
            {
                SetBar(2, (int)Math.Round(double.Parse(t.Groups[1].Value) * 100));
                SetBar(3, (int)Math.Round(double.Parse(t.Groups[2].Value) * 100));
            }
            else { SetBar(2, 0); SetBar(3, 0); }
        }
        var c = ChRe.Match(line);
        if (c.Success)
        {
            SetBar(4, DbToPct(double.Parse(c.Groups[1].Value)));
            SetBar(5, DbToPct(double.Parse(c.Groups[2].Value)));
        }
    }

    private static int DbToPct(double db)
    {
        if (db <= -80) return 0;
        double pct = (db + 80) / 80.0 * 100.0;
        return (int)Math.Clamp(pct, 0, 100);
    }

    private void SetBar(int i, int v)
    {
        if (InvokeRequired) { BeginInvoke(new Action(() => SetBar(i, v))); return; }
        _bars[i].Value = Math.Clamp(v, 0, 100);
    }

    private void ClearBars() { for (int i = 0; i < 6; i++) SetBar(i, 0); }

    // ───────────────────────────── FlexInput 启动器 ─────────────────────────────
    private void LaunchFlexInput()
    {
        var running = Process.GetProcessesByName("flexinput");
        if (running.Length > 0)
        {
            MessageBox.Show(this, $"FlexInput 已在运行（PID {string.Join(",", running.Select(p => p.Id))}），\n为避免双实例导致设备混乱，不再重复启动。", "单实例保护", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (!File.Exists(AppPaths.FlexInputExe))
        {
            MessageBox.Show(this, "找不到 flexinput.exe：\n" + AppPaths.FlexInputExe, "未找到", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo { FileName = AppPaths.FlexInputExe, UseShellExecute = true, WorkingDirectory = AppPaths.FlexInputDir });
            AddLog("[GUI] 已启动 FlexInput（若弹 UAC 请允许）");
        }
        catch (Exception ex) { MessageBox.Show(this, "启动失败: " + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private void OpenPatchDir()
    {
        try
        {
            if (Directory.Exists(AppPaths.FlexInputDir)) Process.Start("explorer.exe", AppPaths.FlexInputDir);
            else MessageBox.Show(this, "目录不存在：\n" + AppPaths.FlexInputDir);
        }
        catch (Exception ex) { MessageBox.Show(this, "打开目录失败: " + ex.Message); }
    }
}
