# 主界面启动与首屏加载（0.9.9）

2026-10-23 在 review/0.9.9 分支完成的主界面启动优化。入口见 [AGENTS.md](../../AGENTS.md)，其他方向的后续项见[优化待办](../optimization-backlog.md)（CPU-09 至 CPU-11）。本页只记录静态分析结论与 Linux 测试结果，**未在 Windows 上测量启动耗时**。

## 依据（静态分析）

改动前，从点击图标到页面可用依次要经过以下步骤：

1. Avalonia 初始化与主窗口构造。
2. `Opened` 后才开始连接管道。连接先等 300 ms 超时；托盘未运行时，超时后才启动 `HeyMelody.Host.exe`。
3. `subscribe`。
4. `enumerate`。Host 每次都新建一个 `--catalog-worker` 进程做 WinRT 已配对设备枚举；冷启动时还要排在托盘自己的首次目录刷新之后，即串行启动两个 worker。
5. `select` 与 `resourceSettings`。

以上全部完成后才撤下启动遮罩、启用页面。另外，首个带降噪模式的快照到达时，UI 线程要同步构造 `BundledResourceStore`：用反射方式反序列化 224 KB 的 artwork.json，且该 store 随进程常驻。

发布包已经启用 ReadyToRun，美术解码（WebP、Lottie、位图）已在线程池完成，因此这两项不是本轮的瓶颈。

## 实施

| 改动 | 位置 | 效果 | 语义边界 |
| --- | --- | --- | --- |
| 提前连接 | `Program.Main`、`ClientBridge.BeginConnect`、`App`、`MainWindow(IHeadsetService)` | 取得主实例互斥体后，在 Avalonia 初始化之前创建客户端并在线程池建立管道。冷启动时托盘与界面并行启动 | 只建管道，不 subscribe、不发请求。失败留给第一条真实请求重连，并沿用原有告警。关闭时随客户端生命周期取消，不会在连接闸门上等满 15 秒。连接尚未订阅时按界面计数，托盘提前离开效率模式（原有规则，60 秒内未订阅则不再计入） |
| 互斥体探针 | `ClientBridge.EnsureConnectionAsync` | 托盘单实例互斥体不存在时直接启动后台，省去 300 ms 探测 | 互斥体存在（托盘启动中或退出中）或探针无法判断时，保持原来的“先探测 300 ms、再启动”顺序和 10 秒防重复启动。设备管理器与校准窗口使用同一个公共构造函数，同样受益 |
| 先显示后台已有名单 | `MainViewModel.BackendStartupDevices`、`MainWindow.InitializeOnceAsync`、`DeviceStartupCoordinator.ConfirmPresentedAsync` | 热启动时 `subscribe` 返回的有效名单非空且没有选择记录警告，就直接 `InitializeAsync(名单)` 并撤下遮罩。`enumerate` 改在后台核对 | 后台名单由托盘的设备监听维护，只用于展示和选择，不授予控制、不触发 SET。核对结果非空时由 Host 的 catalog 事件更新页面（不发 Select）。核对失败时保留原选择，由 Host 的 health 状态提示，不弹窗。核对结果为空、且没有手动管理或关闭正在进行时，恢复遮罩并走原来的管理器流程。名单为空、带警告或后台未订阅时，仍走原来的阻塞路径 |
| 加入托盘首次刷新 | `BackendSupervisor.RefreshCatalogForClientAsync`，`enumerate` 与 `enumerateCandidates` | 托盘首次初始化（资源加首次目录刷新）进行中时，客户端查询等待它完成，不再排队第二个 worker | 只在首次初始化期间生效。首次刷新失败或已结束时，仍按原样新建刷新。单个客户端取消只取消自己的等待。依据：冷启动时该刷新随托盘进程开始，晚于拉起它的窗口；之后的配对变化由设备监听触发 catalog 事件 |
| 降噪图标路径 | `DeviceVisuals.AncGlyphPaths`、`PrewarmBundledArtworkAsync` | 在线程池上一次性解析 5 个固定图标路径，之后释放 store；UI 线程不再解析 artwork.json | 位图仍在首次使用时解码，不对无降噪设备常驻。解析失败时退回符号图标，与原先的 `IOException` 回退一致 |

内存：不增加常驻对象；artwork.json 的反序列化结果不再常驻，预计略有下降（未测量）。CPU：提前连接与后台核对只是改变时序。冷启动少启动一个 catalog-worker 进程。热启动的 `enumerate` 仍执行，因此不增加工作量。

## 验证（Linux x64，.NET SDK 10.0.401）

- App、Client、DeviceManager Release 编译 0 错误。Host 以 `-p:EnableWindowsTargeting=true` 编译 0 警告 0 错误。
- Client.Tests 190→194 项全部通过，新增 4 项：
  - 互斥体不存在时不探测即启动；
  - 提前建立的管道直接服务第一条请求；
  - 提前连接期间释放客户端能及时返回；
  - 提前连接失败后由首条请求重连，且不重复启动后台；
  - 订阅名单只作为启动提示，后台不可用、名单为空或带警告时清空。
- Host.Tests 共 116 个 PASS 行，新增 2 项：
  - 首次初始化期间的查询只触发一次枚举，取消一个等待方不影响共享刷新，首次刷新结束后恢复按需刷新；
  - 首次刷新失败时，等待方改为自行刷新。
- App.Tests 新增 1 项无头检查：确认逻辑只在空名单时要求管理器，失败时保留名单，取消时向上传播，且从不打开管理器、连接或提交设置。
- App.Tests 新增 1 项 `RunNativeWindowCasesAsync` 原生窗口用例：后台核对仍在进行时页面已可用，关闭窗口会取消核对。该用例只在 Windows 原生运行，本轮未执行。
- App.Tests 有两组既有失败，与本轮无关，在未修改的 HEAD（`cb8171e`）上同样失败，已登记为 BUG-03 与 BUG-04：
  - `AncFeatureTests` 的设置行几何检查；
  - `ArtworkPersistenceTests` 的“全部设置为图标卡片”检查。
  - 临时让各组不中断地运行后，本轮与 HEAD 的结果仅差新增的 1 项通过，其余组一致。
- DeviceManager.Tests 8 个用例全部通过。

## 未验证

- Windows 冷启动与热启动的实际耗时、首帧时间、CPU 与内存，均未测量。
- 真实托盘下互斥体探针与提前连接的时序。
- 原生窗口用例。
- ARM64 硬件。

建议的 Windows 测量方法：分别在托盘已运行和未运行时，记录从启动 `HeyMelody.exe` 到启动遮罩消失的时间，并用 `tools/Measure-HostMemory.ps1` 对比 catalog-worker 进程数与各进程内存。
