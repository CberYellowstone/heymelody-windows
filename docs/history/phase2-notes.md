# 阶段 2：协议基础与 X4 配置模型

历史记录：下文仅对应 2026-10-06 的 0.2.0 只读 CLI 和当时验证。当前入口见 [仓库说明](../../REPOSITORY.md)。文中的源码、研究输入和 artifacts 路径均相对于仓库根目录。

2026-10-06 完成。CLI 源码版本 0.2.0，输出 schemaVersion=2；运行入口仍为 `Run-ReadOnly.ps1`。阶段 1 的协议公开 API 和八个 GET 发送策略保留，历史实测证据不覆盖；回执结构升级为 schema 2，未发送请求的序号可为空。没有实施阶段 3 的参数设置、空间关闭、重连或音频验证。

## 协议基础

`HeyMelody.Protocol` 保留已有 AA/ULEB128 编解码和有界分片重组，新增 EOF 完整性检查。末尾存在半帧、未收完长度或未结束分片时，`PacketDecoder.Complete()` 抛固定 FormatException 并清理缓存。FSN 必须连续，不接受孤立末片、重复/跳序或超过 256 片的回绕。

`SequenceAllocator` 为每个控制会话分配 0..255，一次一个且线程安全；耗尽不复用。`ReadOnlySession` 的第 257 次请求返回 sequence-exhausted、不提交帧，并要求新会话。不可用、耗尽和未支持请求的序号为 null，避免记录伪造的序号 0。`RequestKey` 同时校验响应位、命令和当前序号，旧响应不能完成新请求。

69 位能力表来自 APK `p133i8/a.java`，与对应 smali 常量/数组构造逐项一致；空映射位置保留，重复命令去重。11 条基础命令独立加入。真实 16 字节位图 `FF775AEEEE8FA65F1700000000000000` 解析出 92 条命令声明，包含 0x0419。未知的第 69 位及之后置位只记录，不推断命令。

`supportedCommands` 保留阶段 1 的八个 GET 子集；新增 `allSupportedCommands`、完整映射范围、已知 bit 数和未知置位索引。集合包含 SET 只是协议能力 metadata，不扩展发送允许列表。每项 ACK 的 status、长度、计数和正文严格校验；功能值只有 0/1 转为 false/true，其他值保持原始值及 null 状态。

## X4 配置与来源

`HeyMelody.Devices` 新增 `X4Profile.Load()`、`DeviceModelResolver.FindByProductId()`、`CapabilityResolver.Resolve()` 及版本条件模型。仅精确匹配 06EC10，不使用名称/别名子串猜测产品。

完整资产 `Assets/oppo-enco-x4.json` 与 reference 证据包成员 `evidence/app_assets/builtin_x4_compatWhiteList.json` 逐字节一致：

```
95fcb16e291d5e8c3669745a20907d80c6f21a9d3982dcf1a22587e263dbf0db
```

加载嵌入资源时验证该 SHA256。原始 JSON 及子项字段保留，类型化投影使用只读集合和脱离 JsonDocument 的副本；本轮没有重新执行 APK 白名单 AES-GCM 解密，不把资产哈希当作厂商数字签名。

| 数据 | 保留方式 |
|---|---|
| 产品/别名、UUID、SPP | 原值及来源 provenance |
| EQ | 原 JSON 顺序 ID 0、7、2、1、3；modeType 42、43、28、46、29 分开保存 |
| ANC | 原父子 protocolIndex、modeType；12/13/14 子项不转换成猜测的普通模式 |
| 自定义 EQ | 31、62、125、250、500、1000、2000、4000、8000、16000 Hz |
| 空间 | 允许列表 0/1/2；新 012A/0422 与旧 feature 1B 分开 |
| 游戏模式 | 06/28 候选分开；无观测保持 Unobserved |
| 游戏音效 | 专用 012B/0423，类型 3/0；不是通用 feature 28 |
| 互斥 | spatialHiResMutex=1、gameSoundMutexes=1/3 原值；不凭空添加空间互斥 |
| 双连、多设备 | 原始标志 2、函数列表及版本条件 |
| 版本条件 | 来源字段、AndroidAppMarker/EarbudFirmware 域及 Pass/Fail/Conditional |
| 其他配置 | 完整 RawSource/RawFunctions，未投影字段没有丢弃 |

版本条件缺少上下文时不会猜测满足。Android 来源门槛 minVersion=16010000 不与本应用 0.2.0 或耳机固件 116 比较。当前只读工具不传额外版本上下文，正门槛可为 Conditional；零门槛与没有门槛为 Pass。

`FeatureCapability` 分开静态声明、查询命令支持、设置命令支持、原始状态、三态状态及来源条件。unknown profile 的 staticDeclared=null，命令存在不能升级为产品支持声明。

## 集成与本机实测

`Phase2ModelBuilder` 在本次身份验证及能力成功后生成 `deviceModel`。能力来自这条控制会话的 0100，状态投影仅采用本次显式请求的 feature 18；回包里的其他原值可保留在 query evidence，但不能使模型猜测游戏/空间状态。投影自身没有发送接口，settingsTransmissionEnabled=false。

实测文件：`artifacts/phase2-x4-complete.json`。

- 开始：2026-10-05 16:35:15.8301765 UTC / 北京时间 2026-10-06 00:35:15。
- schemaVersion=2、success=true、phase=complete；8 GET 均匹配本次命令/序号、status=0、布局校验通过。
- 产品 06EC10、SDP channel 15；控制 MTU 4600、Vendor1946；协议版本 raw010000、版本语义未确认。
- 固件按原三元组保留 1/2→116、2/2→116、3/2→100；codec raw8 不跨层命名。
- 原始位图 16 字节，完整能力 92 条，未知置位为空；HiRes=false、raw feature18=0。
- X4 配置精确匹配；EQ ID/10段频点完整；未请求的游戏模式为 Unobserved，其他状态为未知。
- 完整报告 56,966 字节，低于父进程 512 KiB 回执上限。
- 已提交记录仅八个 GET：七个空载荷与 010D 的 01 18，settingsCommandsSent=0，默认无蓝牙地址。

报告 SHA256：

```
2426b7f9ff9c218695f20edc7ae972f31271c65fe0173a4ebed076100ac590c2
```

发送记录经独立字节检查确认命令、序号、长度和固定载荷；这些是应用提交证据，不是独立 HCI 空口抓包。没有设置 Hi-Res、注册通知、宣告主机能力、配对操作或驱动写入，也没有声明当前采样率、实际码率、无损或延迟已验证。

## 验证

- 37 项 Protocol 离线测试：包括真实/互补 golden 能力位图、未知位、4096 次并发精确 256 个成功唯一序号、响应关联、独立 ACK/ULEB/三分片字节样本及每个截断前缀 EOF。
- 13 项 Devices 离线测试：完整资源/pin、ID不猜、EQ稀疏编号、ANC原值、不可变字段、版本域、独立读写门控、未知状态与专用空间/游戏通道。
- 23 项 Probe 会话/投影测试：只读允许列表、错误身份、空 ACK、状态/旧序号、257 次不复用、EOF、完整查询、投影零额外发送与未请求状态不进入模型。
- 共 73 项全部通过。Release 构建及 ARM64 Release 交叉构建 0 warnings / 0 errors；ARM64 真实蓝牙执行未验证。
- 有界静态审查提出的空间/游戏通道混用及投影范围问题已修复并回归；没有剩余阻塞。

参考 APK、固件、归档及阶段 1 证据没有被修改。下一阶段可在此模型上建立独立、明确授权的设置链及回读流程。
