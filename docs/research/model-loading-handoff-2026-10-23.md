# 3D 模型加载提速交接书（2026-10-23）

本文交给在 **Windows** 上接手的会话，用来提升使用 3D 模型的机型（Enco Free4、Free4 Dynaudio 演示及欢律下发的 GLB）在左侧显示区的加载速度。开始前先读 [AGENTS.md](../../AGENTS.md)、[三维模型渲染](../model-rendering.md) 与 [Filament 构建与接口](../filament-renderer.md)。

- **用户授权的范围**：托管层改动加原生小改。
- **本文不是测量结果**：所有耗时判断都是对 `cb8171e`／`40acbb9` 之后代码的静态分析。Linux 开发机没有 Filament 运行时和 GPU，没有任何计时数据。
- **登记**：本工作在[优化待办](../optimization-backlog.md)中为 UI-11。

同一天已在 review/0.9.9 完成并推送“左侧加载改用 ProgressRing”：

- 3D、位图、Lottie、动态 WebP 和美术解析阶段统一显示延迟 200 ms 出现的 `HeroLoadingIndicator`。
- 3D 的指示器在首帧呈现时隐藏。
- 本文的改动不应改变这一表现。

## 1. 目标与约束

- **目标**：缩短两段时间：
  - 冷加载：本进程第一次显示模型，即从 `DeviceModelView` 附加到视觉树到首帧呈现（ProgressRing 消失）；
  - 热加载：切换设备后再回到模型。
- **不得**改变模型字节、贴图或资源；不得新增常驻进程或开机预启动。
- **完整性边界不变**：
  - 原生 `hm_load_model` 必须继续独立计算 SHA-256 并与 expected 比对；
  - 材质 profile 仍只按摘要授予（`FilamentModelProfiles.IsAuditedOpaque1001`、原生 `audited_asset(digest)`）；
  - 不能改为信任调用方传入的摘要。
- **内存**：`HeyMelody.exe` 私有工作集不得增加。`DeviceModelView.VerifiedModels` 的 LRU 容量 2 不变，不保留额外的模型副本或第二个 Engine。
- **CPU**：不得出现持续占用。并行只允许把原本串行的工作提前，不能重复。
- **分轮规则**：只做本方向。其他发现登记到优化待办。发布、真机操作按当次授权。

## 2. 当前加载链路（按符号定位）

### 托管侧（`src/HeyMelody.App`）

1. **`DeviceVisualCatalog.ResolveAsync`**：
   - 先 `AcquireArtworkAsync(..., "hero")`；种类为 model/glb/lottie/animated-webp 时，再串行 `AcquireArtworkAsync(..., "detail")` 取静态回退图。两次 RPC 都可能触发在线下载。
   - 之后在 UI 线程 `Complete` 中构造 `DeviceModelVisual(file, staticVisual)`。内置演示使用 `EncoFree4ModelView`／`EncoFree4DynaudioModelView` 的 `avares://` GLB。
2. **模板与附加**：`DeviceVisuals.DefaultTemplate` 构造 `DeviceModelView`（`EncoFree4ModelView.cs`）。`OnAttachedToVisualTree` 取得资源租约后调用 `ApplyRenderingPreference`，再调用 `LoadAsync`。
3. **`LoadAsync`**（全部在一个 `Task.Run` 内串行）：
   1. `await retirement`：上一个渲染器的退役，即 `DisposeRendererAsync` 销毁旧 Engine；
   2. `_lifetimeGate.WaitAsync`；
   3. `ReadVerifiedModel(_visual.File)`：先查 `VerifiedModels` LRU；未命中则整读约 9.7 MB，`FilamentModelData.CreateValidated` 在托管侧做一次 SHA-256；
   4. `_factory.Create(backend)`，即 `NativeFilamentRendererFactory.Create`：
      - `NativeBindings.Instance`、`GetProbePayload`；
      - **`NativeBackendProbe.Shared.EnsureAvailable`**：本进程内该后端第一次使用时，启动 `hm_filament_probe` 子进程，**在子进程里完整创建、诊断再销毁一次 Filament Engine**，用于隔离驱动在 noexcept 路径中的崩溃。成功只缓存在本进程内存中；
      - `new NativeFilamentRenderer`，即 `hm_create`：原生工作线程 `RendererState::initialize`，执行 `Engine::create`、renderer、scene、IBL；
   5. `created.LoadModel(model)`：pin 托管字节后调用 `hm_load_model`，同步等待原生工作线程完成；
