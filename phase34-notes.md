# 阶段 3/4：Hi-Res 闭环与高采样率实测

2026-10-06 完成。代码包含 `HeyMelody.Control`、`HeyMelody.Audio`、`HeyMelody.Experiments`，用户入口 `Run-HiRes.ps1`。原有只读入口与阶段 1/2 证据保留。

## 结果

汇总：`artifacts/phase34-validation.json`，success=true。

| 层次 | 本机验证结果 |
|---|---|
| 耳机控制 | 唯一 Hi-Res SET 0403 / 18 01，匹配序号 ACK status0 |
| 空间互斥 | baseline 新空间模式0，实际没有必要关闭空间；活跃空间的关闭/ACK/回读路径经离线测试 |
| 重连与回读 | 观测到 Windows 蓝牙断连，新控制连接重新确认06EC10、能力、Hi-Res=1、空间=0 |
| 驱动能力 | 44100/48000 扩展为44100/48000/96000/192000；无需 SYS 补丁 |
| 耳机 AVDTP 能力 | SEP3 raw `3a050000354c3506114000`，44.1/48/96/192 kHz，16/24 bit |
| 192k 配置 | `3a050000354c0102114000`，耳机接受 SetConfiguration，接受 Start，186 个已识别 LHDC5 媒体包 |
| 96k 配置 | `3a050000354c0402114000`，耳机接受 Reconfigure，接受 Start；连续捕获对应4207个媒体包 |
| 192k 软件流 | Windows engine/client均192000 Hz，driver during Streaming(2)、192000/24 |
| 96k 软件流 | Windows engine/client均96000 Hz，driver during Streaming(2)、96000/24 |
| 最终状态 | Hi-Res=true、192000 Hz/24 bit，Quality index5、LowLatency=true |

**采样率已经是实际协商的 LHDC5 配置，不只是下拉选项或驱动目标。**这些证据仍不等于耳机 DAC 时钟测量、逐样本 bit-perfect、当前压缩始终无损或听感评价。测试应用发送短静音 PCM16；引擎使用浮点32，协议编码位深24。实际数据包码率、延迟、耗电与无损比例未测。

## 可复用调用

```powershell
cd D:\Code\LHDC_Driver\HeyMelodyWindows
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Run-HiRes.ps1 -Action Enable
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Run-HiRes.ps1 -Action DriverStatus
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Run-HiRes.ps1 -Action Rate -SampleRate 96000
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Run-HiRes.ps1 -Action Play -SampleRate 96000
```

默认 Action 为 DriverStatus。Enable 可能短暂中断播放，已有 Hi-Res=1 时不重发 SET。Rate 只允许96/192 kHz、使用 persist=false、新鲜能力和稳定 ID 检查，最多一次提交。设置提交后不自动重试。Play 最多约2秒静音，不调整音量、不暂停其他应用、不强制占用独占端点。切换采样率时端点可能重新枚举；若返回未就绪，只等待并重新查询/播放，不重发已经接受的设置。

`Run-HiRes` 返回的是应用与驱动结果，`WireActualVerified=false` 保持未确认状态；需另外分析捕获才能确认线上参数。`tools/build_validation_summary.py` 仅汇总此次固定证据文件，不发设备命令。捕获与分析命令见 `tools/audio-evidence/README.md`。

## 闭环与安全边界

Control workflow 每个连接都核验精确 Product ID、X4配置 pin 和完整能力。优先012A/0422新空间模式，只有新模式不支持才查询旧1B。空间未知、关闭 ACK 或回读失败时停止，不盲目启用 Hi-Res。

控制发送允许列表限定基础 GET、Hi-Res/空间 GET、0403 的18 01/1B 00及0422模式0。没有注册通知、主机能力宣告、OTA或配对操作。SET 在进入 Send 前标记 Submitted；超时、连接丢失或空ACK后只允许新会话 GET，绝不自动回放 SET。空ACK为 ACK unknown，可通过独立状态回读验证；明确拒绝不能伪称 accepted。

Driver wrapper 精确匹配 Bluetooth address 对应的稳定 Audio Sink ID；WASAPI endpoint 使用 PnP祖先链关联，不能用名称或默认输出代替。三次、有界、只读身份枚举重试应对端点过渡；歧义立即拒绝，不重复 Initialize/Start。Core源码只引用，未修改。

用户授权阶段3/4后实际开启Hi-Res，并进行多次已确认的96/192独立试验；所有driver rate请求均 persist=false。未更改 Mode、BitDepth、Quality、LL、许可证、签名、驱动绑定或 SYS。高采样率实验副本没有安装。

## 线上证据与隐私

- `phase3-hires-enable.json`：单次 SET、ACK、before/after及新会话。
- `phase34-driver-before.json` / `phase4-driver-after-hires.json`：能力从低采样扩展到高采样。
- `phase4-192-wire-final.json`：一个完整连续会话捕获，严格 ConnectionComplete地址→ACL生命周期→PSM19/CID→事务 ACK→媒体关联，目标归属 verifiedAddress。
- `phase4-96-wire-continuous.json`：另一个完整连续捕获，target verifiedAddress、96k配置接受+Start+媒体。早期拼接报告只保留为开发期证据，最终96结论使用本连续样本。
- `phase4-fullsession-play-192.json`、`phase4-continuous-play-96.json`、`phase4-last-playback.json`：实际WASAPI与driver Streaming采样。
- `phase4-last-driver.json` / `phase34-last-ear-status.json`：完成前最终只读回读。

96k连续捕获记录一个较早的 malformedOrReassemblyError，安全报告如实保留。关键96k Reconfigure接受、Start接受和后续媒体链均在该记录之后完整解析。成功结论不声称整个捕获没有异常。

原包、私有目标地址和tshark中间字段均位于被gitignore的 `artifacts/private`，ACL只给当前用户/Admin/SYSTEM。捕获写盘前仅保留指定 Intel radio USB bulk 及 ConnectionComplete/DisconnectionComplete事件，不保留其他Interrupt/Control，不记录链路密钥事件。radio bulk可能含其他连接数据，因此原包不可直接分享。安全JSON不包含MAC、音频内容或其他对端名称。

为了取得完整握手，实测只对已配对X4做了Windows设置中的断开/连接；没有删除配对或重启蓝牙适配器。一次管理员节点重启辅助尝试未生成成功回执，因此未计入成功动作，辅助脚本已撤除。

解析器修复了 IRP指针复用导致的全局去重错误，改按OUT提交/IN完成选择；保留严格地址与CID关联。DelayReport(signal13)正确作为元数据事务，不误报帧损坏，不影响媒体配置判断。

## 验证

- Control：19个离线场景通过，包括空间关闭顺序、失败停止、SET超时唯一提交、错误/空ACK、fresh身份与断连GET重试。
- Audio：88个离线断言通过，包括稳定ID、严格端点选择、SDK GUID实际头文件比对、读写错误与无自动重试。首次实机发现的IMMDeviceCollection GUID声明错误已修复并回归。
- AVDTP分析器：23个离线场景通过，含实际tshark读取合成USBPcap、IRP复用、peer-initiated PSM19 pending/success、DelayReport。
- Release及ARM64交叉构建0 warnings /0 errors。ARM64实际耳机、驱动与播放未运行。
- 本机96/192 kHz均完成实际短静音播放、driver Streaming回读及独立协议证据；最终汇总要求全部证据成立才success=true。

SYS、reference、旧阶段报告及旧Manager源码/发布包保持原样。实际声学输出和长时稳定性仍不是本阶段的测量对象。
