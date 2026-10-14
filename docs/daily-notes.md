# 日常控制层（0.7.0 当前实现）

0.8.12 补充：日常事件与校准事件的注册告警分开处理；仅校准事件明确拒绝不再产生日常订阅提示，缺失或异常确认与真实日常故障仍提示。声明能力、真实注册集合及被动接收保持独立。完整说明见[通知提示修复](research/notification-header.md)，下文历史验证范围保持原值。

2026-10-10 补充：游戏音效与 Hi-Res／空间音效的当前行为已按[原 APK／固件复核](research/game-audio-spatial.md)纠正。本文修改前原稿已[逐字节归档](../research/game-audio-spatial-20261010/original-documents/docs/daily-notes.md)，历史阶段验证范围保持原值。

2026-10-14：所选来源的全部固件条件默认仅产生 warning，包括最低固件未满足或无法解释；App 条件、codec、命令能力和已知状态继续约束控制。原条件结果保留，实际权限通过 `EvaluateForControl`，EQ 使用对应的 `EvaluateConditionsForControl`。日常、EQ、ANC 与校准共用该策略；仅收集已实现路径涉及的固件条件，不能把 Air4 Pro 未实现的个人降噪最低 150 写成连接门槛。提示沿现有 DTO 显示在主界面右上角。

