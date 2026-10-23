# 优化待办

本页登记 review/0.9.9 分支按方向分轮优化时发现的优化项，供后续轮次选择。入口见 [AGENTS.md](../AGENTS.md)，开发与验证入口见[开发指南](development.md)。

## 用途与规则

- 本分支（review/0.9.9）按方向分轮优化。每轮只实施用户指定的方向。
- 全局审查中发现的其他方向可用优化只登记在本页，不顺手实施；用户另行要求时才例外。
- 实施会话完成后更新条目状态，并链接对应专题文档或[版本记录](history/release-notes.md)。
- 完整证据放 `research/<专题>/`（本机完整档案，通常不随 Git clone 提供），此处只写摘要。
- 行号会随代码变化。登记时注明 commit 短哈希与日期；本轮改动过的 Resources、Contracts、Client、Host 文件的行号可能已移动，按符号定位，不按行号。
- “预估收益”是预期，不是测量结果，除非写明来源。验证层级沿用项目分级：静态分析、模拟或离线测试、Linux 测量、Windows 原生或真机验证；未写明 Windows 的条目均未在 Windows 测量。

## 条目字段

编号（方向前缀加序号）；方向（由所在小节标题和编号前缀给出）；位置（`file:line @commit`）；现象与依据（括号内为验证层级）；预估收益；风险与依赖；状态（待处理、进行中、已完成、放弃并说明理由）；登记来源（日期加当轮方向）。下列“后端内存”小节的登记来源均为 2026-10-21 后端内存轮；其他小节为同一轮全局审查中顺带发现的条目，登记来源写作“2026-10-21 后端内存（顺带发现）”。位置的 commit 为 `f415d29`（该轮审计起点）；MEM-00 与 CPU-05 涉及本轮新增或改动的文件，以该起点为基准。“能耗（PWR）”小节及 2026-10-22 后端能耗轮登记的条目以 `53a436a`（该轮起点，上一轮已提交的末尾）为基准，其位置涉及该轮尚未提交的改动，按符号定位。“能耗（PWR）”小节的登记来源为 2026-10-22 后端能耗，其中同轮顺带发现的条目写作“2026-10-22 后端能耗（顺带发现）”。

## 后端内存（MEM）

MEM-00 为本轮已完成项的汇总，实施细节、Linux 检查结果与未验证内容见[后台内存审计](research/backend-memory-audit.md)。MEM-01 起为本轮未实施的后续项，其中 MEM-10 已在 2026-10-22 能耗轮完成。

