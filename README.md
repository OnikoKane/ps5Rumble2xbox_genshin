# ps5Rumble2xbox_genshin — 调研与踩坑记录

> **一句话结论：在原神 PC 版上，把 Xbox 手柄伪装成 DualSense 去获取「PS5 原生震动（HD 触觉）」是不可行的——因为原神 PC 版根本不向 DualSense 发送触觉数据。** 本仓库记录完整调研、实测证据与踩坑，供他人避坑。

**English TL;DR:** Genshin Impact on PC does **not** emit DualSense HD haptics (the 4-channel controller audio's ch3/4 stay silent). Its "DualSense support" is only button glyphs / UI. We built a full virtual-DualSense pipeline and verified every stage (virtual USB composite DualSense, input mapping, real Xbox 4-motor rumble, HidHide hiding the physical Xbox, and a WASAPI audio→motor engine), but there is simply **no haptic data to convert**. Sound-to-vibration does produce rumble, but it is semantically wrong (music/voices trigger it) and introduces audio-routing/volume issues. Project discontinued; this repo is the post-mortem to save others the trouble.

---

## 1. 背景与目标

初衷：让一把官方 **Xbox 手柄**被《原神》PC 版识别为 **PS5 DualSense**，从而让游戏输出 DualSense 的 HD 触觉/震动参数，再把这些参数转换成真实 Xbox 手柄的震动（握把 + 扳机脉冲马达），并带调试界面。

结论先行：这条链路的前半段（伪装成 DualSense、被原神识别为 PS5）能做成，但**后半段的前提不成立——原神 PC 不发触觉数据**，所以整个目标无法达成。

## 2. 核心结论与实测证据

### 2.1 原神 PC 不向 DualSense 发送 HD 触觉（关键证据）

DualSense 的 USB 音频是 4 声道：**ch1/2 = 扬声器，ch3/4 = 左右震动致动器（Voice Coil，即 HD Haptics）**。要想"拿到震动参数"，游戏必须往 ch3/4 写触觉波形。

实测方法（可复现）：
1. 用 FlexInput 创建虚拟 **USB 复合 DualSense**（含 4 声道音频端点，下称"虚拟手柄音频端点"）。
2. 把 Windows **默认播放设备**设为该虚拟端点。
3. 用 WASAPI loopback 只读采集该端点，进原神运行、游玩（走动/战斗）观测各声道电平。

实测结果：
```
ch1/ch2 ≈ -30 ~ -42 dB    ← 有普通游戏音频
ch3/ch4 = -180 dB         ← 全程静音，无任何触觉波形
blocks  > 0               ← 端点确实在接收数据（不是没连上）
```
**ch3/4 恒为静音 ⇒ 游戏从未写入触觉数据。** 换言之，PC 版原神没有任何"要转换的震动参数"。

### 2.2 原神的"DualSense 支持"只有 UI，没有触觉

当原神把该手柄识别为 DualSense 时，按钮图标会变成 PS5 样式（✕ ○ □ △），**但没有任何震动反馈**。而且因为游戏进入"PS5 模式"，连它原本在 Xbox 模式下会发的 XInput 震动也不再发——**一切换到 DualSense 模式，震动反而"彻底消失"**。

### 2.3 附带发现的问题

- **虚拟手柄做默认音频设备会引发音量/路由异常**：虚拟 DualSense 同时是一个音频设备，Windows 把它当扬声器后，会出现"无法调节音量 / 声音跑到虚拟设备"等问题。
- **"声音→震动"兜底能出震动，但语义错误**：把游戏主音频的低频转成马达，音乐会、人声、环境音都会触发震动，不等于真实打击反馈，体验不佳。

## 3. 我们验证过的技术链路（可复用经验）

| 组件 | 做法/结论 | 状态 |
|---|---|---|
| 虚拟 USB 复合 DualSense | FlexInput（Easy 模式 + General purpose preset + 输出 DualSense + Audio&Haptics；底层 HIDMaestro + usbip-win2）生成 `VID 054C:PID 0CE6`，HID + 4 声道 USB 音频。ControllerMeta 可识别，报告描述符 输入64/输出48/特征64 字节 | ✅ 可行 |
| 输入映射 | 物理 Xbox（XInput）→ 虚拟 DualSense，按键映射正常 | ✅ 可行 |
| 真实 Xbox One 4 马达震动 | `Windows.Gaming.Input.GamepadVibration`（LeftMotor/RightMotor/LeftTrigger/RightTrigger，0..1）可驱动，实测握把 + 左右扳机脉冲马达都能震；`XInput`(XInputSetState) 只有 2 马达 | ✅ 可行 |
| HidHide 隐藏物理 Xbox | 用 HidHide 可把物理 Xbox 的 **XInput 对非白名单进程隐藏**（实测 XInput 槽清零、RawGame 0），使游戏只看到虚拟 DualSense（原神因此**稳定识别为 DualSense / PS5 UI**）。代价：内核驱动 + 需重启；官方对 Xbox 隐藏标注"不可靠"(issue #39)，但本机实测成功 | ⚠️ 可行但有代价 |
| 音频触觉→马达引擎（自研） | WASAPI loopback 采集端点；多声道取 ch3/4 作触觉；ch1/2 镜像到真实扬声器；低通/带通+噪声门+瞬态包络把 ch1/2 低频转马达（`--source main/auto`） | ✅ 可行 |

也就是说：**除了"游戏提供触觉数据"这一步缺失，其它每一环都跑通了。** 缺的恰恰是最关键的数据源。

## 4. 最终结论

- **在《原神》PC 版上实现"DualSense 原生触觉 → Xbox 震动"：不可能**——没有数据源（ch3/4 恒静音）。
- "声音→震动"兜底虽能出震动，但非真实触觉、误震多、且虚拟手柄作默认音频设备会带来音量/路由问题，体验差。
- **建议：放弃在《原神》PC 上做该目标**；同样的方法（虚拟 DualSense + HidHide + 音频→马达）可移植到**真正支持 DualSense 触觉的游戏**上。

## 5. 复现本结论的最小步骤

1. 安装 FlexInput（便携版即可），Easy 模式：输入=你的手柄；输出=`DualSense` 并勾 `Audio & Haptics`；顶部 Preset 选 `General purpose preset`。
2. Windows 设置 → 声音 → 输出：把默认播放设备设为 `DualSense Wireless Controller`。
3. 用任意 WASAPI loopback 工具采集该端点（或参考本项目思路自写），观察各声道电平。
4. 进原神运行；观察 `ch3/ch4` 是否始终为 -180 dB（是 → 无触觉数据）。

## 6. 参考资料 / 同类项目

- FlexInput（x-iso/FlexInput）：虚拟 DualSense / Edge + USB 音频 / HD 触觉 + usbip，Windows 上少见的完整虚拟 DualSense 方案。
- DualSenseBridge（flyingriverhorse/DualSenseBridge）：反向（PS5→Xbox），其报告解析与音频触觉处理可参考。
- HidHide（nefarius/HidHide）、ViGEmBus（nefarius/ViGEmBus）：隐藏物理设备 / 虚拟 Xbox。
- SAxense（egormanga/SAxense）、HapticImproveManager（ZhuTTTT/HapticImproveManager）：DualSense 触觉/延迟参考。
- Nielk1 的 DualSense 自适应扳机效果字节布局参考（TriggerEffectGenerator）。

## 7. 隐私与免责

- 本文档已剔除**个人身份信息、本机绝对路径、设备序列号 / MAC、访问令牌**等敏感内容；仅保留公开的硬件 VID/PID 以便复现。
- 结论基于 2026-11 的 Windows 11 + 《原神》PC 版实测；游戏 / 驱动 / 系统更新后可能变化，请自行复测。
- 本项目已停止，不再继续实现；仓库只作调研与避坑记录。