4. **首帧**：`RefreshActivity` 启动 `RequestAnimationFrame` 循环。`PresentLatestFrame` 取到与配置尺寸一致的帧后，隐藏 ProgressRing。首帧包含 Vulkan／GL 管线创建（ubershader）、4×MSAA 和回读。

### 原生侧（`native/HeyMelody.Filament.Native/src/hm_filament.cpp`）

1. `hm_load_model` 把输入复制成 `std::vector<uint8_t> owned`，经 `invoke` 投递到工作线程，并 `future.get()` 等待。
2. `RendererState::load` 依次执行：
   1. GLB 头检查；
   2. **`sha256(bytes)`**：手写的逐块实现，函数开头 `std::vector<uint8_t> message(input)` 再复制一次整个输入，并逐字节 `push_back` 填充；
   3. `cgltf_parse`、`cgltf_validate`，外部 URI 检查，统计三角形；
   4. `drain(); unload();`；
   5. `AuditedMaterials`（`createUbershaderProvider`）、`AssetLoader::create`、`createAsset`；
   6. `createStbProvider` 加 `ResourceLoader::loadResources(asset)`，解码 PNG/JPEG 贴图；
   7. **`drain()`**（`flushAndWait` 加 `pumpMessageQueues`）；
   8. 包围盒、`addEntities`、`releaseSourceData`、`apply_configuration`。

### 两个内置 GLB 的构成（由仓库文件解析得出）

| 文件 | 大小 | mesh／primitive | 材质 | 贴图 | 非贴图字节 |
| --- | --- | --- | --- | --- | --- |
| `Assets/Devices/EncoFree4/model.glb` | 9,727,500 | 28／28 | 1 | 4 张 PNG，均为 2048×2048（1.93 MB、0.97 MB、2.91 MB、0.21 MB） | 约 3.65 MB |
| `Assets/Devices/EncoFree4Dynaudio/model.glb` | 9,863,456 | 20／20 | 1 | 4 张 PNG，均为 2048×2048（1.58 MB、1.00 MB、3.08 MB、0.24 MB） | 约 3.92 MB |

摘要：Free4 为 `c49540d3e5afd7af8bb5aa562d3dc00e35246361e6efc14fa142a749b7d22bcc`，Dynaudio 为 `7f85c66d397ee45163246ff745c37e52a70059e739be0af2283a30bfb689c2f6`（分别见 `EncoFree4ModelView.ModelSha256` 与 `EncoFree4DynaudioModelView.ModelSha256`）。

### 静态判断的主要耗时（待步骤 0 验证）

| 顺序 | 项目 | 何时发生 | 判断 |
| --- | --- | --- | --- |
| 1 | `hm_filament_probe` 子进程（进程启动加一次完整 Engine 创建与销毁） | 每次启动应用后的第一次模型加载，每个后端一次 | 很可能最大 |
| 2 | 进程内 `Engine::create`（Vulkan 实例、设备） | 每次加载（每个视图一个 Engine） | 大 |
| 3 | 4 张 2048² PNG 解码、上传与 mipmap | 每次加载 | 大；StbProvider 在 JobSystem 中解码，可能已并行 |
| 4 | 首帧管线编译、4×MSAA、回读 | 每次加载 | 中到大（与 UI-05 相关） |
| 5 | 原生手写 SHA-256 加两次 9.7 MB 复制 | 每次加载 | 中；ARM64 上更明显 |
| 6 | 托管读取加 SHA-256 | 冷加载（LRU 命中后为零） | 小到中 |
| 7 | 等待旧渲染器退役 | 切换设备时 | 视旧 Engine 销毁耗时 |