| 编号 | 位置 | 现象与依据 | 预估收益 | 风险与依赖 | 状态 | 登记来源 |
| --- | --- | --- | --- | --- | --- | --- |
| MEM-00 | Host、Resources、Contracts、Client 多文件，详见[审计](research/backend-memory-audit.md) @f415d29 起点 | 用户报告 Windows x64 托盘常驻约 80–100 MB（0.7.0 前后端分离时约 30 MB，测量口径未知）。已实施：GC 配置、托盘软件渲染与 RunTray 拆分、资源流式哈希／下载／解压、快照 Clone、BLE 公司 ID 过滤、试听单拷贝与缓冲复用、目录载荷缓存／内置目录只解析一次／OriginalBytes 仅在线保留、安装后压缩回收、IPC 源生成（静态分析、模拟或离线测试、Linux 测量） | 未在 Windows 测量 | Windows 实测与真机验证待补，清单见审计文档 | 已完成（Linux 验证，Windows 待验证） | 2026-10-21 后端内存 |
| MEM-01 | `src/HeyMelody.Resources/ResourceCoordinator.cs:455-463` `CachedCatalogExists` @f415d29 | 每次 10 分钟配置检查都 ReadAllBytes、AES-GCM 解密、gunzip 到 MemoryStream 再 SHA256，瞬时 LOH 峰值为配置大小的数倍（静态分析） | 降低每 10 分钟的瞬时 LOH 峰值；未测量 | 缓存有效性判断语义（摘要、密钥校验）须不变 | 待处理 | 2026-10-21 后端内存 |
| MEM-02 | `src/HeyMelody.Resources/ResourceCatalog.cs:37-85` `Parse` @f415d29 | utf8.ToArray、GetRawText（120 KB 内置目录约生成 240 KB 的 UTF-16 字符串，进 LOH）、JsonNode.Parse、SerializeToElement，逐 profile SerializeToElement／DeepClone；常驻 Raw 与逐 profile 副本；启动、每次在线更新和每次 worker 解析都执行（静态分析） | 减少目录常驻堆与瞬时 LOH；未测量 | 须保持目录摘要、OriginalBytes 与修复路径语义；需 Resources.Tests 对拍 | 待处理 | 2026-10-21 后端内存 |
| MEM-03 | `src/HeyMelody.Resources/ResourceCache.cs:9-26` `ResourceCacheCodec.Encrypt/Decrypt` @f415d29 | MemoryStream 与 ToArray 多份拷贝，配置上限 8 MiB（静态分析） | 降低缓存读写的瞬时峰值；未测量 | 信封格式（12 字节 nonce、16 字节 tag）与旧缓存须字节兼容 | 待处理 | 2026-10-21 后端内存 |
| MEM-04 | `src/HeyMelody.Resources/VendorResourceClient.cs:103-110` `BoundedReadAsync` @f415d29 | 每次调用分配 byte[81920]，MemoryStream 倍增后 ToArray；每 10 分钟逐目标的元数据 POST 也走此路径。包下载已于本轮改为流式，此处仅余目录与元数据响应（静态分析） | 小；降低每 10 分钟的瞬时分配 | 响应大小上限与取消语义须不变 | 待处理 | 2026-10-21 后端内存 |
| MEM-05 | `src/HeyMelody.Resources/ArtworkPackageExtractor.cs` `IsAnimation`、`IndexAnimation`（:196、:231）@f415d29 | 仍整读 Lottie JSON（上限 24 MiB）并 JsonNode.Parse，保留是为了 UTF-8 BOM 语义不变（静态分析） | 仅影响在线美术安装时的瞬时峰值；未测量 | 须保持 BOM 与解析语义，需对拍测试 | 待处理 | 2026-10-21 后端内存 |
| MEM-06 | `src/HeyMelody.Resources/BundledResourceStore.cs:34-39` @f415d29 | `GetDefaultKey` 每次 `ActiveKey()` 都重新反序列化 default-keys.json；`ReadEmbedded` 双份拷贝；每个实例都反序列化 artwork.json（224 KB），Host 中的实例数待统计（静态分析） | 小；确认实例数后再定 | 本轮已把 GetCatalog 改为实例级 Lazy（见 MEM-00），此处为其余部分 | 待处理 | 2026-10-21 后端内存 |
| MEM-07 | `src/HeyMelody.Resources/ResourceCoordinator.cs:95` `ExportEffectiveCatalogJson` @f415d29 | 每次调用分配整份字符串；src 与 tests 中已 grep，无调用，疑为死代码（静态分析；公开成员，外部引用未查） | 无运行时收益，仅删除死代码 | 删除前确认无外部或反射使用，属公开成员变更 | 待处理 | 2026-10-21 后端内存 |
| MEM-08 | `src/HeyMelody.Host/BackendSupervisor.Resources.cs:124-132`、`NativeWorker.cs` @f415d29 | 每个 worker 都保留完整目录而只需一个 profile；gzip 加 base64 的目录在每次 worker 启动时经 stdin 发送，可只传该 profile 或缓存路径。本轮只让 Host 端载荷按目录实例缓存（见 MEM-00）（静态分析） | 每个 worker 的常驻堆与启动开销；未测量 | RpcWire 每行 524,288 字符上限；worker 的目录解析行为须不变；涉及 Host 与 worker 内部约定 | 待处理 | 2026-10-21 后端内存 |
| MEM-09 | `src/HeyMelody.Host/CalibrationAuditionPlayer.cs:43-75` @f415d29 | 试听样本（约 4.4 MB 的 short[]，在 LOH）从 Prepare 到 Cleanup 一直存活，可改为直接读内嵌资源的 UnmanagedMemoryStream（静态分析） | 试听期间约少 4.4 MB 常驻 | 须在 Windows 真机验证试听播放；与本轮 `AuditionWave` 流式解析相邻，不要回退 | 待处理 | 2026-10-21 后端内存 |
| MEM-10 | `src/HeyMelody.Host/BackendSupervisor.cs:440-467` `ScheduleBroadcastExpiryLocked` @f415d29 | 每条匹配广播都新建 linked CTS 与 Task.Delay，每个 peer 一个可复用计时器即可（静态分析） | 广播密集时减少分配；未测量 | 须保持过期、开盖去重与 30 秒新鲜性语义；需真机广播验证 | 已完成（2026-10-22 能耗轮：每个 peer 一个可复用计时器，Linux 离线测试通过，Windows 真机广播待验证；见[后台能耗](research/backend-energy.md)） | 2026-10-21 后端内存 |
| MEM-11 | `src/HeyMelody.Host/NativeWorker.cs:221-222`、`:277-278` @f415d29 | worker 的 stdout、stderr 在匿名管道上各占用一个线程池线程的读取（静态分析） | 小 | 需 Host.Tests 覆盖管道读取行为 | 待处理 | 2026-10-21 后端内存 |
| MEM-12 | `src/HeyMelody.Windows/RfcommConnection.cs:33-34`、`:144` @f415d29 | RFCOMM 的 RegisteredWaitHandle 每个网络事件重新注册（静态分析） | 小 | 属接收通道，须保持每连接单一接收通道；需真机验证 | 待处理 | 2026-10-21 后端内存 |
| MEM-13 | `src/HeyMelody.Host/DeviceStateStore.cs`、`DeviceWhitelistStore.cs`、`DeviceAppearanceStore.cs` @f415d29 | Host 持久化存储仍用反射 JSON；Linux 探针的 host 场景显示反射 JSON 比源生成多约 5 MiB RSS，可考虑文件字节保持不变的源生成上下文（Linux 测量，探针合成负载；Windows 未测） | 量级约数 MiB（探针 host 场景）；未在 Windows 复测 | 持久化文件字节与中文转义策略须不变，旧文件须可读 | 待处理 | 2026-10-21 后端内存 |
| MEM-14 | `src/HeyMelody.Host/TrayApplication.cs` @f415d29 | 托盘为两项菜单仍加载 Avalonia.Controls 与 FluentAvalonia；原生 Win32 托盘菜单可能更省，但本轮为保持外观和 zh-CN 字体而决定不做（静态分析层面的假设，未测量） | 未测量 | 菜单外观、字体、DPI 与深浅色回归；需 Windows 视觉验证 | 待处理（本轮已决定不做） | 2026-10-21 后端内存 |
| MEM-15 | `src/HeyMelody.Calibration/HearingRadarMath.cs:21-26`、`:96` @f415d29 | 预览期间每 50 ms 分配 double[6] 与 ReadOnlyCollection（静态分析） | 小 | 与结果雷达显示一致性需单元测试 | 待处理 | 2026-10-21 后端内存 |

