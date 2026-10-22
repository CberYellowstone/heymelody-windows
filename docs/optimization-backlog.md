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

编号（方向前缀加序号）；方向（由所在小节标题和编号前缀给出）；位置（`file:line @commit`）；现象与依据（括号内为验证层级）；预估收益；风险与依赖；状态（待处理、进行中、已完成、放弃并说明理由）；登记来源（日期加当轮方向）。下列“后端内存”小节的登记来源均为 2026-10-21 后端内存轮；其他小节为同一轮全局审查中顺带发现的条目，登记来源写作“2026-10-21 后端内存（顺带发现）”。位置的 commit 为 `f415d29`（该轮审计起点）；MEM-00 与 CPU-05 涉及本轮新增或改动的文件，以该起点为基准。

## 后端内存（MEM）

MEM-00 为本轮已完成项的汇总，实施细节、Linux 检查结果与未验证内容见[后台内存审计](research/backend-memory-audit.md)。MEM-01 起为本轮未实施的后续项。

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
| MEM-10 | `src/HeyMelody.Host/BackendSupervisor.cs:440-467` `ScheduleBroadcastExpiryLocked` @f415d29 | 每条匹配广播都新建 linked CTS 与 Task.Delay，每个 peer 一个可复用计时器即可（静态分析） | 广播密集时减少分配；未测量 | 须保持过期、开盖去重与 30 秒新鲜性语义；需真机广播验证 | 待处理 | 2026-10-21 后端内存 |
| MEM-11 | `src/HeyMelody.Host/NativeWorker.cs:221-222`、`:277-278` @f415d29 | worker 的 stdout、stderr 在匿名管道上各占用一个线程池线程的读取（静态分析） | 小 | 需 Host.Tests 覆盖管道读取行为 | 待处理 | 2026-10-21 后端内存 |
| MEM-12 | `src/HeyMelody.Windows/RfcommConnection.cs:33-34`、`:144` @f415d29 | RFCOMM 的 RegisteredWaitHandle 每个网络事件重新注册（静态分析） | 小 | 属接收通道，须保持每连接单一接收通道；需真机验证 | 待处理 | 2026-10-21 后端内存 |
| MEM-13 | `src/HeyMelody.Host/DeviceStateStore.cs`、`DeviceWhitelistStore.cs`、`DeviceAppearanceStore.cs` @f415d29 | Host 持久化存储仍用反射 JSON；Linux 探针的 host 场景显示反射 JSON 比源生成多约 5 MiB RSS，可考虑文件字节保持不变的源生成上下文（Linux 测量，探针合成负载；Windows 未测） | 量级约数 MiB（探针 host 场景）；未在 Windows 复测 | 持久化文件字节与中文转义策略须不变，旧文件须可读 | 待处理 | 2026-10-21 后端内存 |
| MEM-14 | `src/HeyMelody.Host/TrayApplication.cs` @f415d29 | 托盘为两项菜单仍加载 Avalonia.Controls 与 FluentAvalonia；原生 Win32 托盘菜单可能更省，但本轮为保持外观和 zh-CN 字体而决定不做（静态分析层面的假设，未测量） | 未测量 | 菜单外观、字体、DPI 与深浅色回归；需 Windows 视觉验证 | 待处理（本轮已决定不做） | 2026-10-21 后端内存 |
| MEM-15 | `src/HeyMelody.Calibration/HearingRadarMath.cs:21-26`、`:96` @f415d29 | 预览期间每 50 ms 分配 double[6] 与 ReadOnlyCollection（静态分析） | 小 | 与结果雷达显示一致性需单元测试 | 待处理 | 2026-10-21 后端内存 |

## 界面与前端（UI）

| 编号 | 位置 | 现象与依据 | 预估收益 | 风险与依赖 | 状态 | 登记来源 |
| --- | --- | --- | --- | --- | --- | --- |
| UI-01 | `src/HeyMelody.Client/MainViewModel.Selection.cs:45` `ReadOnlyPresentation` @f415d29 | 用默认选项的 JSON 往返复制快照，可改用 `UiSnapshot.Clone()`（静态分析） | 减少界面端分配与反射序列化；未测量 | 须保持“只读展示不含暂存选择和 EQ 草稿”的语义；Client.Tests 覆盖 | 待处理 | 2026-10-21 后端内存（顺带发现） |
| UI-02 | `src/HeyMelody.App/RenderingSetup.cs:17`、`:22` @f415d29 | GUI 的 Skia GPU 资源上限为 256 MiB，而 Avalonia.Skia 12.1.4 不按时清理 GPU 缓存（静态分析，依据程序集元数据，未反编译） | 可能降低 GUI 长时间运行的显存与内存占用；未测量 | 影响三维模型与 120 Hz 渲染预算；需 Windows 原生窗口验证 | 待处理 | 2026-10-21 后端内存（顺带发现） |
| UI-03 | FluentAvaloniaTheme 3.1.0 包依赖 @f415d29 | 该主题包把 Avalonia.Controls.DataGrid 和 ColorPicker 带入每个 GUI 进程（包元数据，静态分析） | 减少加载的程序集；未测量 | 升级或替换主题包会影响外观，见 [FluentAvalonia 调研](research/fluent-avalonia.md) | 待处理 | 2026-10-21 后端内存（顺带发现） |

## 磁盘 I/O（IO）