## 3. 步骤 0：先加计时，测基线（必须先做）

- **加计时**：在 `DeviceModelView.LoadAsync` 中用单调时钟（沿用 `_clock` 或局部 `Stopwatch`）记录各阶段：
  1. `attach→load start`
  2. `retirement wait`
  3. `gate wait`
  4. `read+verify`（区分 LRU 命中）
  5. `probe`（在 `NativeFilamentRendererFactory.Create` 内单独计时，可经 `FilamentDiagnostics` 或返回值带出；区分“已缓存”和“新启动子进程”）
  6. `engine create`
  7. `load model`
  8. `first frame`（到 `PresentLatestFrame` 隐藏 ProgressRing）
- **展示**：
  - 在诊断叠加层的 `UpdateDiagnostics` 文本加一行“加载 …ms（探测 …／引擎 …／模型 …／首帧 …）”。开启方式为 Ctrl+Shift+F12 或 `HEYMELODY_RENDER_DIAGNOSTICS=1`。
  - 每次加载完成时 `Trace.WriteLine` 一行结构化文本。
  - 计时只读，不改变行为；默认关闭时不分配额外字符串。
- **基线测量**：
  - Windows x64，后端 Vulkan 与 OpenGL 各测；
  - 冷加载：每次重启 `HeyMelody.exe`，托盘保持运行；
  - 热加载：同一进程内切换到其他设备再切回；
  - 每种至少 5 次，记录中位数与最大值；
  - 有 ARM64 真机时另测，没有就写明未测。
- **记录**：完整原始数据放 `research/model-loading-<日期>/`（本地档案），`docs/` 只写摘要。

## 4. 步骤 1：托管层改动

每项单独提交，或至少单独测量。

### (a) 读取校验与引擎创建、退役等待并行

在 `LoadAsync` 一开始（`Task.Run` 之前）就启动 `var modelTask = Task.Run(() => ReadVerifiedModel(_visual.File))`，与 `await retirement`、`_lifetimeGate` 等待和 `_factory.Create` 并行；`LoadModel` 前再 `await modelTask`。要求：

- **租约**：`using var loadingLease = _visual.File.Acquire()` 必须在读取开始前取得，并覆盖到读取结束。
- **取消**：取消或 generation 失效时仍要 await（或观察）`modelTask`，避免未观察异常；不得在失效后调用 `Create`。
- **失败分类**：读取失败（`InvalidDataException`）仍归入 `FailRenderer` 的“三维模型资源无效”。若读取已失败，不要继续 `Create`，避免白建 Engine。
- **内存**：LRU 命中时不会多读；未命中时与原来一样只有一份字节。

### (b) 美术解析完成即预读

`DeviceVisualCatalog.Complete` 生成 `DeviceModelVisual` 后，在线程池调用已有的 `DeviceModelView.LoadModelAsync(visual)` 填入 LRU。要求：

- 预读期间持有 `slot.Lease` 的一个 `Acquire()`，结束后释放；
- 异常吞掉，由视图自己的加载报告；
- 同一摘要已在 LRU 时跳过；
- 不改变 `VerifiedModelCacheCapacity`。

### (c) 提前并共享后端探测

`NativeBackendProbe` 目前“每个调用方自己的子进程”。若在 (b) 的时机就调用 `EnsureAvailable`，与视图里的调用会**重复启动两个子进程**。需要改为：