## 能耗（PWR）

PWR-00 为 2026-10-22 后端能耗轮已完成项的汇总，依据、测试数量与 Windows 待验证清单见[后台能耗](research/backend-energy.md)。该轮同时完成了上文 MEM-10 与下文 IO-01、CPU-01、CPU-02。

| 编号 | 位置 | 现象与依据 | 预估收益 | 风险与依赖 | 状态 | 登记来源 |
| --- | --- | --- | --- | --- | --- | --- |
| PWR-00 | `src/HeyMelody.Host/EnergyPolicy.cs`、`EnergyController.cs`、`WindowsProcessEnergy.cs`（新增），`BackendPipeServer.cs`、`TrayApplication.cs`、`WindowsStateMonitor.cs`、`BackendSupervisor.cs`、`CalibrationAuditionPlayer.cs`、`DeviceStateStore.cs`，`src/HeyMelody.Resources/ResourceCoordinator.cs`，`RpcWire.cs`、`ClientBridge.cs` @53a436a 起点 | 无界面连接时（启动后 15 秒宽限、最后一个界面断开后 10 秒延迟）托盘与全部 worker 进入 EcoQoS 加 IDLE 优先级，界面连接立即恢复，启动窗口前先恢复默认并保持 30 秒，试听线程豁免；BLE 两个 watcher 只在存在已配对且在白名单的耳机时运行；试听泵与资源协调器改为空闲阻塞或睡到到期，BLE 过期每 peer 一个计时器；设备状态内容未变时不写盘；IPC 缓冲读取与单次序列化。已实施部分由静态分析与 Linux 模拟或离线测试验证，未做 Linux 能耗测量 | 未在 Windows 测量 | 效率模式显示、IDLE 响应、BLE 启停、试听豁免等 Windows 待验证，清单见[后台能耗](research/backend-energy.md) | 已完成（Linux 验证，Windows 待验证） | 2026-10-22 后端能耗 |
| PWR-01 | 开盖弹窗实现处（尚未实现）；`EnergyPolicy.cs`、`BackendPipeServer.cs` 启动窗口路径 @53a436a | 开盖弹窗实现时复核效率模式优先级（IDLE→BELOW_NORMAL 备选）：满载 CPU 时 IDLE 优先级可能让 BLE 广播回调延迟数秒，用户 2026-10-22 决定先保持 IDLE（静态分析层面的推断，未测量） | 避免开盖弹窗因广播回调延迟而迟到；未测量 | 弹窗显示前须像启动主窗口那样先恢复默认；延迟不可接受时改 BELOW_NORMAL 并保留 EcoQoS；须在 Windows 满载下测广播回调延迟 | 待处理 | 2026-10-22 后端能耗 |
| PWR-02 | `src/HeyMelody.Host/BackendPipeServer.cs` `UpdateGuiCount`、subscribe 处理 @53a436a | 管道没有 subscribe 超时：已连接但一直不订阅（保持沉默）的客户端按角色未知的界面计入 GUI 数，直到它断开，期间托盘与 worker 不会进入效率模式；observer 以外全部计入是有意的，以免启动中的窗口遇到被限速的后台（静态分析；CPU 与能耗） | 异常或挂起的客户端存在时仍可进入效率模式；未测量 | 超时须长于正常界面的启动与订阅耗时，不能让慢启动的窗口遇到 Eco；需 Host.Tests 覆盖 | 待处理 | 2026-10-22 后端能耗（顺带发现） |

