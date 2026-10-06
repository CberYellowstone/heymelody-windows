# 阶段 5 协议证据与有界独立审阅

## 历史证据与当前实现的区别

下文保留阶段五原始官方协议来源和有界源码审阅。执行者、未委派/未调用设备、12 项资源断言、当时的源码行号及 SHA256 固定范围均属于那次记录；文中“当前实现”指原审阅时版本，不构成对后来 Avalonia 改动的重新审阅或实机证明。协议编号、负载布局与来源仍可参考；当前代码行为以 [daily-notes.md](daily-notes.md) 为准。

当前 Daily/Host 的每次 Apply 仍是单次尝试，目标 SET 不自动重放，获得显式同意的互斥前置 SET 未完整确认就停止；客户端在重新完整读取并核验同一设备后，可以按安全重复或用户确认规则进行最多一次后续尝试。新建 EQ 不确定时先确认并核对唯一新增完整匹配条目，复用设备回读 ID，不追加猜测的激活命令。因此下文“只提交一次”“不自动再发 SET”的历史结论不能扩展为当前整个 UI 操作永远没有第二次尝试。

当前结果独立记录提交、ACK 接受/拒绝/未知和回读匹配/不匹配/不可用；后来的状态匹配不证明原 ACK 被接受，也不能证明修改来源。明确 SET EOF/IO/socket 断开与超时/格式未知分开；GET 重连恢复不会抹去该次断开失败。分区刷新只更新所查询字段，从锁内最新缓存保留其他分区，普通字段失败不沿用该字段的旧值。Product ID 只识别型号，同一物理目标由 Host 当前不透明身份与精确地址绑定验证。

电量 Raw=0 在当前 Daily/UI 中显示未知，不据此认定不在位或精确耗尽。下文原始“未知/不在位”措辞不应理解为已经证明物理不在位；更细的来源和保守显示政策见 daily-notes.md。新增刷新、重试和连接分类测试使用内存传输及伪工作进程，不为原协议来源增加真实 SET 或 ARM64 硬件证据。

## 原始核对记录

核对时间：2026-10-06 UTC。执行者运行时为 `gpt-6.1-sol/high`；根级授权边界为 `gpt-6.1-sol/max`。没有委派。所有证据来自本地只读官方 APK 解包、既有型号配置和桌面参考源码。没有调用蓝牙、驱动、注册表、私有抓包、许可证模块或系统设置。本文不证明设备已接受任何 SET，也不证明 ARM64 已实际运行。

## 官方编号与中文标签

X4 配置必须按原始编号匹配，不能按列表位置发送。配置来源 `src/HeyMelody.Devices/Assets/oppo-enco-x4.json:266`，APK 映射为 `reference/OPPOEncoX4/encox4/jadx/sources/Ab/h0.java:148-175`，中文资源为 `apktool/res/values-zh-rCN/strings.xml:1853,1867,1871,1890,1892`。

| protocolIndex | modeType | 官方中文 |
|---:|---:|---|
| 0 | 42 | 丹拿原声 |
| 7 | 43 | 丹拿高解析 |
| 2 | 28 | 纯享人声 |
| 1 | 46 | 清亮高音 |
| 3 | 29 | 澎湃低音 |

降噪配置的父/子编号也是独立字段，不能把 modeType 当发送编号。X4 配置 `oppo-enco-x4.json:204-264`：父 1 降噪，子 7 智能切换、4 深度降噪、5 中度降噪、6 轻度降噪；父 11 自适应，子 12 更少噪音、13 标准、14 更多噪音；父 0 关闭，子 3 关闭；父 2 通透，子 8 通透。

智能/深度/中度/轻度标签由 `apktool/smali_classes3/Zb/k.smali:7-188` 的 modeType 7/4/8/3 映射资源 3c5/2a2/3e8/3d4。自适应由 `Zb/b.smali:506-588` 的 modeType 11/13 映射“更少噪音”/“更多噪音”，12 为“标准”；资源在 `values-zh-rCN/strings.xml:323-329,819,529,834,854`。官方有默认分支，但客户端对于未知编号应保留未知，不能回退显示“标准”。

## 电量：0106 → 8106

官方 `CommandUtil` 的 `apktool/smali_classes3/com/oplus/melody/btsdk/protocol/commands/e.smali:1828-1927` 读取一个 unsigned count，再按每条两个字节构造 `BatteryInfo`。查询响应外层包含 status，日常 GET 解析应采用 `[status,count,(component,raw)*count]`，不能自动尝试省略数量、分离 charging 或 count=4 特例。组件 1/2/3 分别左右耳/盒；其他组件保留原始编号。

