# tools/ — 本项目实现的工具（供公众测试，避免重复造轮子）

> 这些是调研过程中实现的工具。**结论见仓库根 `README.md`：原神 PC 不发 DualSense 触觉数据，本项目已停止。** 工具本身是通用可复用的，开放出来供他人测试/借鉴。

## 环境 / 构建
- Windows 10/11 x64，**.NET SDK 8**（`dotnet --version` ≥ 8）。
- 各工具单独构建：进入对应目录执行 `dotnet build -c Release`（`ps5r2x` 会自动还原 NAudio NuGet）。
- 源码里不包含第三方驱动（FlexInput / HIDMaestro / usbip-win2 / HidHide），需自行安装。

## 各工具

### `ps5r2x/` —— 音频→Xbox 4 马达 引擎（核心）
WASAPI loopback 采集某个音频渲染端点的音频，转成真实 Xbox 手柄的震动。
- 多声道（≥4，如虚拟 DualSense 的 USB 音频）取 **ch3/4 作触觉**；`ch1/2` 可实时**镜像到真实扬声器**（解决"虚拟手柄作默认音频设备导致听不到声音"）。
- `--source haptic|main|auto`：haptic=只用 ch3/4；main=把 ch1/2 低频(带通)转震动；auto=有 ch3/4 用 ch3/4，否则 ch1/2 兜底。
- DSP 参数：`--band-lo --band-hi --gate --attack --release --gain --smooth --sensitivity`。
- `--triggers`：把 L/R 也映射到扳机脉冲马达（LT/RT）。
- 其它：`--list`（列出端点）、`--endpoint <子串>`、`--demo`（自测音）、`--inject ch3,ch4`、`--mirror <子串>`、`--run`（生产：DualSense+镜像+auto）、`--seconds N`。
- 关键点：**Windows.Gaming.Input 可驱动 Xbox One 的 4 个马达（含左右扳机脉冲马达）**；XInput 只有 2 个。
- 用法示例：`ps5r2x --list` 然后 `ps5r2x --endpoint "DualSense" --run`。

### `xbox4motor/` —— 真实 Xbox 4 马达验证
用 `Windows.Gaming.Input.GamepadVibration` 依次单独驱动 左马达/右马达/左扳机/右扳机，用 XInput 做对照（证明 XInput 只有 2 马达）。用来确认"你的 Xbox 手柄扳机脉冲马达能不能被驱动"。
- `xbox4motor --probe` 只枚举；`xbox4motor --hold 2000` 分段驱动。

### `ps5Rumble2Xbox/` —— WinForms 调试 GUI
状态灯（FlexInput 进程 / 虚拟 DualSense / 4ch 音频端点 / Xbox WGI）、引擎启停（调用 ps5r2x）、增益/平滑/灵敏度滑杆、实时电平、一键启动 FlexInput。
- 需要 `ps5r2x` 已构建在 `tools/ps5r2x/bin/Release/net8.0-windows10.0.19041.0/` 下；根目录可用环境变量 `PS5R2X_ROOT` 指定。

### `ds5shm/` —— HIDMaestro 共享内存只读解码（实验）
只读映射 HIDMaestro 的 `Global\HIDMaestroOutput{i}` 共享内存，解码 DualSense 输出报告(0x02)里的电机与**扳机效果 11 字节**。
- ⚠️ 仅对 **HIDMaestro(HID-only)** 虚拟设备有效；对 **usbip 复合**虚拟 DualSense（带 USB 音频）无效（其输出被 FlexInput 进程内部消费，不写 shm）——见根 README。

### `ps5r2x/*.bat` —— 便捷脚本
`vibtest.bat`（4 马达震动演示）、`griptest.bat`（只震握把）、`genshin_run.bat`（DualSense 端点+镜像）、`genshin_sound_rumble.bat`（声音→震动，握把 only）。

## ⚠️ 风险与免责
- 这些工具会通过第三方驱动创建/读取虚拟手柄、隐藏物理设备、采集音频。**自担风险**，请仅在测试环境使用；不要在介意反作弊的在线游戏里使用注入/隐藏类手段。
- 与 Sony / Microsoft / miHoYo 无关；DualSense / PlayStation / Xbox 为其各自商标。
- "虚拟 DualSense" 由 FlexInput（x-iso/FlexInput，MIT）提供，本仓库不含其二进制。
