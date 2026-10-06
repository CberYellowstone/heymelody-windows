# 阶段五日常控制层

实现位于 `src/HeyMelody.Daily/`，公开命名空间为 `HeyMelodyWindows.Daily`。库使用 .NET 10 BCL 和已有 Protocol、Devices、Control 接口，不增加第三方依赖、蓝牙扫描、通知注册、驱动安装或系统设置。项目为 AnyCPU，Windows 原生传输和三个架构的应用发布由宿主集成。

`DailyClient(IControlConnector)` 的构造不连接设备。刷新和用户设置通过同一信号量串行执行，每个物理控制会话有独立的序号分配器和解码器。序号不会在一个连接内复用；余量不足时，下一轮读取先换连接。每次设置之前重新读取产品 ID、能力位图和相关状态，仅已核实的 06EC10 / OPPO Enco X4 配置允许设置。

## 公开接口

所有方法提供 `CancellationToken` 参数。刷新返回 `DailySnapshot`，设置返回 `DailyApplyResult`，类实现 `IAsyncDisposable`。

| 方法 | 行为 |
| --- | --- |
| `RefreshAsync` | 仅 GET，生成新快照，失败字段不沿用旧值 |
| `ApplyAncAsync(byte primary, byte level)` | 校验原始父子编号，发送所选叶子编号的位图 |
| `ApplyPresetAsync(byte presetId)` | 按原始内置预设 ID 发送，保留非连续编号 |
| `SelectCustomEqualizerAsync(byte id)` | 使用刚回读的完整条目发送 0418/action2 |
| `SaveEqualizerAsync(EqualizerEdit)` | 新建 action1 / 更新 action2；回读完整内容和选中标志 |
| `SetDualConnectionAsync(bool)` | feature 11 |
| `SetGameModeAsync(bool)` | 只使用本次 GET 实际报告的 28 或 06 路由 |
| `SetSpatialModeAsync(byte)` | 优先 012A/0422；不足时用已报告的 legacy 1B |
| `SetHiResAsync(bool)` | feature 18 |

空间和 Hi-Res 两个方法还有显式 `allowMutexChange` 重载。省略这个参数等于 false。新鲜状态显示开启目标需要先关闭互斥项时，默认不提交任何 SET。获得允许后，先提交关闭互斥项、匹配 ACK 并回读，再核对最新能力与状态，最后提交目标。前置操作没有完整确认就停止；不会进行补偿写入或自动重发。

`DailyApplyResult.SubmissionAttempted` 表示本次任一 SET 曾进入传输提交步骤，含前置互斥操作和部分发送失败。`ResponseMatched` 表示全部已提交步骤的响应命令、响应位和序号匹配。`AckAccepted` 使用 nullable bool，空响应或响应未获得为 null，明确非零状态为 false。`ReadbackVerified` 只描述最终目标的回读。`Success` 要求所有已提交 ACK 为零且最终回读一致；当前状态已符合请求时，可以返回 Success=true、SubmissionAttempted=false。PartialEffects 包括确认完成的前置操作，也会明确记录已经提交但未完整确认的前置请求。

每个请求使用四秒超时。GET 连接故障最多重新打开一次；新的会话重新验证身份。SET 的提交标记在调用传输之前建立，超时、取消、断开、空响应和发送异常之后均不重发。该超时不能保证同步 SDP 和异常内核传输在四秒内退出，宿主应保持 UI 响应并对工作进程设总预算。

## 数据和协议依据

| 功能 | GET 请求 | SET 请求和回读 |
| --- | --- | --- |
| 电量 | 0106 / empty | status、count、component/raw 成对记录；只读取 |
| 降噪 | 010C / 01 01 | 0404 / 01 01 + leaf 位图；回读原始模式位 |
| 内置 EQ | 010F / empty | 0406 / 原始预设 ID；回读该 ID |
| 自定义 EQ | 0122 / empty | 0418 / action + min + max + ID + UTF8 名长/名称 + 段数 + (频率 LE16,增益 S8) |
| 双连 | 010D 的明确 feature 列表 | 0403 / 11 + 0或1；回读 11 |
| 游戏 | 同上 | 0403 / 实际报告的 28 或 06 + 0或1 |
| 空间 | 012A / empty，或 feature 1B | 0422 / 0,1,2；legacy 只允许 0,1 |
| Hi-Res | feature 18 | 0403 / 18 + 0或1 |

