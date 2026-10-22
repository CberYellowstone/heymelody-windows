# 后台能耗与 CPU（2026-10-22）

本文是 review/0.9.9 分支“后端能耗／CPU”方向的公开整理稿：问题、决定、已实施项、测试、Windows 待验证清单和顺带发现。完整记录在本机档案 [research/backend-energy-20261022/](../../research/backend-energy-20261022/README.md)（含基线与分步日志）；`research/` 通常不随 Git clone 提供，缺少它不影响阅读本文或正常构建。架构侧说明见[共享后台架构](../backend-architecture.md#资源占用)，其他方向的待办见[优化待办](../optimization-backlog.md)，前一轮见[后台内存审计](backend-memory-audit.md)。

产品版本不变（源码仍为 0.9.2），尚未发布；公共 IPC 仍为 schema 4，线格式字节不变。验证层级分为静态分析、模拟或离线测试、Linux 测量、Windows 原生或真机验证。**本轮只有前两级加 Linux 上的离线测试；没有 Linux 能耗测量，Windows 上的行为一律未验证。**

## 问题与范围

目标是降低后台在没有界面时的 CPU 与能耗：托盘进程及其 `--worker`、`--catalog-worker` 子进程在空闲时仍有定时唤醒、持续的 BLE 扫描和不必要的写盘。本轮只改后端（Host、Resources、Contracts、Client 的通信层）与测量脚本，不改设备能力门控、协议和设置写入规则。起点为提交 `53a436a`，本轮改动尚未提交。

## 决定

- 效率模式只在没有界面连接时生效，界面连接立即恢复，避免用户打开窗口时遇到被限速的后台。
- 优先级使用 IDLE。满载 CPU 时 IDLE 可能让 BLE 广播回调延迟数秒；用户 2026-10-22 决定现在保持 IDLE，开盖弹窗实现时再复核（弹窗前恢复默认，必要时改 BELOW_NORMAL 并保留 EcoQoS，登记为 PWR-01）。
- 后台开盖检测保持运行：只要有已配对且在白名单的耳机，扫描就继续，caseOpen 事件不变。
- 不使用 MMCSS；节流失败只记录到 Trace，不向用户显示，不计入后台健康。
- 其他方向的发现只登记在[优化待办](../optimization-backlog.md)，不顺手实施。

## 已实施

| 项 | 内容 | 验证层级 |
| --- | --- | --- |
| 效率模式 | `EnergyPolicy`／`EnergyController`／`WindowsProcessEnergy`：无界面时托盘与所有 worker 设 EcoQoS（ProcessPowerThrottling 的 EXECUTION_SPEED；Windows 11 22000 及以上另加 IGNORE_TIMER_RESOLUTION，被拒绝时回退）加 IDLE 优先级；启动后 15 秒宽限，最后一个界面断开后 10 秒延迟；界面连接立即恢复 Normal | 静态分析；模拟或离线测试（假时钟、假应用器）；Windows 未验证 |
| 界面计数 | 角色 main、manager、calibration 计入，preview 同样接受，observer 不计；已连接但尚未订阅的客户端按界面计 | 模拟或离线测试（真实命名管道，Linux） |
| 启动窗口 | 托盘打开主窗口前先恢复默认并保持 30 秒，再把新窗口设为 Normal（子进程会继承 IDLE 优先级类） | 静态分析；模拟或离线测试覆盖保持时长；Windows 未验证 |
| 试听豁免 | WASAPI 泵线程始终线程级关闭 EcoQoS | 静态分析；对应检查（空闲阻塞、豁免只应用一次、2 秒内释放）在 Linux 上提前返回，未执行；Windows 未验证 |
| 按需 BLE 扫描 | 两个过滤 Active watcher 只在存在已配对（Present）且在白名单（Allowed）的耳机时运行；Present 指仍在 Windows 已配对音频设备列表中，与连接无关；扫描参数不变 | 模拟或离线测试（`ScanDemandChanged` 在允许、移出、返回与解除配对时触发）；Windows 未验证 |
| 空闲唤醒 | 试听泵线程不播放时阻塞（原先首次预览后以 20 Hz 唤醒直到 worker 结束）；资源协调器睡到下一次检查到期（关闭在线资源时无计划；偏好变化、重新下载、新美术目标、释放、电脑从休眠恢复时唤醒；两轮至少间隔 30 秒；10 分钟／30 秒节奏不变）；BLE 广播过期每个 peer 一个可复用计时器 | 协调器与过期计时器：模拟或离线测试；试听泵线程：静态分析，Linux 上对应检查提前返回，Windows 未验证 |
| 写盘（IO-01） | 内容除 SampleTime 外未变且样本前进不足 60 秒时不写盘，内存保持最新；关闭时写入最新样本；崩溃后保存时间最多晚 60 秒（界面显示 HH:mm） | 模拟或离线测试 |
| IPC（CPU-01、CPU-02） | 每条连接一个带缓冲的 `RpcLineReader`，直接读管道或标准流的字节并增量解码（上限、EOF、'\r' 语义不变，旧静态读取已删除；审查修复后不再经 `StreamReader`，见下节）；每个管道事件序列化一次供所有客户端共用，按客户端的合并与限额不变 | 模拟或离线测试，线格式字节对照 |
| 测量脚本 | `tools/Measure-HostMemory.ps1` 另报每进程 CPU 用户／内核／合计毫秒、CPU 百分比、每秒上下文切换、priorityClass、powerThrottling（eco、default、custom、unknown）与 ignoreTimerResolution；Add-Type 编译一个小辅助类型到 `%TEMP%` | 静态分析；脚本未在 Windows 运行 |

## 测试

Linux 容器。基线为 `53a436a`（本轮开始前的工作树），Host 编译使用 `-p:EnableWindowsTargeting=true`。

| 检查 | 基线 | 本轮 |
| --- | --- | --- |
| Resources | 34 | 40 |
| Client | 173 | 181 |
| Host.Tests | 48 PASS，随后在需要 COM 的试听测试处停止（退出码 134） | 110 个 PASS 行，可在 Linux 完整运行；其中 2 项 Windows 专用检查（真实 WASAPI 播放器的致命初始化；试听线程空闲释放）提前返回，先打印 SKIP 再计为 PASS，实际执行 108 项 |
| Host 编译 | 0 warning / 0 error | 0 warning / 0 error |
| Daily、Devices、Control、Protocol、Calibration、DeviceManager、Calibration.UI、App | 177、21、19、77、55、8、13、129 | 改后全量重跑数量不变，全部通过 |

审查修复后 Resources 为 40、Client 为 181，Host.Tests 仍为 110 个 PASS 行与同样 2 项提前返回，Host 编译 0 警告 0 错误，worker 加载 Avalonia 为 0，App 129、DeviceManager 8 全部通过。

新增检查覆盖策略时序（宽限、延迟、启动保持）、控制器（立即应用、线程池上重应用、已退出进程跳过、失败只记录一次）、`POWER_THROTTLING_STATE` 为 12 字节、管道角色计数、写盘规则、单次序列化、扫描需求变化与过期计时器。提前返回的 2 项须在 Windows 补跑，不能算作已验证。

## 审查修复（同日，未提交）

对本轮未提交改动的审查发现，已全部修复；模拟或离线测试，Windows 未验证：

- **IPC 读取可能扣住整行。** `StreamReader.ReadAsync` 在上一次底层读取恰好填满字节缓冲、而请求的字符数尚未满足时会继续读取，所以数据恰好在一次满读处结束（多条消息共 4096 或 8192 字节，或含多字节字符）时，已在缓冲中的完整行要等无关数据到达才返回，worker 请求可能撞上 75／120 秒期限。`RpcLineReader` 现在以 `Stream` 构造并读字节：`Stream.ReadAsync` 一有数据即返回，用 UTF-8 `Decoder` 增量解码（无 BOM，所有写入端为 `UTF8Encoding(false)`；客户端读取保持严格解码，Host 与 worker 为替换解码），再按原字符级逻辑处理：'\r' 丢弃、每条记录至多 524288 个非 CR 字符且超出时立即失败（恰为上限后接 '\n' 仍接受）、干净 EOF 返回 null、记录中途 EOF 为 `Incomplete RPC record.`、进入时检查取消并在读取中响应、'\n' 之后的数据留在缓冲里。内存由字符上限限定，没有换行的流不会增长，也就无需另设字节上限。管道（客户端、`BackendPipeServer`）、worker 标准输入（`Console.OpenStandardInput()`）与 Host 侧的 `Process.StandardOutput.BaseStream`（`--worker`、`--catalog-worker`）均直接交给它，这些流上没有其他读取者。新增检查：多字节字符在任意位置被切开、CJK 与表情、无效 UTF-8 与一次性解码对照、字符（非字节）计数的上限、真实匿名管道上数据恰好 4096／8192 字节且写端保持沉默时仍返回整行。
- **进程句柄。** `WindowsProcessEnergy` 的 P/Invoke 改用 `SafeProcessHandle`（`process.SafeHandle`），所有者同时释放 `Process` 时不会留下已关闭或被复用的句柄值；`EnergyController` 的分离（`Detach.Dispose`）与应用共用 `_applySync`，释放会等待进行中的应用；锁序始终为 `_applySync` 然后 `_sync`，`_applySync` 下不会等待分离方，无环。
- **`PeerWorker` 的节能注册与 `Stop`。** 附加后用 `Interlocked.Exchange` 发布 `_energy` 再复查 `_stopped`；`Stop` 同样用 `Interlocked.Exchange` 取走，两侧至少一方看得见对方，且恰有一方释放。
- **`WindowsStateMonitor.OnScanDemand`。** 在 `UpdateRadio` 或读取开关状态失败的回退运行之前（`_radioChecked`）只记录 `_scanWanted`，不启动扫描，由 `UpdateRadio`／回退启动。
- **休眠后的资源检查。** `ResourceCoordinator.Reschedule()` 触发既有唤醒通道，`BackendSupervisor.SetSuspended(false)` 在恢复时调用；休眠期间到期的检查随即执行，仍受两轮至少 30 秒的下限约束，在线资源关闭时不做任何事。

## Windows 待验证清单

以下均未在 Windows 或真机验证，不能由 Linux 结果推断：

- 最后一个窗口关闭约 10 秒后，任务管理器显示“效率模式”；打开窗口后 1 秒内恢复。
- 从托盘打开的主窗口以 Normal 优先级运行。
- CPU 满载时试听播放不出现爆音或卡顿。
- 没有已配对且在白名单的耳机时扫描停止；允许一副耳机后，电量与开盖广播在数秒内恢复。
- 窗口全部关闭、后台仍在效率模式时，开盖检测仍触发 caseOpen。
- 用 `tools/Measure-HostMemory.ps1` 在同一台机器、同一连接状态下录制改前改后 JSON（含新字段），先关闭所有窗口至少 15 秒，再打开主窗口。
- Windows 10 上 IGNORE_TIMER_RESOLUTION 被拒绝时的回退。

## 顺带发现（已登记，未实施）

均为静态分析，登记来源为 2026-10-22 后端能耗（顺带发现），详见[优化待办](../optimization-backlog.md)：PWR-02 管道没有 subscribe 超时，已连接但沉默的客户端一直占用界面计数；IO-02 `DeviceStateStore.Load` 不预置写入节流状态，重启后第一次 `Remember` 总会重写；BUG-02 `ResourceCoordinator` 自动刷新循环遇到取消以外的异常会静默结束；CPU-08 `ClientBridge` 在 `StreamReader` 上再叠一层 `RpcLineReader` 缓冲（审查修复已随 `RpcLineReader` 改为读字节而一并解决）。PWR-01 为开盖弹窗实现时复核优先级。

## 上一轮状态

后端内存轮提交 `53a436a` 的 CI 全部成功：Windows x64 与 arm64 应用包（含 Verify-Release）以及 Windows、Ubuntu、macOS 的 Filament 运行时构建。用户 2026-10-22 在 Windows x64 真机上用该 CI 构建试用了大部分功能，结论为“功能上没问题”。该结论只覆盖这一范围，不是逐项验证，也不是内存或能耗测量。