## 界面与前端（UI）

UI-00 为 2026-10-22 界面性能轮（起点 `a13e236`）已完成项的汇总，依据、反编译核实、测试数量与 Windows 待验证清单见[界面性能（0.9.9）](research/ui-performance-0.9.9.md)。该轮同时完成了 UI-01、UI-02，并核实了 UI-03；UI-04 起为该轮登记的后续项，位置以 `a13e236` 为基准，涉及该轮改动的文件按符号定位。

| 编号 | 位置 | 现象与依据 | 预估收益 | 风险与依赖 | 状态 | 登记来源 |
| --- | --- | --- | --- | --- | --- | --- |
| UI-00 | `src/HeyMelody.App/RenderingSetup.cs`、`EncoFree4ModelView.cs`、`AnimationYieldTriggers.cs`（新增）、`MainView.axaml`，`src/HeyMelody.Artwork/LottieView.cs`、`AnimationYield.cs`、`GpuJanitor.cs`（新增），`src/HeyMelody.Client/MainViewModel.WriteQueue.cs`（新增）等 @a13e236 起点 | 合成器默认把一帧的全部脏区并成一个包围矩形（反编译核实），改为区域跟踪；界面展开、滚动与窗口失活时，模型空闲自转与 Lottie 降到 30 fps；模型帧直接写入锁定位图，少一次整帧复制；Lottie 末帧缓存为 GPU 图像；EQ 竖直滑块居中不再被裁；动态设置行与 EQ 选择改为非阻塞排队（静态分析、反编译与 Linux 无头测试） | 降低弱 GPU 上的整窗重绘与 UI 线程复制；未在 Windows 测量 | 8cx 等真机的帧时间、Mica 与合成行为、真机写入均待 Windows 验证 | 已完成（Windows 待验证） | 2026-10-22 界面性能 |
| UI-01 | `src/HeyMelody.Client/MainViewModel.Selection.cs:45` `ReadOnlyPresentation` @f415d29 | 用默认选项的 JSON 往返复制快照，可改用 `UiSnapshot.Clone()`（静态分析） | 减少界面端分配与反射序列化；未测量 | 须保持“只读展示不含暂存选择和 EQ 草稿”的语义；Client.Tests 覆盖 | 已完成（2026-10-22 界面性能轮，改用 `Clone()`，语义由既有 Clone 等价检查覆盖） | 2026-10-21 后端内存（顺带发现） |
| UI-02 | `src/HeyMelody.App/RenderingSetup.cs:17`、`:22` @f415d29 | GUI 的 Skia GPU 资源上限为 256 MiB，而 Avalonia.Skia 12.1.4 不按时清理 GPU 缓存（2026-10-22 反编译确认：只调用 `SetResourceCacheLimit`，从不 purge；`WriteableBitmap` 每次修改生成新 `SKImage`，即新纹理） | 可能降低 GUI 长时间运行的显存与内存占用；未测量 | 影响三维模型与 120 Hz 渲染预算；需 Windows 原生窗口验证 | 已完成（保留 256 MiB 上限，新增事件驱动的 `GpuJanitor`：动画让路结束或窗口失活后最多每 10 s 调用 `PurgeUnusedResources(30 s)`，无空闲定时器；Windows 显存未测量） | 2026-10-21 后端内存（顺带发现） |
| UI-03 | FluentAvaloniaTheme 3.1.0 包依赖 @f415d29 | 该主题包把 Avalonia.Controls.DataGrid 和 ColorPicker 带入每个 GUI 进程（包元数据，静态分析）。2026-10-22 反编译：`ControlThemes/Controls.axaml` 无条件以 `typeof(DataGrid)`、`typeof(ColorPicker)` 等为键注册延迟主题，主题类没有排除选项 | 减少加载的程序集；未测量 | 只能替换主题包或修补其资源字典，会影响外观，见 [FluentAvalonia 调研](research/fluent-avalonia.md)；加载时机未运行验证 | 待处理（本轮核实后不实施：无受支持的排除方式） | 2026-10-21 后端内存（顺带发现） |
| UI-04 | `src/HeyMelody.App/EncoFree4ModelView.cs` `PresentLatestFrame`，`src/HeyMelody.Rendering.Filament` @a13e236 | 模型每帧仍经 GPU 回读、CPU 复制到位图、再由 Skia 上传新纹理（静态分析）；可改为 Filament 与 Avalonia 共享 GPU 纹理（D3D11/Vulkan 互操作） | 去掉回读与重新上传，弱 GPU 上收益最大；未测量 | 需要原生层与 Avalonia GPU 互操作改造，OpenGL/Vulkan 两后端分别验证；属架构改动 | 待处理 | 2026-10-22 界面性能 |
| UI-05 | `EncoFree4ModelView.cs` `FitCamera`（物理像素尺寸、`MsaaSamples = 4`）@a13e236 | 模型按物理像素和 4×MSAA 渲染，200% 缩放的大窗口约 1440×1280（静态分析） | 弱 GPU 上可按渲染耗时降低分辨率或 MSAA；未测量 | 画质取舍需用户决定；须在 8cx 等真机测量 | 待处理 | 2026-10-22 界面性能 |
| UI-06 | Avalonia 12.1.4 Win32 合成（WinUI/DirectComposition 交换链）| 反编译未见部分呈现（partial present）；区域脏矩形减少的是重绘，交换链是否仍整窗提交未核实（静态分析，低置信度） | 若整窗提交，可进一步降低 GPU 带宽；未测量 | 需 Windows 上的 GPU 计数或 PIX 测量；可能依赖上游改动 | 待处理 | 2026-10-22 界面性能 |
| UI-07 | `src/HeyMelody.Artwork/LottieView.cs` `LottieHandler` @a13e236 | 未让路时循环 Lottie 仍按每个合成帧执行 Skottie（0.9.0 为 120 Hz 平滑有意保留；静态分析） | 若 Windows 测量显示 Lottie 仍是瓶颈，可按动画原生帧率或缓存循环帧 | 违背 0.9.0 的平滑决定，需用户确认；缓存循环帧内存开销大 | 待处理 | 2026-10-22 界面性能 |
| UI-08 | `src/HeyMelody.App/RenderingSetup.cs`／Avalonia 12.1.4 ARM64 Adreno 选卡 | 本机默认选卡命中上游屏蔽规则，普通 UI 实际为 WARP；同 WebP 场景显式 Adreno 后回调节拍与 CPU 明显改善，见[本机卡顿调查](research/ui-lag-arm64-2026-10-23.md) | 解除已实测的主要软件绘制成本；数据仅适用于本机离线组合压力场景，不是上屏 FPS | 上游历史透明窗口问题仍需兼容验证；覆盖所有共享 RenderingSetup 的窗口、弹出层及设备丢失恢复，保留回退 | 已完成：所有 Windows 默认首适配器与持久 Basic Render 开关；本机 ARM64 离线原生与失败后备已验，x64／x86 仅构建，旧驱动／长期兼容性仍有边界 | 2026-10-23 本机 ARM64 卡顿调查 |
| UI-09 | `src/HeyMelody.Artwork/AnimatedWebpView.cs`／X4 主图 | 真实主图为 780×780、约30.3帧／秒 WebP；同组件同源暂停后帧回调由约86升至112次／秒，已定位持续更新路径的额外成本，未拆分解码／复制／纹理上传占比 | 硬件路径上的后续帧时改善；保持素材、画质与完整功能 | 需继续用同源对照与线程／纹理数据定位，不能把源30帧当作整个UI上限 | 调查定位完成，机制细分与修复待处理 | 2026-10-23 本机 ARM64 卡顿调查 |
| UI-10 | 未来 NSIS 安装器与 `uiBasicRenderMode` 初始值 | 部分旧 Adreno 设备曾出现透明窗口故障，见[上游历史](research/avalonia-adreno-history-2026-10-23.md)；当前默认 GPU，用户可选 Basic Render | 安装阶段为经过确认的不兼容设备初始化兼容值，降低首次启动失绘风险 | 需经验证的 GPU／驱动组合清单；尊重用户已有选择，不按所有 ARM 设备笼统启用 | 待未来引入 NSIS 时实现；本轮不实现检测、安装器或初始化逻辑 | 2026-10-23 用户指定后续方向 |