`jadx/sources/com/oplus/melody/btsdk/api/data/BatteryInfo.java:104-108` 直接读取 `raw&0x7F` 和充电位 `raw&0x80`。`fixBattery:72-85` 在 UI 层把小于 1 的值变为 0、大于等于 100 的值钳位 100，并可能依配置进位；这不是 wire 上“127 为未知哨兵”的证据。Windows 客户端保留原始值，只有 1..100 显示百分比，0 视为未知/不在位，101..127 不显示异常百分比，是明确的保守显示政策。不要把参考桌面 Battery.cs 的宽松 fallback 作为官方格式。

## 降噪：010C → 810C，0404 → 8404

官方 `PollCommandManager` (`commands/m.smali:7310-7441`) 根据 response kind 分流，kind 1 在 offset 2 构造 `CurrentNoiseModeInfo`；该类 `jadx/.../CurrentNoiseModeInfo.java:31-66` 把 type 1 和 5 解析为 LSB 位图，type 2 则读取 level。故普通响应为 `[status,kind,type,bitmap...]`，不是固定两个逻辑模式字节。未知 kind/type、零位、多位和配置之外的单个位都应保留原始状态。

`CurrentNoiseModeInfo.java:73-85` 返回第一个 set bit index；官方 UI `Zb/n.java:55-69` 用该 index 与配置子项 protocolIndex 匹配。这证实 bit 1 只表示父降噪容器，不能凭它猜智能或深度；bit 0/2 是父关闭/通透，bit 3/8 是相应子状态编号。当前实现可显示父态，发送具体档位仍使用已核实子编号。

官方子档选择链 `Zb/g.smali:751-767 → earphone/O.smali:16334-16341 → O.l0:16044-16109 → earphone/y.smali:293-310` 将原始 protocolIndex 设为唯一 bit，type=1。`CurrentNoiseModeInfo.getData:87-118` 构造 `[1,1,bitmap...]`。例如子 4 为 `01 01 10`，子 8 为 `01 01 00 01`，子 14 为 `01 01 00 40`。这是参考实现生成的负载事实，不是本轮实际 SET 验证。

kind 4 描述智能实时降噪档；不能把关闭、通透或自适应 bit 强制转换为智能。只接受有证据的 ANC 子档 4/5/6/7，再把主选择保留为智能；即时原始 mask 继续留存。

## 预设与十段自定义 EQ

预设 GET `010F`、SET `0406` 使用内置原始 preset ID；X4 不连续顺序为 `[0,7,2,1,3]`。自定义 EqInfo ID 与此命名空间不同。

官方 GET all EQ 为 `0122` 空负载：`commands/m.smali:9177-9238` 使用 `Lw8/b.b`，而 `apktool/smali_classes3/w8.1/b.smali:32-43` 明确将其初始化为空 byte[]。不得未经证据改成 `[1,5]`。

官方解析 `commands/e.smali:157-1739` 的布局为 `[status,count, entries...]`，每项 `[selectedU8,minS8,maxS8,idU8,nameLengthU8,UTF8Name,bandCountU8,(frequencyLE16,gainS8)*N]`。selected 在 `e.smali:364-373` 读取完整 unsigned byte，并在 `:1587` 直接赋给 EqInfo；没有 bit0 掩码证据。客户端严格只接受 0/1。bounds/count、唯一 ID、唯一频率、UTF8 严格解码和 gain 范围应明确校验，不复制官方异常后部分返回的行为。

SET `0418` 在 `btsdk/multidevice/HeadsetCoreService.smali:3419-3863` 构造 `[action,minS8,maxS8,idU8,nameLengthU8,UTF8Name,bandCountU8,(frequencyLE16,gainS8)*N]`。nameLength 是 UTF8 字节数，单字节格式上限 255；这只是 wire 长度上限，不声称官方 UI 允许任意 255 字节名字。官方 API 对频率/增益非空和等长进行校验；新增十段频率必须精确采用 X4 配置 `[31,62,125,250,500,1000,2000,4000,8000,16000]`。

新建 action 1 的默认范围 **-6..+6 dB 有官方证据**：`Ab/Z$i.smali:81-157` 从新建回调构造 ta/b，`:115-126` 设置 +6/-6，传配置频率、用户增益，然后 action=1。更新继续采用该条目刚回读的 min/max；不能用默认范围覆盖其真实限制。

**选择现有自定义条目也用 action 2 的完整 0418 记录**：`Ab/Z.smali:548-583` 用户选中 CustomEqPreference 后获取其真实 EqInfo ID，再 `Lta/c.i(2,address,fullEntry)`。滑块更新 `Ab/Z$i.smali:248-358 → Ab/Z$l.smali:65-72` 也发送 action 2。因此 action 2 同时用于修改/选用，不存在据此证明的“只保存不选用”模式，不能发送 `0406[customId]`。