`0x0114` 的精确已知错长形状对所有已核实配置型号启用，不依赖 PID 或固件 125。无法识别的格式可在原两次读取尝试预算内以新会话跳过该查询，重新核实身份和能力；恢复会话保留跳过状态，显式重连重新查询。codec 保持未知，不以 warning 或恢复结果补造能力，不重放设置。详情见 [Air4 Pro 调查](research/enco-air4-pro-control.md#2026-10-14固件提示与编码查询恢复)。

实现位于 `src/HeyMelody.Daily/`，公开命名空间为 `HeyMelodyWindows.Daily`。库使用 .NET 10 BCL 和已有 Protocol、Devices、Control 接口，不增加第三方依赖、蓝牙扫描、驱动安装或系统设置；显式监听入口会按设备能力注册已核实通知。项目为 AnyCPU，Windows 原生传输和三个架构的应用发布由宿主集成。

`DailyClient(IControlConnector)` 的构造不连接设备。刷新和用户设置通过同一信号量串行执行，每个物理控制会话有独立的序号分配器和解码器。序号不会在一个连接内复用；余量不足时，下一轮读取先换连接。每次设置之前进行完整 GET，重新读取产品 ID、能力位图和日常状态，由所选来源中已核实配置、可编码路径及实际回报决定设置权限。当前 Avalonia 客户端的推送同步、手动读取和最多一次应用层重试见下文；阶段五原始验证记录仍保留在本文末尾。

## 公开接口

所有方法提供 `CancellationToken` 参数。刷新返回 `DailySnapshot`，设置返回 `DailyApplyResult`，类实现 `IAsyncDisposable`。

| 方法 | 行为 |
| --- | --- |
| `RefreshAsync(CancellationToken)` | 完整 GET 刷新；已开启监听时，换连接也会重新注册通知 |
| `RefreshAsync(DailyRefreshScope, CancellationToken)` | 指定分区 GET；在锁内合并缓存与较新的通知，保留未查询分区 |
| `StartMonitoringAsync(CancellationToken)` | 显式开启通知监听；先确认产品与能力，0200 查询后用 0205 或逐项 0201 注册 |
| `SnapshotChanged` / `DailySnapshot.Revision` | 发布分离的缓存快照及单调版本；通知接收独立于命令锁 |
| `ApplyAncModeAsync(byte primary)` | 校验已配置主模式，发送父编号的位图；回读按所属主模式匹配 |
| `ApplyAncAsync(byte primary, byte level)` | 校验原始父子编号，发送所选叶子编号的位图 |
| `ApplyPresetAsync(byte presetId)` | 按原始内置预设 ID 发送，保留非连续编号 |
| `SelectCustomEqualizerAsync(byte id)` | 使用刚回读的完整条目发送 0418/action2 |
| `SaveEqualizerAsync(EqualizerEdit)` | 新建 action1 / 更新 action2；回读完整内容和选中标志 |
| `SetDualConnectionAsync(bool)` | feature 11 |
| `SetGameModeAsync(bool)` | 只使用本次 GET 实际报告的 28 或 06 路由 |
| `SetSpatialModeAsync(byte)` | 优先 012A/0422；不足时用已报告的 legacy 1B |
| `SetHiResAsync(bool)` | feature 18 |

空间和 Hi-Res 两个方法保留 `allowMutexChange` 重载以兼容调用方；当前日常路径均只写用户请求的目标，不先关闭另一项、不要求另一项状态已知。各自的资源声明、版本、命令能力和目标状态仍须通过核实。`SpatialHiResMutex` 元数据保留，实际另一项状态由耳机读回决定。独立实验工具 `HeyMelody.Control/HiResWorkflow.cs` 保留其历史实验策略。

`DailyApplyResult.SubmissionAttempted` 表示 SET 曾进入传输提交步骤，含部分发送失败；`TargetSubmitted` 记录目标步骤。`PrerequisiteSubmitted` 保留字段兼容，当前日常路径不提交前置互斥步骤。它们都不证明设备已接收或设置已生效。`ResponseMatched` 表示响应命令、响应位和序号匹配。`AckAccepted` 使用 nullable bool，空响应或响应未获得为 null，明确非零状态为 false；`AckStatus` 保留目标 SET 的状态字节。

`ReadbackMatches` 为 true 表示最终目标匹配，为 false 表示已取得有效目标状态但不匹配，为 null 表示未取得可验证的目标回读。`ReadbackVerified` 等价于 `ReadbackMatches == true`。`Success` 要求已提交 ACK 为零且最终回读一致；当前状态已符合请求时，可以返回 Success=true、SubmissionAttempted=false。未知或拒绝的 ACK 不会因后来状态匹配而改成接受。PartialEffects 记录已观察到的效果及结果未完整确认的设置。自定义 EQ 保存路径可通过 `ObservedEqualizerId` 返回唯一完整内容匹配的设备条目 ID，未知 ACK 或未选中时也可能观察到该 ID。

每个协议请求使用四秒超时。一次读取遇到连接故障最多重新打开一次；新的会话重新验证身份。Daily 和 Host 的每次 Apply 都是单次尝试，目标 SET 最多提交一次，Hi-Res／空间不提交前置互斥 SET。库不会因超时、取消、断开、空响应或发送异常重放 SET，客户端桥接层也不重放 IPC。四秒期限不能保证同步 SDP 和异常内核传输在四秒内退出，宿主仍在独立工作进程中处理原生调用并施加总预算。

## 分区刷新、连接证据和客户端重试

未开启监听时，完整刷新与分区刷新只发 GET；开启监听后，新传输还会执行能力允许的通知重注册，不发送设置。分区入口从持锁时的最新缓存复制快照，而不是保存轮询开始时的旧快照；因此用户写入完成后，后续分区读取保留写入回读更新的其他字段。返回快照与缓存不共享可变集合。普通设置查询先清除本次字段，拒绝或解析失败使该字段未知、相关写入能力不可用，其他分区保留。0109 盒盖/放置按返回组件合并（0107实际是OTA能力）；未返回或读取失败的组件保留原值及旧观测时间，不伪装成新观测。身份查询或传输无法恢复时，整个可写快照失效。新传输即使只查电量，也先读取产品 ID 和能力；产品 ID 变化会清除旧缓存。

| `DailyRefreshScope` | 查询覆盖 |
| --- | --- |
| `Identity` | 产品 ID 0103、能力位图 0100 |
| `Features` | 功能列表 010D，以及可用时的现代空间 012A；否则依据实际 legacy 1B |
| `Anc` | 当前降噪 010C |
| `Equalizer` | 内置预设 010F、自定义列表 0122 |
| `Battery` | 电量 0106 |
| `Information` | 固件 0105、codec 0114，以及能力允许时的放置/盒盖 0109 |
| `Full` | 身份及以上全部日常状态；仍遵守设备报告的查询能力 |

`DailySnapshot.Connected=false` 可能只是控制读取失败，不能直接解释为 Windows 已断开耳机。原生 worker 内部的 NativeHeadsetService 映射仍会在读取失败时枚举精确地址，缺失或不唯一的结果保持 Unknown。0.7.0 对外由 BackendSupervisor 合并 OS 目录、radio 和挂起状态，发布 Connected、Disconnected 或 Unknown 的 Windows ConnectionState；ControlReady 独立表示控制连接、型号和能力是否可验证。Product ID 标识型号，物理目标由当前 Host 的不透明身份与精确地址绑定；重启后不会按相同名称转移待写入操作。

一次 SET 请求遇到明确 EOF、传输 IOException（不含 InvalidDataException）或已识别的 Winsock 断开错误，会保留 `FailureKind=Disconnected`；即使之后只读重连成功、After 已显示 Connected，也不会丢失这次连接中断。超时、空 ACK、格式异常和未识别的错误仍为 TransportUnknown。目标 ACK 接受但缺失回读为 ReadbackUnavailable，有效回读不匹配为 ReadbackMismatch，明确拒绝为 PeerRejected。Host 返回完整类型结果时才有 `BackendReceiptReceived=true`；Bridge 未取得结果或收到的结果没有此标记时，客户端不能据此确认成功或自动重试。

0.7.0 生产界面不再每八秒轮询。Host 启动持续接收并注册通知，手机或耳机状态变更直接更新缓存；仅含部分信息的通知合并为对应 scope 的补充读取，排在正在执行的设置事务之后。初始化、重新连接、手动刷新和设置前后验证仍读取耳机；生产界面的手动刷新是一个 Full 请求。旧非事件服务的分段调度仅作为测试兼容路径保留。

重试由 `HeyMelody.Client/MainViewModel.Actions.cs` 管理，Daily/Host/Bridge 没有隐藏重试。Windows 显式提交的目标在点击时固定，后台仍依据每次最新 GET 校验能力、条目和互斥条件；手机在提交前改变旧值不会仅因值不同触发冲突提示。第一次失败后，只有收到完整结果、同一设备仍可验证、动作可以安全重复且没有部分互斥操作时，才在重新完整读取后自动再尝试一次。若最新状态已符合目标，只结束操作并保留原始 ACK 事实，不再发送。

游戏音效开关与类型是上述重试策略的例外：每次界面操作最多一次 SET，明确失败只提示一次，不邀请立即重写；未知响应只用 GET 核对。匹配的观察值不会改写原 ACK 事实。失败后复用的控件恢复观察值，其他草稿与较新的编辑仍保留。Combo／Radio 按选项 ID 集合验证，不能把默认数值范围 `0..1` 用于拒绝合法的类型 `3` 或空间模式 `2`。

已知断连或无法确认结果/身份时第一次即提示失败，不再次写入。设备仍可验证连接、但动作不能安全自动重复时，显示重试/取消确认；确认后仍需完整读取并重新校验。新建 EQ 的未知结果需确认，先按原列表 ID 和完整内容核对新增条目；唯一匹配时复用实际 ID，歧义或名称冲突时不再创建。第二次尝试仍失败时提示后完整刷新，没有第三次尝试。取消重试会丢弃全部本地草稿和排队写入，再完整刷新设备信息。

协议没有状态版本、修改来源或 compare-and-set。回读只能说明观察到的状态，不能证明是哪台手机或客户端修改；本地预检也不能消除预检与 SET 之间的竞争。

2026-10-09的[校准修复](research/calibration-repair.md)区分了0200宣告与8205逐项注册确认，失败订阅不阻断被动校准结果。两类filter仅转发各自最新请求sequence的分片，保留真实响应sequence，避免重试时迟到包污染结果。校准期间保留唯一reader及最后8个清理序号，不因普通刷新主动换连接；已提交的校准wire事务等待有界ACK/超时，不因界面取消或滑块新值中断共享reader。

## 数据和协议依据

| 功能 | GET 请求 | SET 请求和回读 |
| --- | --- | --- |
| 电量 | 0106 / empty | status、count、component/raw 成对记录；只读取 |
| 降噪 | 010C / 01 01 | 0404 / 01 01 + 位图；主模式发送父 bit，子档发送子 bit |
| 内置 EQ | 010F / empty | 0406 / 原始预设 ID；回读该 ID |
| 自定义 EQ | 0122 / empty | 0418 / action + min + max + ID + UTF8 名长/名称 + 段数 + (频率 LE16,增益 S8) |
| 双连 | 010D 的明确 feature 列表 | 0403 / 11 + 0或1；回读 11 |
| 游戏 | 同上 | 0403 / 实际报告的 28 或 06 + 0或1 |
| 游戏音效开关 | 010D 的 feature 27 | 0403 / 27 + 0或1 |
| 游戏音效类型 | 012B / empty | 0423 / 合法类型 + 01；只核对类型读回 |
| 空间 | 012A / empty，或 feature 1B | 0422 / 0,1,2；legacy 只允许 0,1 |
| Hi-Res | feature 18 | 0403 / 18 + 0或1 |

功能列表 010D 查询明确请求 11、06、28、18、1B 五个编号，查询本身不注册通知；显式 StartMonitoringAsync 单独注册已核实通知，也不会根据静态功能标志自动发送设置。返回的未知布尔值保留原字节，状态为 null。现代游戏编号 28 只在响应确实出现时选用；出现但值未知时不会退回旧编号制造可用状态。

电量直接依据 APK `BatteryInfo.java:104-108` 和 `commands/e.smali:1828-1975` 的 component/raw 布局，低七位为等级，高位为充电。没有 count=4 特殊布局、无数量猜测或桌面兼容 fallback。缺失组件保持 null，所有组件编号和 raw 存在 RawComponents。等级 0 和大于 100 的值不显示成已知百分比。官方列表 UI `com/heytap/headset/component/mydevicelist/a.java:379-395` 仅显示大于零的值；`repository/earphone/Q.java:350-351` 在三组件全零时返回 null。因此这里保留 Raw=0 并显示未知，不推断物理不在位或精确耗尽。

降噪保留 Kind、ModeType、完整 RawMask 与原始 Primary/Level。APK `CurrentNoiseModeInfo.java:31-65` 定义 type1/type5 的位图，未知类型、多个置位或未知编号不猜选中模式。kind4 实时智能档只在 ANC 叶子 4/5/6/7 范围内归为智能切换，其余仍保留原始值并置 nullable 编号。父子配置保留 0/3 关闭、2/8 通透、1/(7,4,5,6) 智能/深/中/轻、11/(12,13,14) 更少噪音/标准/更多噪音。

主模式与子档操作独立：`ApplyAncModeAsync` 对父或同组子 bit 回读确认 Primary；`ApplyAncAsync` 仍要求精确子档。仅父 bit 被回读且该父项有 children 时 Level=null，不猜默认子档。UI 独立提供 `AncPrimary` 与 `CanSetAncMode`，纯父模式可以切换；具体子档和 `CanSetAnc` 仍以已核实子项及当前状态为准。两条操作共用 ANC 待确认分组。双 APK 的点击链、精确负载及未验证的提示音因果见 [ANC 主模式研究](research/anc-mode-prompt.md)。

内置 EQ 原顺序为 0 丹拿原声、7 丹拿高解析、2 纯享人声、1 清亮高音、3 澎湃低音。十段频率为 31、62、125、250、500、1000、2000、4000、8000、16000 Hz。自定义条目与内置预设属于不同 ID 空间；任何自定义选择都不会发 0406/customID。

官方 APK `Ab/Z.smali:548-583` 的已有条目选择路径使用 action2 和完整 EqInfo。`commands/m.smali:9177-9238` 的 0122 查询使用默认空 payload。`Ab/Z$i.smali:81-157` 的新建路径把最大/最小增益设为 +6/-6，因此空列表也可以创建新条目；这两个范围是已核实的官方创建策略，不是声称设备在空列表时报告过的范围。更新条目仍要求与新鲜回读的 min/max 一致。

既有 factory 名称允许包含空格和混合 UTF8，例如“哈曼特调 Sound by Harman”。选择使用完整回读名称；编辑未更名时也保留该名称，只校验 UTF8、无控制字符和编码长度不超过 255。新建和重命名使用当前客户端的字母/数字/中文输入策略，与参考桌面应用一致；这不代表耳机 wire 层只接受这些字符。

保存后只读完整条目内容、真实回读 ID 和 Selected。action1/action2 是否已选用以实际回读为准，不根据 ACK 或列表索引推测。内容匹配但未选中时，Daily 的本次尝试报告部分结果并停止，不追加 0406 或补发 SET；当前客户端可以按上述确认、核对流程进行唯一一次后续尝试。已有选中且完整内容相同的条目保存返回 no-op，不发送 SET。`EqualizerEdit.Activate=false` 明确拒绝提交，因为没有已核实的仅保存而不选用命令。

此实现根据协议结构自行编写，没有复制参考桌面控制类或其依赖。研究参考保留在 `reference/`，未经修改。OTA、录音、翻译、健康、取消配对、连接其他手机和设备优先级管理不在这个库的操作入口内。

## 验证范围

从当前仓库根目录执行：

```powershell
dotnet run --project tests/HeyMelody.Daily.Tests/HeyMelody.Daily.Tests.csproj
dotnet build src/HeyMelody.Daily/HeyMelody.Daily.csproj -c Release
```

历史 0.6.x：2026-10-06 刷新改进后，Daily 的 52 组内存传输场景已经运行通过，Host 构建 0 warnings / 0 errors。新增覆盖分区请求范围、最新缓存合并、返回快照隔离、局部状态失效和恢复、换连接与产品变化、缺失回读与不匹配的区别、目标/前置提交标记、EQ no-op 和观察到的 ID，以及 SET EOF/IO/socket 断开在 GET 重连后仍保留失败分类、超时/格式异常保持未知。这些结果不包含新的真实设备 SET 或 ARM64 实机执行。应用层重试和排队取消另由 `tests/HeyMelody.Client.Tests/` 验证，不能用 Daily 测试替代。

0.7.0 Daily 的 62 组内存传输测试通过，新增通知注册、响应同批通知保留、乱序 GET/推送合并、订阅恢复和未知能力路由检查。盒盖、左右耳放置及三组件电量分别保留观测时间；其他 GET 不刷新旧状态时间。共享 Host 对外的 Windows ConnectionState 来自 OS 目录、radio 与挂起状态，ControlReady 独立表示当前核验的控制能力；不能把 BLE 观测或缓存名称视为可写证据。详细来源见 [蓝牙事件研究](research/bluetooth-events.md)，多设备与 BLE 融合见 [后台架构](backend-architecture.md)。

以下为阶段五原始验证记录，时间、场景数和真实设备样本没有更新为本轮结果：

截至 2026-10-06 02:47 UTC，38 组离线场景全部通过，Release 构建 0 warnings / 0 errors。测试包括电量数量/重复/状态校验、ANC 原位图和智能档反例、EQ 完整截断扫描/UTF8/增益/重复 ID、独立十段和 factory 名称 wire golden、GET0122 空请求、现代/旧游戏与空间路由、双向互斥同意门、前置后能力变化、部分发送/空 ACK/明确拒绝不重发、实际分配 ID、保存但未选中、不支持的 save-only、序号耗尽和并发串行。

这些测试全部使用内存传输，不接触本机设备。Root 的真实 x64 只读报告 `artifacts/phase5-live-readonly-x64-first.json` 记录左右电量 90%、深度降噪、内置预设 2、双连开启、游戏关闭、Hi-Res 开启、空间关闭，以及所有日常控制能力可读；它证明当时的读取和 Host 映射成功，不证明这些 SET 在耳机上已经实测。ARM64 真实设备执行和 x86 原生验证由整体交付另行记录。