## 磁盘 I/O（IO）

| 编号 | 位置 | 现象与依据 | 预估收益 | 风险与依赖 | 状态 | 登记来源 |
| --- | --- | --- | --- | --- | --- | --- |
| IO-01 | `src/HeyMelody.Host/DeviceStateStore.cs:113-123`、`:137-165`、`:216`、`:277`；调用方 `BackendSupervisor.cs:600-607` @f415d29 | 每个 SPP 快照都重写并以 WriteThrough 落盘，因为 SampleTime 总在变；`:277` 为检查大小而序列化整份展示。建议仅 SampleTime 前进且小于 60 秒时跳过落盘（界面只显示 HH:mm）（静态分析） | 减少频繁的磁盘写与 fsync；未测量 | 须保持缓存展示与“上次连接时间”语义；设备状态缓存见[设备状态](device-state.md) | 已完成（2026-10-22 能耗轮：内容未变且样本前进不足 60 秒时不写盘，关闭时写入最新样本；Linux 离线测试通过，Windows 未验证；见[后台能耗](research/backend-energy.md)） | 2026-10-21 后端内存（顺带发现） |
| IO-02 | `src/HeyMelody.Host/DeviceStateStore.cs` `Load`、`Remember` @53a436a | `Load` 不预置写入节流状态（已排队内容与样本时间），重启后第一次 `Remember` 总会重写文件，即使内容与文件相同（静态分析） | 每次重启每个设备少一次写盘；小 | 预置须与文件内容一致并保持 60 秒规则；崩溃后保存时间最多晚 60 秒的约定不变 | 待处理 | 2026-10-22 后端能耗（顺带发现） |

