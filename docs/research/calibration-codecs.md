# 贴合度、测听与耳扫实现记录

2026-10-08 的实现位于独立 [HeyMelody.Calibration](../../src/HeyMelody.Calibration/CalibrationEngine.cs) 模块，依赖 [CalibrationCodecs.cs](../../src/HeyMelody.Protocol/CalibrationCodecs.cs)。模块不引用 App、Host 或 Contracts，transport 由 Daily/Host 适配。以下是固定 APK 的静态协议核对与内存测试结果，没有真实设备测试。

## Transport 与状态

`ICalibrationTransport` 提供 `IsConnected`、`RequestAsync(command,payload,token)`、`Notification` 与 `Disconnected`。调用方提供 `CalibrationContext` 的 `StableKey`、连接 `Generation`、已报告 `Commands/Events` 和明确功能开关 `Fit/HearingV1/HearingV2/EarScan`，不能由型号名称授权写入。

`CalibrationSnapshot` 的 Revision/Generation、Workflow/Stage、Uid、Side、FrequencyType、StepIndex/StepCount、LevelIndex/Decibels、RawStatus、Message、FitResults、HearingLevels、ScanResult、Curves、SavedName 用于 UI 展示。频点 type `1..6` 是测听步骤/增强类型，原厂说明依次为力量感、浑厚感、澎湃感、节奏感、速度感、力度感；没有证据将其换算为 EQ 频率或 Hz。

主要 API 为 `RunFitAsync`、`StartEarScanAsync`、`BeginHearingAsync`、`AnswerAsync`、`SetLevelAsync`、`ContinueAsync`、`PreviousAsync`、`SaveHearingAsync`、`ApplyHistoryAsync`、`RestoreAsync`、`ReadCurvesAsync`、`CancelAsync` 与 `DisposeAsync`。等待结果不会占用输入 gate，取消可与等待中的测试并行。

## 固定 payload

协议字节不含 SPP 帧头。UID 是 Java signed int 的 BE32；长度、filter key 和 filter float 使用表内规定的 LE 编码。

| 命令 | payload / reply |
|---|---|
| `0405` 贴合度 | `[1]` 开始，`[0]` 停止 |
| `040D` 测听 | `[2,1]` 开始，`[2,0]` 停止 |
| `040D` 耳扫 | `[4,1,UID:BE32]` 开始，`[4,0,UID:BE32]` 停止 |
| `040E` 完整参数 | `[action,count,(side,type,db:i8)*count,UID:BE32,name:UTF8]` |
| `040E` 更新单步骤 | `[3,1,side,type,db:i8]`，此专用形式没有 UID/name |
| `0415` 保存耳扫 | `[3,len:LE16,scanBytes,UID:BE32]` |
| `0115` 当前测听 | 请求 `[]`；回包 `[status,action,count,(side,type,db:i8)*count,UID:BE32,name:UTF8]` |
| `011E` 当前耳扫 | 请求 `[]`；回包 `[status,action,len:LE16,scanBytes,UID:BE32]` |
| `0116` filter 请求 | `[count,(side,type,db:i8)*count,UID:BE32]` |
| `8116` filter 分包 | `[status,UID:BE32,total:u8,key:LE16,index:u8,enhanceType:u8,values:floatLE32*N]` |

`040E` 的完整参数 action 为 `1` 临时应用、`2` 停止临时应用、`3` 实际保存、`4` 暂停。完整临时/停止/暂停和单步骤更新不能混用布局。`0116` 除 SPP 自身分包还有应用分包；合并按 UID/key/total/enhanceType 校验，拒绝冲突重复、缺口和非有限 float，不能收到首个 ACK 就伪造完整曲线。

贴合度由 event `4` 返回左右结果 `(component,status)` 两组，component `1/2`，结果 `0` 一般、`1` 良好、`6` 较差。开始前以 `0107` 组件状态验证双耳入耳。界面 `SetCommandStatus` 的 `9/15/16` 是原始 ACK 状态，并未由 SDK 转译；实现保留 RawStatus 和拒绝结果。

耳扫由 event `14` 返回 `[action,len:LE16,scanBytes,UID:BE32]`，必须与本次 UID 匹配且 scanBytes 非空才完成。没有猜测固定“完成 action”来代替真实结果。现有 `0204` 与 `8202` 的事件封套分别去掉 event ID、以及成功 status/event ID 后再解码。

## 两种测听流程

每次依次完成左耳 type `1..6`、右耳 type `1..6` 共 12 步。

V1 严格沿用 OEM `classes2.dex` 的 `h0.onClick @ 3380E4`。初始 lower `-60`、upper `20`、current `0`：回答“听到”时移动上界并向 lower 下降；回答“未听到”时移动下界并向 upper 上升，半步采用原厂向上取整。负区间和非负区间的收敛阈值分别为 4 和 2，完成时选择该分支的 lower/upper。不能替换成通用二分搜索或自定义 1 dB 收敛。

固定回归轨迹：“听到”连续路径为 `-30,-45,-53,-57,-60` 后完成；“未听到”为 `10,15,18,20` 后完成；交替“未听到、听到、未听到、听到、未听到”为 `10,5,8,6,8` 后完成。Previous 返回旧步骤时重建原始搜索 bracket，不复用之后步骤残留状态。

V2 有 12 个离散等级 `[-55,-49,-41,-35,-25,-19,-11,-5,3,7,13,17]`，初始 index `5`（`-19 dB`）。拖动 300 ms 协调更新，释放立即更新；取消待发送任务不会继续提交旧滑块值。Continue/Previous 先获得暂停 ACK，再改变步骤和发送下一次频点。

当前资源入口根据明确函数选择流程；原厂主入口已证检查 `hearingEnhancement/hearingEnhancementNew`，版本二入口与 `hearingJudgeDb` 的全部型号路由仍属于适配审计项，不以字段名或当前常见设备代替完整 APK 路由证明。

## 保存、恢复与清理

历史是普通 JSON，按设备 stable key 隔离并保存 signed UID。名称 trim 后 2..40 字符、不含控制字符，重名比较忽略大小写；最多 10 项。名称冲突、容量与指定替换记录必须在任何设备写入前检查。不会静默 `Take(10)` 丢掉旧记录；替换需要显式 `allowReplacement` 和已存在记录 UID。

保存成功需要 `0115` 回读的 UID、名称和全部 12 个步骤与请求一致。带耳扫设备应用历史还必须具有真实 ScanData，不能生成空记录。Restore 只读取并验证当前 UID/扫描，不借“恢复”名义提交未知参数；filter 也只采纳匹配 UID 的完整曲线。

引擎在关闭、取消和断开连接时撤销本次任务并忽略旧 generation 的迟到结果。清理只针对本次拥有的临时配置；停止前读取当前 UID，如果手机已经换成另一 UID，则保留手机状态。明确拒绝的启动 ACK 不触发停止其他控制端的流程；启动 ACK 丢失则按所有权条件处理，不能一律认作没有开始。引擎不替用户切换 ANC、空间音效或黄金听感 feature `0B`。

现有 [Calibration.Tests](../../tests/HeyMelody.Calibration.Tests/Program.cs) 共 **18 项通过**，覆盖黄金 payload、严格解析、真实事件 UID、V1 分支、V2 Previous/暂停顺序、历史预检、容量/替换、手机改动保护、取消及断开。剩余验证包括各固件实机行为、欢律同构方法的逐项对照、额外入口版本路由，以及不同固件 filter 分包兼容性。
