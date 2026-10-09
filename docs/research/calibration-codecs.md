# 贴合度、测听与耳扫旧实现记录（修复前）

**2026-10-09 状态更新：** 本文保留修复前0.8.8实现及其纠错记录，下文“当前/现有”均指该历史基线。后续已将研究用于源码修复，当前实现与验证见 [组件修复](calibration-repair.md)；协议依据见 [重新研究摘要](calibration-reassessment.md) 和 [完整过程与结果](../../research/calibration-reassessment-20261009/README.md)。旧模拟基线不能继续作为原厂协议完整证明。

2026-10-08 的实现位于独立 [HeyMelody.Calibration](../../src/HeyMelody.Calibration/CalibrationEngine.cs) 模块，依赖 [CalibrationCodecs.cs](../../src/HeyMelody.Protocol/CalibrationCodecs.cs)。模块不引用 App、Host 或 Contracts，transport 由 Daily/Host 适配。以下保留旧实现记录，并按此次重新研究标出差异；旧静态核对与内存测试没有真实设备测试。

## Transport 与状态

`ICalibrationTransport` 提供 `IsConnected`、`RequestAsync(command,payload,token)`、`Notification` 与 `Disconnected`。调用方提供 `CalibrationDeviceContext` 的 `StableKey`、连接 `Generation`、已报告 `Commands/Events` 和明确功能开关 `Fit/HearingV1/HearingV2/EarScan`，不能由型号名称授权写入。

`CalibrationSnapshot` 的 Revision/Generation、Workflow/Stage、Uid、Side、FrequencyType、StepIndex/StepCount、LevelIndex/Decibels、RawStatus、Message、FitResults、HearingLevels、ScanResult、Curves、SavedName 用于 UI 展示。频点type `1..6` 在X4 v116的测试音控制和波表路径对应500、1000、2000、4000、6000、8000 Hz。旧文案“力量感”等属于结果描述，不能作为频点定义；此Hz映射尚不外推到其他固件。

主要 API 为 `RunFitAsync`、`StartEarScanAsync`、`BeginHearingAsync`、`AnswerAsync`、`SetLevelAsync`、`ContinueAsync`、`PreviousAsync`、`SaveHearingAsync`、`ApplyHistoryAsync`、`RestoreAsync`、`ReadCurvesAsync`、`CancelAsync` 与 `DisposeAsync`。等待结果不会占用输入 gate，取消可与等待中的测试并行。

## 原厂 payload 与当前差异

协议字节不含 SPP 帧头。UID 是 Java signed int 的 BE32；长度、浮点样本总数、采样率和 filter float 使用表内规定的 LE 编码。filter key 来自外层请求关联，不在本文的 filter payload 中。

| 命令 | payload / reply |
|---|---|
| `0405` 贴合度 | `[1]` 开始，`[0]` 停止 |
| `040D` 测听 | `[2,1]` 开始，`[2,0]` 停止 |
| `040D` 耳扫 | `[4,1,UID:BE32]` 开始，`[4,0,UID:BE32]` 停止 |
| `040E` 试听/保存 | action1/3：`[action,count,(side,type,db:i8)*count,UID:BE32,name:UTF8]`；正常保存count12 |
| `040E` 停试听/暂停 | `[02]` / `[04]`；现有encoder错误地附加count与UID |
| `040E` 更新单步骤 | `[3,1,side,type,db:i8]`，此专用形式没有 UID/name |
| `0415` 保存耳扫 | `[3,len:LE16,scanBytes,UID:BE32]` |
| `0115` 当前测听 | 请求 `[]`；回包 `[status,action,count,(side,type,db:i8)*count,UID:BE32,name:UTF8]` |
| `011E` 当前耳扫 | 请求 `[]`；回包 `[status,action,len:LE16,scanBytes,UID:BE32]` |
| `0116` filter 请求 | `[count,(side,type,db:i8)*count,UID:BE32]` |
| `8116` 测听filter | `[status,UID:BE32,deviceType:u8,totalSamples:LE16,packetId:u8,enhanceType:u8,values:floatLE32*N]` |
| `011F` 耳扫filter请求 | `[scanLen:LE16,scanBytes,UID:BE32]`；当前尚未实现 |
| `811F` 耳扫filter | `[status,UID:BE32,deviceType:u8,samplingFrequency:LE16,totalSamples:LE16,packetId:u8,values:floatLE32*N]` |

`040E` action1是试听、2停试听、3按count1单频/count12保存分流、4暂停。只有action1/3完整参数包含count与UID，action2/4原厂为单字节。X4当前六字节action2通过前置检查后会额外清零工作RAM的item_count，不能笼统称附加字节无效。`0116`的总数是浮点样本数、不是包数，key不在payload；当前`HearingFilterAssembler`按包数完成的逻辑需要纠正。`011F`是独立耳扫数据，不套测听header。完整结构见最新协议表。