## CPU、IPC 与启动（CPU）

| 编号 | 位置 | 现象与依据 | 预估收益 | 风险与依赖 | 状态 | 登记来源 |
| --- | --- | --- | --- | --- | --- | --- |
| CPU-01 | `src/HeyMelody.Contracts/RpcWire.cs:38-52` `ReadLineAsync` @f415d29 | 每次 ReadAsync 只读一个字符并写入 StringBuilder（行约 14 KB）（静态分析） | 降低 IPC 读取的 CPU 与异步开销；未测量 | 须保持每行 524,288 字符上限与畸形输入行为 | 已完成（2026-10-22 能耗轮：带缓冲的 `RpcLineReader`，Linux 离线测试通过；见[后台能耗](research/backend-energy.md)） | 2026-10-21 后端内存（顺带发现） |
| CPU-02 | `src/HeyMelody.Host/BackendPipeServer.cs:251`、`:314` @f415d29 | 同一事件对每个客户端分别序列化（静态分析） | 多客户端时减少重复序列化；未测量 | 每客户端按键合并的输出队列语义须不变 | 已完成（2026-10-22 能耗轮：每个事件序列化一次，线格式字节不变，Linux 离线测试通过；见[后台能耗](research/backend-energy.md)） | 2026-10-21 后端内存（顺带发现） |
| CPU-03 | 快照事件，`src/HeyMelody.Contracts/UiContracts.cs:69-100` @f415d29 | 每次快照事件都重复发送不变的 AncModes、FeatureSections、EqualizerEntries（合成估计约 14 KB）；增量协议需提升 IPC schema（静态分析加合成估计，非真机） | 降低 IPC 流量与序列化；未测量 | 需提升 IPC schema 版本，与旧后台的升级处理见[后台架构](backend-architecture.md) | 待处理 | 2026-10-21 后端内存（顺带发现） |
| CPU-04 | `--catalog-worker` 启动，`src/HeyMelody.Host/NativeWorker.cs:301-316` @f415d29 | 每次目录刷新新建一个进程（已有 200 ms 合并）；进程隔离是设计要求（静态分析） | 进程创建开销；收益有限 | 进程隔离不能放弃；可作调度优化而非改变隔离 | 待处理 | 2026-10-21 后端内存（顺带发现） |
| CPU-05 | `src/HeyMelody.Contracts/RpcJsonContext.cs`（本轮新增，基准 @f415d29）与探针日志 | 源生成 JSON 首次使用仍 JIT 约 1,000 个方法（`JsonPropertyInfoValues<T>` 与上下文 lambda 以 Tier0 编译）；可试 GenerationMode=Serialization，或查明 crossgen 拒绝预编译的原因（Linux 测量，探针日志，合成负载） | 降低首次往返的 JIT 时间；未在 Windows 测量 | 须保持线格式字节兼容；Windows ReadyToRun 行为可能不同 | 待处理 | 2026-10-21 后端内存（顺带发现） |
| CPU-06 | `src/HeyMelody.Host/CalibrationAuditionPlayer.cs` `Fill` @f415d29 | 逐帧复制并带取模与边界检查，分段 Array.Copy 即可（静态分析） | 试听期间每 5 ms 的 CPU；小 | 须保持循环与声道映射输出样本逐位一致 | 待处理 | 2026-10-21 后端内存（顺带发现） |
| CPU-07 | `src/HeyMelody.Host/WindowsStateMonitor.cs:220` `Advertisement` @f415d29 | 每条匹配广播分配一个 DataReader 与 byte[]（按公司 ID 过滤后影响较小）（静态分析） | 小 | 须保持单包解析语义，需真机广播验证 | 待处理 | 2026-10-21 后端内存（顺带发现） |
| CPU-08 | `src/HeyMelody.Client/ClientBridge.cs` `Connection.Reader` @53a436a | `RpcLineReader` 构造在 `StreamReader`（4096 字符缓冲）之上，字节经两层缓冲与拷贝，可直接从管道字节解码（静态分析；Host 端读取点是否同样叠两层未核对） | 小；每次读取少一层拷贝 | 须保持 UTF-8 严格解码、行长上限与 EOF、'\r' 语义 | 已完成（2026-10-22 审查修复：`RpcLineReader` 以 `Stream` 构造并增量解码字节，客户端保持严格解码，Host、worker 端同样直接读流；Linux 离线测试通过；见[后台能耗](research/backend-energy.md)） | 2026-10-22 后端能耗（顺带发现） |