- 每个 `(payload, backend)` 只有一个进行中的探测 Task，后来者 `WaitAsync(token)` 加入；单个调用方取消不得取消共享探测；
- 失败与取消后仍可重试（保持现有 generation／`Invalidate` 语义）；
- 提前探测只在已解析出 `DeviceModelVisual` 时触发，使用当前 `ModelRenderingPreferences.Backend`；
- 不在应用启动时无条件探测（那属于预启动），也不为没有模型的设备探测。
- 测试：`tests/HeyMelody.Rendering.Filament.Tests` 中 `NativeBackendProbe` 的替身 runner。需新增并发加入、取消一方不影响另一方、失败可重试三项检查。

### (d) `FilamentModelData.CreateValidated`

expected 为空时目前先算一次摘要，构造函数再算一次。改为只算一次：新增内部构造路径，或让构造函数接受“已计算”的标志。公开构造函数的校验语义保持不变。

### (e) 需用户另行决定，默认不做：跨进程缓存探测结果

把“探测成功”按 `(运行库身份 payload.Identity, 后端, GPU 适配器 vendor/device/驱动版本)` 持久化到 `%LOCALAPPDATA%\HeyMelody\`，即可在每次启动时省去子进程。代价是失去对驱动崩溃的进程隔离，因此必须配合崩溃守护：

- 进程内 `Create` 前写入 pending 标记，成功后清除；
- 下次启动若发现残留 pending，丢弃缓存并恢复探测；
- 驱动或运行库任一变化即失效。

这属于取舍，**实施前须先向用户展示步骤 0 测得的探测耗时并取得同意**。

## 5. 步骤 2：原生小改

修改 `hm_filament.cpp` 会改变 `tools/filament/FilamentArtifactCache.psm1` 的指纹，CI 的 Windows 包任务（x64、arm64）会完整重编 Filament。参考同类构建约 15–20 分钟；任务上限 60 分钟。本地先按 §6 编译验证再推送。

### (a) SHA-256

- **Windows（`_WIN32`）**：
  - 使用 CNG 一次性哈希：`BCryptHash(BCRYPT_SHA256_ALG_HANDLE, nullptr, 0, data, size, digest, 32)`（Windows 10 起支持伪句柄，项目最低版本为 19041）；
  - 在 `CMakeLists.txt` 为 Windows 目标链接 `bcrypt`；
  - 返回码非 0 时抛 `std::runtime_error`，不回退为跳过校验。
- **其他平台**：保留现有实现，但改为就地处理完整的 64 字节块，只为末尾不足一块的数据加填充建一个 128 字节缓冲。去掉 `message(input)` 的整份复制和逐字节 `push_back`。
- **函数签名**：改为接收 `const uint8_t*`／`size_t`。
- **对拍**：
  - 空串 `e3b0c442…b855`；
  - `"abc"` `ba7816bf…15ad`；
  - 长度为 55、56、63、64、65 字节的边界输入；
  - 两个内置 GLB 的摘要（§2）。
  - 在 `tools/smoke.cpp` 或新的原生自检中加入这些向量；托管侧保留 `DynaudioModelTests` 的摘要错误拒绝检查。

### (b) `loadResources` 之后的 `drain()`

先读锁定 commit `ea31afeaf66339e7414aa648aa76c7e61adbde28` 的 `libs/gltfio/src/ResourceLoader.cpp`、`FFilamentAsset::releaseSourceData` 和 `StbProvider`，确认三点：

- 顶点与索引上传的 `BufferDescriptor` 是否引用 `cgltf` 源缓冲；
- `releaseSourceData` 释放的内容；
- StbProvider 解码完成后的 `setImage` 是否要求 flush。

只有源码证明上传已拷贝或已完成时，才移除这次 `drain()`，或把它推迟到首帧前由渲染循环自然推进；否则保留，并在文档写明理由。改动前后都要用步骤 0 的计时证明收益。

### (c) 可选，默认不做：`hm_load_model` 的 vector 复制

`invoke` 是同步等待，托管侧在调用期间保持 pin。改为不拥有数据的视图可省一次 9.7 MB 复制。但必须先确认 `load` 返回后没有任何对象引用输入（`createAsset` 加 `releaseSourceData`）。收益小、风险集中在生命周期，除非步骤 0 显示复制耗时显著，否则不做。

## 6. 构建与验证

在仓库根目录、PowerShell 7 中使用 `-NoProfile`：

```powershell
# 原生运行库（需要 Filament 源码；没有本地副本时用 -FetchSource，构建工具缺失时加 -FetchBuildTools）
# 脚本默认输出到 artifacts/filament；App 默认从 artifacts/filament-compat 取运行库（FilamentRuntimeRoot），因此显式指定。
pwsh -NoProfile -File tools/filament/Build-FilamentNative.ps1 -RuntimeIdentifier win-x64 -FetchSource -FetchBuildTools -OutputRoot artifacts/filament-compat
pwsh -NoProfile -File tools/Verify-FilamentRuntime.ps1 -Directory artifacts/filament-compat/runtimes/win-x64/native -RuntimeIdentifier win-x64 -RequireCurrent

