# 阶段五集成有界审阅

本文保存 0.5.0 原始审阅及 0.6.x 后续说明，不覆盖 0.7.0 共享托盘后台。当前行为见 [后台架构](../backend-architecture.md) 与 [日常控制层](../daily-notes.md)。文中的源码、研究输入和 artifacts 路径均相对于仓库根目录。

## 历史记录与 0.6.x 后续说明

下文从“审阅时间”开始的内容是 2026-10-06 02:58 UTC 对当时 WPF/0.5.0 集成的原始有界审阅。文件位置、行号、939 行读取范围和“未运行测试”等声明仅属于那次审阅；旧 `HeyMelody.App/MainWindow.xaml.cs`、App 内 Bridge/Contracts 不代表当前 Avalonia 文件布局，原审阅结论也不覆盖后来改动。

2026-10-06 的 0.6.x Avalonia 实现对应 `src/HeyMelody.Client/{MainViewModel.cs,MainViewModel.Actions.cs,OperationCoordinator.cs,ClientBridge.cs}`、`src/HeyMelody.Contracts/{UiContracts.cs,RpcWire.cs}`、`src/HeyMelody.App/` 与 `src/HeyMelody.Host/`。本次文档核对读取了当前 Daily/Host、Bridge 和客户端调度/操作路径；没有因此重新执行原审阅、构建或设备调用。当前行为详见 [日常控制层](../daily-notes.md)，当前发布与验证入口见 [README.md](../../README.md)。

- 自动轮询仍以八秒为触发间隔，但分成身份/能力、功能/空间、降噪、EQ、电量和设备信息六个通信边界。通信仍串行；生命周期和用户操作优先于下一个后台分区。未查询分区从持锁时的最新缓存合并，因此恢复轮询不会覆盖刚完成的 SET 回读。普通分区字段失败只关闭对应能力，身份或传输无法恢复时失效整个可写快照。
- 后台刷新不再全局禁用控件；同一设置组等待写入，其他设置及本地 EQ 编辑可继续。草稿版本和会话 epoch 防止旧结果覆盖后来编辑或另一设备。取消重试会清除全部草稿及排队写入，并完整刷新。
- Daily、Host 每个 Apply 只执行一次目标 SET（显式互斥同意时可包含一个前置 SET），Bridge 不自动重放 IPC。当前客户端则允许最多一次后续尝试：完整结果、同一设备和最新能力可验证且动作可安全重复时自动进行；新建 EQ 不确定或部分互斥等情况先确认。明确断连、缺完整结果或身份不可验证时首次即提示失败，不再次写入；第二次失败后提示并完整刷新。新 EQ 先核对唯一完整内容匹配的新增条目，复用回读 ID，拒绝歧义创建。
- `BackendReceiptReceived`、目标/前置提交、目标 ACK 状态、nullable 回读匹配及 `FailureKind` 分开保留。ACK 未知或明确拒绝不能被状态匹配改写成接受；当前目标已匹配时可以结束操作而不再写入。明确 SET EOF/IO/socket 断开保留 Disconnected，即使只读重连后的 After 已 Connected；超时和格式错仍为未知。
- Host 在读取失败时重新枚举所选精确地址，区分 Windows 连接证据与控制状态可用性。缺失或不唯一的枚举结果保持 Unknown。Host 重启会生成新的不透明身份；旧操作不会按设备名称或型号转移。协议没有修改来源、状态版本或 compare-and-set，不能把回读差异直接归因于手机。

## 原始审阅记录

审阅时间：2026-10-06 02:58 UTC。根任务提供的运行时边界为 gpt-6.1-sol/max；本审阅子代理实际查询为 gpt-6.1-sol/high，未委派。

在下面实际读取的范围内，未发现需要报告的 P1/P2 功能问题或阻塞。结论来自静态代码审阅和发布目录文件名、大小及修改时间枚举；不代表真实设备控制或 ARM64 实机验证已经通过。

## 核对结果