## 发布体积（PKG）

| 编号 | 位置 | 现象与依据 | 预估收益 | 风险与依赖 | 状态 | 登记来源 |
| --- | --- | --- | --- | --- | --- | --- |
| PKG-01 | `Build-Client.ps1:219-222` @f415d29 | HeyMelody.Resources 的 Bundles/data（约 34 MB、635 个文件）同时复制进 GUI 目录和 backend；Host 只检查存在并把路径交给界面（静态分析） | 发布包减小约 34 MB 每份副本；未测量 | 去重前先确认界面实际读取哪一份；Verify-Release 与三架构包结构需同步 | 待处理 | 2026-10-21 后端内存（顺带发现） |

## 正确性（BUG）

| 编号 | 位置 | 现象与依据 | 预估收益 | 风险与依赖 | 状态 | 登记来源 |
| --- | --- | --- | --- | --- | --- | --- |
| BUG-01 | `src/HeyMelody.Host/BackendPipeServer.cs:159` @f415d29 | 值为 null 的 uid 或 color 被 WhenWritingNull 省略后，`GetProperty` 抛出（静态分析；既有行为，本轮未改） | 修复一个已有的边界缺陷，无内存收益 | 需先确认触发路径，修复须有测试依据 | 待处理 | 2026-10-21 后端内存（顺带发现） |
| BUG-02 | `src/HeyMelody.Resources/ResourceCoordinator.cs` `StartAutoRefresh` 的循环 @53a436a | 自动刷新一轮中抛出 OperationCanceledException 以外的异常时，循环内的 `try` 只捕获取消，异常逃出 `Task.Run`，循环静默结束，此后不再自动刷新也没有记录（静态分析；既有行为，改为睡到到期后仍然如此） | 修复静默停摆；无能耗收益 | 须保持取消与释放语义和 30 秒下限；应记录而非吞没异常；需 Resources.Tests 覆盖 | 待处理 | 2026-10-22 后端能耗（顺带发现） |