# 托管与原生检查
dotnet run --project tests/HeyMelody.Rendering.Filament.Tests -c Release
dotnet run --project tests/HeyMelody.Rendering.Filament.Tests -c Release -- --native-recovery artifacts/filament-compat/runtimes/win-x64/native
dotnet run --project tests/HeyMelody.App.Tests -c Release
dotnet run --project tests/HeyMelody.App.Tests -c Release -- --native-smoke
dotnet run --project tests/HeyMelody.ModelPreview.Tests -c Release

# 人工核对与完整默认检查
.\Run-3D-Demo.ps1
pwsh -NoProfile -ExecutionPolicy Bypass -File .\Build-Client.ps1 -TestsOnly
```

- App.Tests 在 Linux 上有两项既有失败（BUG-03、BUG-04），与本工作无关；在 Windows 上也需先记录它们在改动前的状态，再比较。
- **新增或调整的检查**：
  - `ModelRendererLifecycleTests`：已有 ProgressRing 加载与首帧检查。为并行读取补一项：读取失败时不创建渲染器，取消时租约归零；
  - `FrameLoopTests`、`RecordingFilamentRenderer`：保持首帧与帧循环语义；
  - `DynaudioModelTests`：LRU 与摘要拒绝；
  - Filament.Tests：探测共享（§4c）。

## 7. 验收

1. 一张步骤 0 的前后对比表：冷加载、热加载 × Vulkan、OpenGL × 各阶段，记录中位数与最大值，并写明硬件、驱动、Windows 版本、构建 commit。
2. 摘要对拍全部通过；`hm_load_model` 对篡改字节仍然拒绝。
3. 两个内置模型和一个在线下载模型的画面与改动前一致：同一相机姿态截图比对，完整截图留在本地。
4. 加载后稳定状态下 `HeyMelody.exe` 的私有工作集与改动前相比不增加；用任务管理器“详细信息”或 `tools/Measure-HostMemory.ps1` 中 HeyMelody.exe 一行。
5. 进程数：除 §4(e) 之外，探测子进程数量不增加；共享后同一后端每进程只有一个。
6. CI「Windows packages」（x64、arm64，含 Verify-Release）与「Filament runtime」成功。

## 8. 文档与待办

- 更新 [三维模型渲染](../model-rendering.md)：加载顺序、探测共享、诊断叠加层的加载计时。
- 更新 [Filament 构建与接口](../filament-renderer.md)：SHA-256 实现、bcrypt 链接、`drain` 结论。
- 在[版本记录](../history/release-notes.md)记录本轮及验证层级（Windows x64 实测；ARM64 仅交叉构建，除非另有真机）。
- 更新[优化待办](../optimization-backlog.md)：UI-11 的状态；§4(e) 若未实施，另登记一条待用户决定的条目；与 UI-04（共享纹理）和 UI-05（MSAA 与分辨率）的关系写入备注。
- 提交说明写明测得的收益与未验证内容。不在 README／AGENTS 堆放测量流水。