- **后台隔离及发布路径一致。** `src/HeyMelody.App/ClientBridge.cs:113-129` 正式启动 `backend/HeyMelody.Host.exe`；`Build-Client.ps1:24-38` 在每个架构目录分别发布 GUI 与后台，各自携带依赖，不再合并 WPF 与 Console 的 `WindowsBase.dll`。Debug/Release DLL 回退仅在正式后台文件不存在时使用。发布目录枚举已看到 x64、x86、arm64 各自的 GUI EXE、backend EXE、runtimeconfig、coreclr、hostfxr、hostpolicy，以及分开的 WindowsBase 文件。这里仅确认布局与代码一致，没有执行二进制或核验其 PE 架构。
- **未知设置结果不自动重发。** `ClientBridge.cs:43-61,64-100` 对一次请求只写入一次 IPC 记录；失联、超时或格式错误会给出保守的未知回执，并在相应异常路径结束后台，不重新提交 action。`MainWindow.xaml.cs:225-237` 更新状态或清除快照，没有补发设置的分支。IPC 已提交仅说明可能进入后台，不被当作已发送到耳机或已生效的证明。
- **请求与回执有编号绑定，阻塞不占用 UI 线程。** `ClientBridge.cs:64-89` 串行处理请求并核对 schemaVersion/id；`RpcWire.cs:25-49` 及 Bridge 读取端限制记录长度；`Host/Program.cs:16-46` 要求请求编号递增并串行处理。Native SDP 在独立进程内运行，父进程对请求施加时限。审阅没有把工作进程退出或 IPC 成功当作耳机接受的证明。
- **设备身份和 UI 选择没有混用名称定位写入。** `Host/NativeHeadsetService.cs:20-45` 将内存 GUID 映射到枚举出的固定地址，并构造固定 peer connector；`:198-211` 每次建立原生连接重新检查精确地址、配对及连接状态和唯一服务。`MainWindow.xaml.cs:92-105,108-115,135-139,193-216,233` 在切换时清除会话/快照，应用前要求 snapshot、session、selection 身份一致，只接收当前会话对应的回读。
- **成功、无操作、部分提交和未知结果的映射一致。** `NativeHeadsetService.cs:99-123` 将 ACK、回读、是否提交分别映射；PartialEffects 仅在失败且有部分操作记录时为 true。`MainWindow.xaml.cs:231-235` 允许未提交且回读匹配的 no-op 成功，即使 Accepted=false 也不会误报失败；未知 ACK 或部分操作不被显示为完整成功。当前文案对部分操作使用“已提交”，没有声称已生效。
- **界面读取失败会禁写，未保存 EQ 不会被轮询覆盖。** `MainWindow.xaml.cs:143-164,176-211` 对失效快照清除状态并关闭写控件；正常刷新在 EQ dirty 时保留本地编辑。切换设备、均衡器或关闭窗口具有编辑丢弃确认。Hi-Res/空间互斥确认通过 `AllowMutexChange` 显式传给后台（`:214-224`；`NativeHeadsetService.cs:91-96`）。本审阅没有重新审阅 Daily 的协议或 fresh gate 实现，沿用该模块已冻结的独立审阅与测试结论。

## 实际读取范围和限制

完整读取以下七个文件，共 939 行（含空行），以本轮读取时版本为准：

| 文件 | 已读行 |
| --- | --- |
| src/HeyMelody.App/ClientBridge.cs | 1–184 |
| src/HeyMelody.App/MainWindow.xaml.cs | 1–298 |
| src/HeyMelody.App/UiContracts.cs | 1–74 |
| src/HeyMelody.Host/NativeHeadsetService.cs | 1–223 |
| src/HeyMelody.Host/Program.cs | 1–53 |
| src/HeyMelody.Host/RpcWire.cs | 1–52 |
| Build-Client.ps1 | 1–55 |

另仅枚举 `release` 内指定 EXE/runtimeconfig/runtime DLL/manifest/启动脚本的文件名、大小和时间，包含当前 0.5.0 stage 与已有失败/构建目录；没有读取启动脚本内容、manifest 内容或 EXE 资源。因此本报告不核验自动架构启动脚本逻辑、ZIP 完整性、程序集依赖完整性、签名或真实运行。构建正在由根代理推进，这些发布文件的最终状态应以根代理最终验证为准。

本轮未运行测试、矩阵、GUI、后台 EXE或设备调用；未读取 private 捕获、reference、驱动、许可、OTA；未发送设置或修改系统。唯一新增文件为本报告，指定源码未修改。
