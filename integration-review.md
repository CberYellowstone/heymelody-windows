# 阶段五集成有界审阅

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
