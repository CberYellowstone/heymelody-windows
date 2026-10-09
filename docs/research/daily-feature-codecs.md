# 日常控制的协议证据与适配边界

本文为公开整理稿；[整理前完整原文](../../research/migrations/organization-2026-10-09/originals/docs/research/daily-feature-codecs.md)保存在本地研究档案，不随普通Git clone提供。历史版本、来源及验证范围保留，完整研究对象与产物由 `research/` 保存。

本页记录 2026-10-08 为通用设置新增的静态协议核对。证据来自本工作区 `research/apps/无线耳机_17.6.3.apk` 的 `classes.dex`（SDK）和 `classes2.dex`（界面、配置与 repository）。DEX 偏移均为该样本的文件偏移。没有运行 Android、连接真实耳机或新增网络请求；测试使用内存 transport，不代表各型号实机验收。

实现入口为 [DailyFeatures.cs](../../src/HeyMelody.Daily/DailyFeatures.cs)、[DailyFeatureCodecs.cs](../../src/HeyMelody.Protocol/DailyFeatureCodecs.cs)，验收入口为 [DailyFeatureTests.cs](../../tests/HeyMelody.Daily.Tests/DailyFeatureTests.cs)。资源来源沿用完整配置选择，不能将无线耳机界面的分支规则直接套到欢律。

## 读取、设置与推送布局

下表只列协议 payload，不含现有 SPP 帧头、序列号和校验。`status=0` 为成功；`i8` 是有符号字节，所有字段保留原协议值。

| 设置 | GET request / reply | SET request | 样本定位 |
|---|---|---|---|
| 二值功能 | `010D [count, featureId…]` / `[status,count,(featureId,value)…]` | `0403 [featureId,value]` | SDK `commands.n.m @ 57C574` 的功能映射与现有 feature codec |
| 低音强度 | `0124 []` / `[status,min:i8,max:i8,current:i8]` | **`041B [current:i8,max:i8,min:i8]`** | `commands.d.c @ 5780C4`；`commands.n.M` 的 `8124` 分支 `57DA6C`；`HeadsetCoreService.F0 @ 5760F8` |
| 提示音音量 | `0130 []` / `[status,value:u8]` | `0427 [value]` | `HeadsetCoreService.l0 @ 573650`、`n1 @ 577798`；`commands.n.M` 的 `8130` 分支 `57D6BE` |
| 游戏音效类型 | `012B []` / `[status,selectedType,count,supportedTypes…]` | `0423 [selectedType,enabled:0/1]` | `commands.n.M` 的 `812B` 分支 `57D784`；`HeadsetCoreService.Q0 @ 5768E4` |
| 普通手势赋值 | `0108 [3,2,3,1]` / `[status,count,(device,button,action,function)…]` | `0401 [1,device,button,action,function]` | `commands.n.t @ 57BDF0`；`KeyFunctionInfo.<init>(I,[B) @ 5687F0`；`8108` 分支 `57EA16`；`HeadsetCoreService.X0 @ 576EDC`；`BluetoothService` 默认 `0401` 分支 `56E724` |

低音 GET 与 SET 的字段顺序不同。写入时使用最新 GET 返回的 min/max，不能固定为 `-6..6` 或沿用界面过期范围。解析器拒绝不完整布局、反向范围、超范围当前值，以及手势中重复的 `(device,button,action)` 键。

游戏音效的 `selectedType` 与启用状态是两个独立值：启用状态从 `010D` 的 feature `27` 读取。类型 `0` 不是“关闭”的代名词。选择类型时保留当前 enabled，切换开关时保留当前 selectedType；选项由资源声明、固件条件与 `012B` 返回支持列表交集决定。

提示音原厂 `PromptVolumeBarPreference` 构造器 `29F59C/29F5DC/29F61C` 的范围默认 `1..10`。资源 `promptVolumeRange.min/max` 非 `-1` 时覆盖。不存在范围声明时可采用该已证默认值；存在但类型或范围非法时禁止设置，不能回退成可写范围。SET 的传输值仍为一个字节。

## 二值功能声明

新增功能查询只在选定资源明确声明相应原厂版本时加入。通用二值版本为 `1`；入耳检测允许版本 `1/2`。未知未来版本不当作开关。

| resource function | feature ID | 界面 |
|---|---|---|
| `wearDetection` | `04` | 入耳检测 |
| `vocalEnhance` | `09` | 人声增强 |
| `hearingEnhancement` / `hearingEnhancementNew` | `0B` | 仅供黄金听感状态读取 |
| `smartCall` | `15` | 智能通话 |
| `longPowerMode` | `17` | 长续航模式 |
| `controlAutoVolumeSupport` | `1C` | 自动音量 |
| `bassEngineSupport` | `1D` | 低音增强；另有强度 GET/SET |
| `adaptiveVolume` | `30` | 自适应音量 |
| `adaptiveEar` | `31` | 自适应听感 |
| `speechPerception` | `32` | 人声感知 |
| `longPressVolume` | `35` | 长按调节音量 |
| `hearingOptimize` | `38` | 听感优化 |
| `incomingCallControl` | `39` | 来电控制 |
| `sleepDetection` | `3A` | 睡眠检测 |

没有额外声明时，保留原 `010D [5,11,06,28,18,1B]` 请求。黄金听感 `0B` 不生成普通开关，也不允许 `ApplyFeatureAsync("hearing-enabled",…)` 绕过已保存测听参数、耳扫记录与 UID 校验；它由独立校准服务管理。

## 手势样式与功能掩码