| 编号 | 位置 | 现象与依据 | 预估收益 | 风险与依赖 | 状态 | 登记来源 |
| --- | --- | --- | --- | --- | --- | --- |
| IO-01 | `src/HeyMelody.Host/DeviceStateStore.cs:113-123`、`:137-165`、`:216`、`:277`；调用方 `BackendSupervisor.cs:600-607` @f415d29 | 每个 SPP 快照都重写并以 WriteThrough 落盘，因为 SampleTime 总在变；`:277` 为检查大小而序列化整份展示。建议仅 SampleTime 前进且小于 60 秒时跳过落盘（界面只显示 HH:mm）（静态分析） | 减少频繁的磁盘写与 fsync；未测量 | 须保持缓存展示与“上次连接时间”语义；设备状态缓存见[设备状态](device-state.md) | 待处理 | 2026-10-21 后端内存（顺带发现） |

## CPU、IPC 与启动（CPU）

| 编号 | 位置 | 现象与依据 | 预估收益 | 风险与依赖 | 状态 | 登记来源 |
| --- | --- | --- | --- | --- | --- | --- |
| CPU-01 | `src/HeyMelody.Contracts/RpcWire.cs:38-52` `ReadLineAsync` @f415d29 | 每次 ReadAsync 只读一个字符并写入 StringBuilder（行约 14 KB）（静态分析） | 降低 IPC 读取的 CPU 与异步开销；未测量 | 须保持每行 524,288 字符上限与畸形输入行为 | 待处理 | 2026-10-21 后端内存（顺带发现） |
| CPU-02 | `src/HeyMelody.Host/BackendPipeServer.cs:251`、`:314` @f415d29 | 同一事件对每个客户端分别序列化（静态分析） | 多客户端时减少重复序列化；未测量 | 每客户端按键合并的输出队列语义须不变 | 待处理 | 2026-10-21 后端内存（顺带发现） |
| CPU-03 | 快照事件，`src/HeyMelody.Contracts/UiContracts.cs:69-100` @f415d29 | 每次快照事件都重复发送不变的 AncModes、FeatureSections、EqualizerEntries（合成估计约 14 KB）；增量协议需提升 IPC schema（静态分析加合成估计，非真机） | 降低 IPC 流量与序列化；未测量 | 需提升 IPC schema 版本，与旧后台的升级处理见[后台架构](backend-architecture.md) | 待处理 | 2026-10-21 后端内存（顺带发现） |
| CPU-04 | `--catalog-worker` 启动，`src/HeyMelody.Host/NativeWorker.cs:301-316` @f415d29 | 每次目录刷新新建一个进程（已有 200 ms 合并）；进程隔离是设计要求（静态分析） | 进程创建开销；收益有限 | 进程隔离不能放弃；可作调度优化而非改变隔离 | 待处理 | 2026-10-21 后端内存（顺带发现） |
| CPU-05 | `src/HeyMelody.Contracts/RpcJsonContext.cs`（本轮新增，基准 @f415d29）与探针日志 | 源生成 JSON 首次使用仍 JIT 约 1,000 个方法（`JsonPropertyInfoValues<T>` 与上下文 lambda 以 Tier0 编译）；可试 GenerationMode=Serialization，或查明 crossgen 拒绝预编译的原因（Linux 测量，探针日志，合成负载） | 降低首次往返的 JIT 时间；未在 Windows 测量 | 须保持线格式字节兼容；Windows ReadyToRun 行为可能不同 | 待处理 | 2026-10-21 后端内存（顺带发现） |
| CPU-06 | `src/HeyMelody.Host/CalibrationAuditionPlayer.cs` `Fill` @f415d29 | 逐帧复制并带取模与边界检查，分段 Array.Copy 即可（静态分析） | 试听期间每 5 ms 的 CPU；小 | 须保持循环与声道映射输出样本逐位一致 | 待处理 | 2026-10-21 后端内存（顺带发现） |
| CPU-07 | `src/HeyMelody.Host/WindowsStateMonitor.cs:220` `Advertisement` @f415d29 | 每条匹配广播分配一个 DataReader 与 byte[]（按公司 ID 过滤后影响较小）（静态分析） | 小 | 须保持单包解析语义，需真机广播验证 | 待处理 | 2026-10-21 后端内存（顺带发现） |

## 发布体积（PKG）

| 编号 | 位置 | 现象与依据 | 预估收益 | 风险与依赖 | 状态 | 登记来源 |
| --- | --- | --- | --- | --- | --- | --- |
| PKG-01 | `Build-Client.ps1:219-222` @f415d29 | HeyMelody.Resources 的 Bundles/data（约 34 MB、635 个文件）同时复制进 GUI 目录和 backend；Host 只检查存在并把路径交给界面（静态分析） | 发布包减小约 34 MB 每份副本；未测量 | 去重前先确认界面实际读取哪一份；Verify-Release 与三架构包结构需同步 | 待处理 | 2026-10-21 后端内存（顺带发现） |

## 正确性（BUG）

| 编号 | 位置 | 现象与依据 | 预估收益 | 风险与依赖 | 状态 | 登记来源 |
| --- | --- | --- | --- | --- | --- | --- |
| BUG-01 | `src/HeyMelody.Host/BackendPipeServer.cs:159` @f415d29 | 值为 null 的 uid 或 color 被 WhenWritingNull 省略后，`GetProperty` 抛出（静态分析；既有行为，本轮未改） | 修复一个已有的边界缺陷，无内存收益 | 需先确认触发路径，修复须有测试依据 | 待处理 | 2026-10-21 后端内存（顺带发现） |