贴合度由 event `4` 返回左右结果 `(component,status)` 两组，component `1/2`，结果 `0` 一般、`1` 良好、`6` 较差。当前实现使用`0107`验证佩戴是错误：该命令查询OTA能力，正确佩戴查询为`0109`，详见[原DEX补充](../../research/calibration-reassessment-20261009/status-query-supplement.md)。界面 `SetCommandStatus` 的 `9/15/16` 是原始 ACK 状态，并未由 SDK 转译；实现保留 RawStatus 和拒绝结果。

耳扫由 event `14` 返回 `[action,len:LE16,scanBytes,UID:BE32]`，必须与本次 UID 匹配且 scanBytes 非空才完成；原厂 UI 还核对设备地址。X4常规完成 action3，但原厂 UI 没有以 action3 代替匹配和非空检查。现有 `0204` 与 `8202` 的耳扫事件封套分别去掉 event ID、以及成功 status/event ID 后再解码；filter event9存在不同偏移边界，见最新协议表。

## 两种测听流程

每次依次完成左耳 type `1..6`、右耳 type `1..6` 共 12 步。

V1 严格沿用 OEM `classes2.dex` 的 `h0.onClick @ 3380E4`。初始 lower `-60`、upper `20`、current `0`：回答“听到”时移动上界并向 lower 下降；回答“未听到”时移动下界并向 upper 上升，半步采用原厂向上取整。负区间和非负区间的收敛阈值分别为 4 和 2，完成时选择该分支的 lower/upper。不能替换成通用二分搜索或自定义 1 dB 收敛。

固定回归轨迹：“听到”连续路径为 `-30,-45,-53,-57,-60` 后完成；“未听到”为 `10,15,18,20` 后完成；交替“未听到、听到、未听到、听到、未听到”为 `10,5,8,6,8` 后完成。Previous 返回旧步骤时重建原始搜索 bracket，不复用之后步骤残留状态。

V2 有 12 个离散等级 `[-55,-49,-41,-35,-25,-19,-11,-5,3,7,13,17]`，初始 index `5`（`-19 dB`）。拖动 300 ms 协调更新，释放立即更新；取消待发送任务不会继续提交旧滑块值。Continue/Previous 先获得暂停 ACK，再改变步骤和发送下一次频点。

两原APK的主入口和Fragment路由已重新核对：`hearingJudgeDb`只控制应用记录时左右阈值差异提示，滑块从`earScan`流程进入；无耳扫的起始wrapper在start成功后切旧yes/no。当前Windows以JudgeDb选择V1/V2需要纠正。原厂耳扫结果到达后先停止扫描并等ACK，再开始测听；当前引擎也遗漏此阶段。

## 保存、恢复与清理

Windows历史是普通JSON，按设备stable key隔离并保存signed UID；这是本项目设计，原厂历史在手机数据库。Windows名称trim后2..40字符、不含控制字符，最多10项，并预检名称/容量/指定替换。X4固件另要求1..39个UTF-8名字字节，当前字符限制未覆盖此边界。APK的2..40字符规则用于本地recordName，wire Entity.name通常是另一日期字段，不能直接当成同一命名契约。APK/Windows本地历史存在不等于耳机方案已生效。

保存成功需要 `0115` 回读的 UID、名称和全部 12 个步骤与请求一致。带耳扫设备应用历史还必须具有真实 ScanData，不能生成空记录。Restore 只读取并验证当前 UID/扫描，不借“恢复”名义提交未知参数；filter 也只采纳匹配 UID 的完整曲线。

以上是当前Windows回读策略。原厂保存链还先发`0411`描述，再`040E action3`，成功后`0415 action3`，最后开feature0B并同步；当前保存顺序相反、无描述步骤。结果页的原音/增强试听与两类filter展示尚未对齐。

引擎在关闭、取消和断开连接时撤销本次任务并忽略旧 generation 的迟到结果。清理只针对本次拥有的临时配置；停止前读取当前 UID，如果手机已经换成另一 UID，则保留手机状态。明确拒绝的启动 ACK 不触发停止其他控制端的流程；启动 ACK 丢失则按所有权条件处理，不能一律认作没有开始。引擎不替用户切换 ANC、空间音效或黄金听感 feature `0B`。

现有 [Calibration.Tests](../../tests/HeyMelody.Calibration.Tests/Program.cs) 的**18项通过**是2026-10-08旧模拟基线；其中filter布局和停止字节等不能作为原厂正确性的证据。本轮完成两APK独立复核，未修改或运行这些测试。下一轮应先修正codec与完整流程，再做原厂抓包/真机对照与相应验证。