action 1 创建调用本身没有 selected 字段。官方界面创建链只证实创建请求，不能单靠静态代码证明固件一定自动选用；需要读取 0122 的 selected 确认。客户端要求创建后内容与 selected 同时匹配，否则未 verified 并停止，不应自动再发一个 SET 弥补。

已有设备 EQ 名称可与新建 UI 规则不同。根级本轮只读观测指出已有名称“哈曼特调 Sound by Harman”含空格；本 owner 未调用设备，该事实由根级证据负责。选用已有条目必须允许保留合法 UTF8、非控制字符和 wire 字节上限内的原始名称；不得按新建 alnum/CJK 输入规则拒绝。新建规则、用户改名规则与重发刚回读已有条目分离。

action 3 删除没有进入本轮 UI，未进一步扩展删除/OTA/录音/翻译/健康路径。

## 双连、游戏、空间与互斥

双连沿 `010D/0403` feature `11`；游戏沿 feature `06/28`，选择依据实际已回报 feature 与型号能力，不能把 06/28 都写一遍。严格状态只认可 0/1；未回报与未知原值保留未知。型号中的 `multiDevicesConnect:2` 是功能版本声明，不能显示为当前已开启。

三模式空间沿 `012A/0422`，模式 `0/1/2`；旧空间是 `010D/0403` feature `1B`，不能用旧开关表达头部跟踪。Hi-Res 是 feature `18`。X4 `spatialHiResMutex:1` 明确要求双向闭环：开启空间前关 Hi-Res；开启 Hi-Res 前关空间；每个前置写入必须匹配响应并回读，任一不确定则停止后续写，不自动补偿或重发。

`gameSoundMutexes:[1,3]` 指游戏音效与 EQ；没有空间项 2。游戏音效 `012B/0423` 是独立 type 状态，不得把它与游戏模式 feature06/28 混为一谈。本轮客户端若只提供游戏模式开关，不需要凭空联动空间或 EQ。

## 有界源码审阅与已反馈问题

审阅范围为完整 `src/HeyMelody.Daily/{DailyClient.cs,DailySession.cs,DailyRefresh.cs,DailyApply.cs,DailyParsers.cs}`，未审阅 UI/NativeConnector/发布程序/ARM64 native 运行；不声称整个客户端无缺陷。对持续变化中的文件以最后 JSON 的 SHA256 固定所读版本。

已立即反馈并核验修正：

1. P1：custom-select 和保存后激活使用 `0406[customId]`。当前 DailyApply 已改为 0418 action2 fullentry，并匹配 Selected+内容；不假设 customID 是 built-in preset。
2. P2：kind4 任意已知 ANC 位均被转换为智能。当前 DailyParsers 限定子 4/5/6/7，其余保留未知。
3. GET0122 的 `[1,5]` 与官方空负载不符，当前 DailyRefresh:113 已改为 `[]`。
4. 已有 EQ 名称含空格导致选用失败，由根级实际只读状态发现；当前 DailyApply:44 使用 preserveReportedName，:189-190 仅已有同名条目放宽；DailyParsers:106-118 仍校验非控制 UTF8/255 字节界限和十段数据。新增/改名独立验证。

设置主路径先获取新鲜身份+能力+状态，序列化执行；SET只提交一次，响应严格命令/序号匹配；空 ACK、超时、拒绝、取消或断连不自动重发。互斥前置写后检查身份/能力并保留部分效果，未知状态阻止继续。仅 GET 可重新建连重试。十段编辑数据在首个 await 前复制，避免滑块后续变化修改已计划提交数据。上述为所读源码的静态结论，本 owner 未运行 backend 测试或实际 SET。`tools/phase5_reference_checks.py` 仅运行 12 项离线资源/配置一致性断言，全部通过；它没有执行客户端，并在安全 JSON 中固定所读源码 SHA256。

非阻塞措辞建议：DailyApply:187 的“官方创建或更新命令同时选用”对 action1 过于肯定，应表述为本客户端仅提供“保存并选用”，并以 selected 回读确认；目前核心不自动补发 SET，因此未知行为不会被误报为 verified。

## 来源许可证与证据边界

官方 APK 是第三方逆向参考，本文只记录协议事实和简短标签，不再分发 APK。`reference/OppoPodsManager/LICENSE:1-6` 声明版权归 Zhaoyi-ya，并采用 GPL-3.0-or-later；若复制其实现而非独立实现，需要按对应许可证保留声明。本文没有复制该桌面实现到 backend。编解码 payload/模型是依据官方证据核对。
