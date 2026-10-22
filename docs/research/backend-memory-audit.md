# 后台内存审计（2026-10-21）

本文是 review/0.9.9 分支“后端内存”方向的公开整理稿：问题、发现、已实施与暂缓项、Linux 测量结果和未验证内容。完整审计、设计、基线、探针与逐步结果在本机档案 [research/backend-memory-20261021/](../../research/backend-memory-20261021/README.md)，探针方法与原始数据见其 [probes/memory-probe/](../../research/backend-memory-20261021/probes/memory-probe/README.md)；`research/` 通常不随 Git clone 提供，缺少它们不影响阅读本文或正常构建。其他方向发现的优化登记在[优化待办](../optimization-backlog.md)，架构侧结论见[共享后台架构](../backend-architecture.md#资源占用)。

产品版本不变（源码仍为 0.9.2），尚未发布。本轮所有数字来自 Linux 容器；**Windows 上的内存没有测量**。验证层级分为静态分析、模拟或离线测试、Linux 测量、Windows 原生或真机验证，下文逐项标注。

## 问题与范围

用户报告 Windows x64 上 `HeyMelody.Host` 常驻时任务管理器显示约 80–100 MB，0.7.0 前后端分离（dd14bbe）时约 30 MB；后者的测量口径（单进程还是分组合计、是否连接耳机）未知。

本轮只处理后端内存：Host 托盘进程、`--worker` 与 `--catalog-worker` 子进程及其直接依赖库（Resources、Contracts、Daily、Calibration、Windows）。审计起点为 commit `f415d29`。任务管理器“进程”页可能把子进程合计到 Host 分组，按进程测量使用 [tools/Measure-HostMemory.ps1](../../tools/Measure-HostMemory.ps1)。

| 进程 | 内容 | 数量 |
| --- | --- | --- |
| Host 托盘 | Avalonia 托盘、BackendSupervisor、命名管道服务、WindowsStateMonitor、ResourceCoordinator | 1 |
| `--worker` | NativeHeadsetService、Daily、校准、试听、目录副本 | 每台已连接白名单耳机 1 个 |
| `--catalog-worker` | 同步原生枚举，每次目录刷新新建，结束即退出 | 瞬时 |
| `HeyMelody.exe` | 从托盘打开主界面时为 Host 的子进程 | 0 或 1 |

## 发现（静态分析，行号为 f415d29）

| # | 位置 | 发现 |
| --- | --- | --- |
| 1 | 无 runtimeconfig GC 配置 | 工作站 GC 的 gen0 预算由 L3 缓存大小推得（32 MB L3 约 16 MiB，大缓存 CPU 更高），托盘与每个 worker 各占一份 |
| 2 | `ResourceCoordinator.cs:397`、`:508`、`:273`、`:275-281` | 每次取图和每 10 分钟的包校验都整读文件做 SHA256（模型 9–10 MB）；下载整包进内存后解压 |
| 3 | `VendorResourceClient.cs:103-110` | MemoryStream 无预设容量加 ToArray（包上限 64 MB） |
| 4 | `ArtworkPackageExtractor.cs:117-130`、`:196-219`、`:327`、`:333` | vfxms、glb、webp 整读校验；Lottie 以 JsonNode 解析 |
| 5 | `RenderingSetup.cs:14-31`（Host `Program.cs:20`） | 托盘沿用 GUI 渲染配置：AngleEgl 加 WinUI／DirectComposition、Skia GPU 资源 256 MiB、字形缓存 64 MiB。Avalonia.Skia 12.1.4 不按时清理 GPU 缓存（依据为程序集元数据，未反编译） |
| 6 | `WindowsStateMonitor.cs:174-237` | Active 扫描没有广播过滤；每个广播包都在托盘内生成 CsWinRT 包装对象，仅靠 GC 与终结器释放（成本未测量） |
| 7 | `BackendSupervisor.cs:637` 及 `:515`、`:517`、`:582`、`:583` | `Copy` 是反射 JSON 往返；PublishLocked 在去重前就复制 |
| 8 | `RpcWire.cs:30-35`、`BackendContracts.cs:16` | 所有进程用反射序列化；每次构造 BackendRequest 都走一次反射 SerializeToElement |
| 9 | `CalibrationAuditionPlayer.cs:43-75`、`:206`、`:228` | 每次准备试听复制 4.4 MB WAV 约四份；每个填充周期新分配缓冲 |
| 10 | `ResourceCatalog.cs:37-85`、`BundledResourceStore.cs:37`、`BackendSupervisor.Resources.cs:124-132` | 目录保留原字节、整份 JsonElement 和逐 profile 副本；内置目录启动解析两次；每次启动 worker 都重算 gzip 加 base64 |
| 11 | `Program.cs:11-22` | JIT 编译 Main 时 worker 也加载 Avalonia 程序集；基线 strace 显示 `--catalog-worker` 打开 Avalonia.Base、Controls、Desktop 的 DLL 各一次 |

已核实为有界、无需改动：修订映射上限 128；每客户端输出队列按键合并并有上限；挂起的 worker 请求总被移除；客户端断开释放美术租约；单一 HttpClient；DeviceWatcher 与 Radio 处理器只注册一次。

## 已实施

| 项 | 内容 | 验证层级 |
| --- | --- | --- |
| GC 配置 | Host 的 runtimeconfig（托盘与所有 worker 共用）设 `System.GC.Gen0MaxBudget=4194304`、`System.GC.ConserveMemory=5`，关闭 TieredPGO | Linux 测量（gen0 预算）；ConserveMemory 与 Windows 效果未测 |
| 托盘渲染 | `Program.RunTray`（NoInlining）使用 Win32 软件渲染加 RedirectionSurface，不再初始化 GPU 与合成器，不再设置 Skia 缓存上限；保留 Typography，菜单仍由 Avalonia 绘制并使用 Microsoft YaHei UI；Host 不再链接 `RenderingSetup.cs`；worker 入口不再加载 Avalonia | 静态分析加 Linux strace（Avalonia DLL 打开次数 3 → 0）；菜单外观 Windows 未验 |
| 资源流式化 | 校验、取图和解压索引用 64 KiB 池化缓冲流式哈希；包下载经 `DownloadToAsync` 写入磁盘 scratch（`.download-*`），写入前检查长度上限并增量摘要，校验后用同一句柄解压；解压和 vfxms 切片有界复制并先做边界检查，失败时清理 scratch | 模拟或离线测试（Resources.Tests 34/34，含变异验证） |
| 目录 | 内置目录实例级 Lazy 只解析一次；`OriginalBytes` 仅在线目录保留；worker 的 gzip 加 base64 载荷按目录实例缓存 | 模拟或离线测试 |
| 安装后回收 | 美术安装后经去抖约 5 秒执行一次阻塞、压缩的 GC，单实例，停止中跳过 | 假时钟探针（离线）；Windows 效果未测 |
| 快照与 IPC | `UiSnapshot.Clone()` 替代反射 JSON 往返；`RpcJsonContext` 与 Host 私有 `HostJsonContext` 源生成，保留反射回退 | 模拟或离线测试（Client.Tests 173）加 Linux 测量；探针结论为保留 |
| BLE | 两个 Active watcher，各带一个字节模式过滤器（厂商数据段 `0xFF`、偏移 0、小端公司 ID），分别只接收 OPPO `0x079A`、OnePlus `0x072F` 的广播包；处理代码仍自行核对公司 ID，单包语义不变；失败或停止时先递增代次再解除委托并停止 | 静态分析；**须在 OPPO、OnePlus 真机验证** |
| 试听 | `AuditionWave.Read` 流式解析，PCM 直接读入 short[]（单拷贝）；`Fill` 与频谱发布复用缓冲 | 离线差分：60,000 例随机变异的样本哈希、异常类型与消息与旧实现一致；播放未在 Windows 验证 |

本轮新增检查：Resources 增 8 项（流式哈希边界尺寸对拍、下载限额与取消无残留、篡改回落、损坏资源跳过、vfxms 切片、GetCatalog 实例与 OriginalBytes 等）；Client 增 10 项（快照 Clone 4 项、线格式兼容 6 项）。临时回退关键改动时相应检查立即失败，说明检查有效。

## 暂缓项

本轮观察到但未实施的内存、CPU、I/O、体积与正确性项已登记在[优化待办](../optimization-backlog.md)：后端内存 MEM-01 至 MEM-15，另有界面 UI-01 至 UI-03、磁盘 IO-01、CPU 与 IPC CPU-01 至 CPU-07、发布体积 PKG-01 和既有缺陷 BUG-01。其中 MEM-14 记录了托盘改用原生 Win32 菜单的可能收益：本轮为保持外观和 zh-CN 字体而决定不做，收益未测量。

## Linux 测量结果

环境：Linux 容器，.NET SDK 10.0.401（运行时 10.0.12），2 核、约 8 GB 内存，AMD EPYC 7763（报告 32 MB L3）。探针为 R2R 自包含 linux-x64 发布，每次新进程，15 轮交错取中位数；测量时机器另有并行构建任务，一分钟平均负载 0.58–1.6。工作负载是合成的（约 14 KB 快照夹具），未包含真实 Host 或 GUI 进程。**绝对数值不可外推到 Windows，只比较相对差异。**

IPC 源生成，反射与源生成对比（保留条件全部满足）：

| 场景 | 指标 | 反射 | 源生成 | 差 |
| --- | --- | --- | --- | --- |
| gui | 首次往返 | 158.7 ms | 105.9 ms | −33% |
| gui | RssAnon | 30,388 KiB | 28,676 KiB | −1.7 MiB |
| host | RssAnon | 38,192 KiB | 32,824 KiB | −5.2 MiB |
| host | 稳态 µs/op | 693.8 | 610.4 | 快 12% |
| gui | 稳态 µs/op | 360.0 | 369.1 | 慢 2.5%（小于波动） |

源生成并不减少 JIT：首次往返的 JIT 方法数反而多 131（gui）与 228（host）。收益来自省掉反射元数据构建（首次分配降 53–59%）和更小的常驻堆。

GC gen0 预算：runtimeconfig 键 `System.GC.Gen0MaxBudget=4194304` 在 3/3 次运行中生效，每次 gen0 GC 约 3.97 MiB（默认 15.93 MiB），合成测试中的 gen0 次数由 128 增至 513，吞吐影响未测。环境变量对照须写成 `DOTNET_GCGen0MaxBudget=400000`（十六进制）；Linux 区分大小写，`GCgen0MaxBudget` 拼写不生效。Windows 上的默认预算由其 L3 推得，实际效果未验证。

TieredPGO 旁测（host 场景，n=5，仅作线索）：关闭后 RssAnon 再降约 4.1 MiB，JIT 方法数 3,031 → 2,322，前 2,000 次操作约快 35%，合成稳态约慢 16%。托盘与 worker 的 JSON 稳态吞吐不是瓶颈，按计划关闭。

## Linux 检查

下表为基线（`f415d29`）与本轮改后全量重跑的对比，均在同一台 Linux 机器上逐个运行；改后 Resources 与 Client 的增量来自本轮新增检查。

| 检查 | 基线（f415d29） | 本轮改后 |
| --- | --- | --- |
| Resources | 26/26 | 34/34 |
| Daily | 177 | 177 |
| Devices | 21/21 | 21/21 |
| Control | 19 | 19 |
| Protocol | 77 | 77 |
| Calibration | 55 | 55 |
| Client | 163 | 173 |
| DeviceManager | 8 cases | 8 cases |
| Calibration.UI | 13 | 13 |
| App | 129 | 129 |
| Host 编译（`-p:EnableWindowsTargeting=true`） | 0 warning / 0 error | 0 warning / 0 error |
| Host.Tests（Linux 子集） | 42 PASS，随后在需要 COM 的试听测试处因 PlatformNotSupportedException 停止（退出码 134） | 48 PASS（基线 42 项逐条保留，新增 6 项：试听 WAV 解析 2 项、worker 线格式 4 项），停点相同 |
| `--catalog-worker`／`--worker` 打开 Avalonia DLL 的次数（strace） | 3 | 0 |

Linux 上可用 `-p:EnableWindowsTargeting=true` 编译 Host 做静态检查；Host.Tests 仅在 Windows 完整运行。

## 未验证内容

以下均未在 Windows 或真机验证，不能由 Linux 结果推断：

- 托盘与各 worker 的私有工作集改前改后对比：用 [tools/Measure-HostMemory.ps1](../../tools/Measure-HostMemory.ps1) 在同一台机器、同一设备连接状态下分别测量 `f415d29` 与本轮构建。
- 完整 Host.Tests 与 `Windows.OfflineChecks`。
- 托盘菜单软件渲染在 Windows 10、11 的外观，以及 DPI、浅色与深色模式。
- OPPO、OnePlus 真机的盒盖、佩戴、电量广播；蓝牙开关、睡眠与恢复。
- 试听播放；在线美术的下载、解压与租约。
- `Verify-Release` 与整个发布包。
- 0.7.0 时约 30 MB 的测量口径；Windows 上 gen0 预算的默认值、ConserveMemory 的实际效果，以及 BLE 过滤省下的 CsWinRT 包装成本，均未测量。

## 后续验证与进展（2026-10-22）

本轮提交 `53a436a` 的 CI 全部成功：Windows x64 与 arm64 应用包（含 Verify-Release，仍是不执行应用的静态校验）以及 Windows、Ubuntu、macOS 的 Filament 运行时构建，上文“未验证内容”中的 Verify-Release 一项由此部分闭合。用户 2026-10-22 在 Windows x64 真机上用该 CI 构建试用了大部分功能，结论为“功能上没问题”；该结论只覆盖这一范围，不是逐项验证，也不是内存测量，Windows 私有工作集的改前改后对比仍未进行。上文“暂缓项”中的 MEM-10、IO-01、CPU-01、CPU-02 已在下一轮完成，见[后台能耗](backend-energy.md)。