源 `control[].action` 是界面样式，不总等于固件 action。OEM repository `L.d @ 2212B0` 的转换为：`11/19/20/27→4`，`22/31/35→6`，`16/28/32→1`，`17/26/29/33/36→2`，`18/30/34→3`；其他值保留。

源 `support` 也不是 `1 << firmwareFunction`。`F9/a.a @ 138894` 的已证本机手势映射为：

| firmware function | resource support mask | 原厂功能名称 |
|---|---|---|
| `0` | `0200` | 无 |
| `1` | `0004` | 播放 / 暂停 |
| `5` | `0020` | 上一首 |
| `6` | `0040` | 下一首 |
| `7` | `0400` | 降噪开 / 关 |
| `8` | `0080` | 音量调节 |
| `10` | `0800` | 歌曲切换 |
| `11` | `0008` | 增大音量 |
| `12` | `0010` | 减小音量 |
| `13` | `1000` | 设备切换 |
| `17` | `2000` | 游戏模式 |
| `22` | `8000` | 耳机专注 |
| `26` | `100000` | 拒接来电 |
| `27` | `200000` | 拒接来电 / 挂断电话 |
| `29` | `80000` | 接听 / 挂断电话 |

OnePlus Buds / Buds Z 的上一首掩码采用 `0040`，定位 `B1/b.J @ 19E6EC`。中文名称核对 OEM `resources.arsc` 的 `zh-rCN` 资源，包含 `adjust_volume`、`switch_song`、`increace_volume`、`decreace_volume`、`switch_device`、`game_mode`、`zen_mode_title`，没有使用关键词猜测固件数字。

设置只使用 GET 已返回的左/右耳 `(device,button,action)` 键，并与同 action/button、版本条件通过的源 `control` 选项匹配。未知当前 function 保留原值，用户仍可选择已声明的原生选项。手机助手、收藏音乐、翻译、AI 摘要、Gemini 等选项没有 Windows 对应服务路径，暂不发送。`controlList`、`calControl`、`musControl` 等额外来源分组尚未统一解释，本实现不凭字段名补出手势写入。

## 游戏音效互斥必须区分来源

`GameSetItem.<init> @ 26E8A4` 将当前 JSON `gameSoundMutexes` 中的 `1→ma/m.b`、`2→ma/m.a`、`3→ma/m.c`、`4→ma/m.d` 写入地址键控的布尔 map。它们不是协议 feature ID。

OEM 包下，`ca/r.h @ 1A2514` 对 `key_bass_engine_switch` 检查 map `c`，随后 `ca/r.I @ 1A3E9C` 经 repository `Q0` 设置 feature `1D`。`HearingOptimizeItem.checkGameSoundMutexDialog @ 271200` 检查 map `d`，`setHearingOptimizeEnable @ 271A98` 设置 feature `38`。所以 **无线耳机来源**的 `3/4` 可分别以最新 `1D/38` 状态判断是否允许开启游戏音效：都关闭时放行，开启时要求用户先明确关闭，未知时禁止开启。

这两个界面方法都有 `com.heytap.headset` 包名分支，欢律路径改读 map `a`。不能对欢律来源沿用 OEM `3/4` 规则。当前实现只有 `DeviceProfile.Provenance.ArchivePath == "WirelessEarphones"` 才允许上述细分校验；欢律/未知来源声明 `3/4` 时明确显示未核实原因并禁用启用。`1/2` 和其他未核实规则同样禁止启用。已经开启的游戏音效仍允许关闭，不为未知互斥规则自动关闭其他功能。

对话框共享 RID `7f1103e0/3df` 的文案为“自适应听感”、`940/93f` 为空间音效、`93e/93d` 为调音，不能只凭对话框数字或文案反推 map/feature。上面的映射依据实际控件键、map 字段和 repository SET 参数。

## 异步更新与未证明范围

OEM `HeadsetCoreService.m0 @ 5736B0` 的 `0500..051D` switch 中，`0508` 分支 `5747C0` 将 **`[min:i8,max:i8,current:i8]`（没有 status）** 交给同一低音 parser；调用点 `5747F8`。它不是 `0507` 双设备信息，也不是 `050A` 颈椎功能。

实现接收 `0508` 后立即更新已声明的低音控件；监控开启时协调一次只读 Features GET 验证，不在 reader 回调中阻塞发请求。`0124` 的读取组归为 `bass`，查询开始后到达的新推送优先于旧 GET 回包。现有 `0503` 则更新二值功能与游戏音效启用状态，未知值禁用相应写入。

本次没有从该 `0500..051D` 分发链确认提示音音量、游戏音效类型列表、手势赋值的专用通知布局；不能据此声称所有 APK/固件都没有这些通知。上述状态由已证 GET 刷新；没有猜测新的通知 ID、事件订阅或 SET。游戏音效启用状态的 `0503` 不等于完整类型列表推送。

所有新设置写前重新读取身份、命令能力、固件/来源条件与当前状态。ACK 缺失、拒绝或传输中断均不重发 SET；即使回读吻合也不会把未知/拒绝 ACK 报作成功。当前日常测试合计 **85 项通过**，其中新增通用控制测试覆盖字段顺序、未知版本、范围变更、两源互斥差异、推送竞争、非法资源和 ACK 不确定性。其他设备与固件仍需真实硬件验证。

**资料归档（2026-10-08）：** 本文访问链接与复现路径已按新目录更新；研究时的原始位置为仓库根 `research/`，原报告与 manifest 内路径保留不变。新旧映射及提交边界见 [资料归档入口](README.md)。