功能列表查询明确请求 11、06、28、18、1B 五个编号，不注册通知，也不会根据静态功能标志自动发送设置。返回的未知布尔值保留原字节，状态为 null。现代游戏编号 28 只在响应确实出现时选用；出现但值未知时不会退回旧编号制造可用状态。

电量直接依据 APK `BatteryInfo.java:104-108` 和 `commands/e.smali:1828-1975` 的 component/raw 布局，低七位为等级，高位为充电。没有 count=4 特殊布局、无数量猜测或桌面兼容 fallback。缺失组件保持 null，所有组件编号和 raw 存在 RawComponents。等级 0 和大于 100 的值不显示成已知百分比。官方列表 UI `com/heytap/headset/component/mydevicelist/a.java:379-395` 仅显示大于零的值；`repository/earphone/Q.java:350-351` 在三组件全零时返回 null。因此这里保留 Raw=0 并显示未知，不推断物理不在位或精确耗尽。

降噪保留 Kind、ModeType、完整 RawMask 与原始 Primary/Level。APK `CurrentNoiseModeInfo.java:31-65` 定义 type1/type5 的位图，未知类型、多个置位或未知编号不猜选中模式。kind4 实时智能档只在 ANC 叶子 4/5/6/7 范围内归为智能切换，其余仍保留原始值并置 nullable 编号。父子配置保留 0/3 关闭、2/8 通透、1/(7,4,5,6) 智能/深/中/轻、11/(12,13,14) 更少噪音/标准/更多噪音。

内置 EQ 原顺序为 0 丹拿原声、7 丹拿高解析、2 纯享人声、1 清亮高音、3 澎湃低音。十段频率为 31、62、125、250、500、1000、2000、4000、8000、16000 Hz。自定义条目与内置预设属于不同 ID 空间；任何自定义选择都不会发 0406/customID。

官方 APK `Ab/Z.smali:548-583` 的已有条目选择路径使用 action2 和完整 EqInfo。`commands/m.smali:9177-9238` 的 0122 查询使用默认空 payload。`Ab/Z$i.smali:81-157` 的新建路径把最大/最小增益设为 +6/-6，因此空列表也可以创建新条目；这两个范围是已核实的官方创建策略，不是声称设备在空列表时报告过的范围。更新条目仍要求与新鲜回读的 min/max 一致。

既有 factory 名称允许包含空格和混合 UTF8，例如“哈曼特调 Sound by Harman”。选择使用完整回读名称；编辑未更名时也保留该名称，只校验 UTF8、无控制字符和编码长度不超过 255。新建和重命名使用当前客户端的字母/数字/中文输入策略，与参考桌面应用一致；这不代表耳机 wire 层只接受这些字符。

保存后只读完整条目内容、真实回读 ID 和 Selected。action1/action2 是否已选用以实际回读为准，不根据 ACK 或列表索引推测。内容匹配但未选中时报告部分结果并停止，绝不追加 0406 或盲目补发另一条 SET。`EqualizerEdit.Activate=false` 明确拒绝提交，因为本轮没有已核实的仅保存而不选用命令。

此实现根据协议结构自行编写，没有复制参考桌面控制类或其依赖。研究参考保留在 `reference/`，未经修改。OTA、录音、翻译、健康、取消配对、连接其他手机和设备优先级管理不在这个库的操作入口内。

## 验证范围

执行：

```powershell
dotnet run --project HeyMelodyWindows/tests/HeyMelody.Daily.Tests/HeyMelody.Daily.Tests.csproj
dotnet build HeyMelodyWindows/src/HeyMelody.Daily/HeyMelody.Daily.csproj -c Release
```

截至 2026-10-06 02:47 UTC，38 组离线场景全部通过，Release 构建 0 warnings / 0 errors。测试包括电量数量/重复/状态校验、ANC 原位图和智能档反例、EQ 完整截断扫描/UTF8/增益/重复 ID、独立十段和 factory 名称 wire golden、GET0122 空请求、现代/旧游戏与空间路由、双向互斥同意门、前置后能力变化、部分发送/空 ACK/明确拒绝不重发、实际分配 ID、保存但未选中、不支持的 save-only、序号耗尽和并发串行。

这些测试全部使用内存传输，不接触本机设备。Root 的真实 x64 只读报告 `artifacts/phase5-live-readonly-x64-first.json` 记录左右电量 90%、深度降噪、内置预设 2、双连开启、游戏关闭、Hi-Res 开启、空间关闭，以及所有日常控制能力可读；它证明当时的读取和 Host 映射成功，不证明这些 SET 在耳机上已经实测。ARM64 真实设备执行和 x86 原生验证由整体交付另行记录。
