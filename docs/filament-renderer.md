# Filament 渲染模块与跨平台构建

## 版本与设置

固定 Google Filament commit 为 `ea31afeaf66339e7414aa648aa76c7e61adbde28`，运行库、matc、uberz 和材质 archive 使用同一版本。官方 App 的 OPLUS VFX Android 封装不作为运行依赖。

“3D模型渲染API”下拉框提供 Vulkan、OpenGL，默认选择 Vulkan；说明为“若存在渲染问题，请尝试切换到 OpenGL”。“3D模型渲染帧率”使用项目默认 FluentAvalonia Slider 样式，三个带刻度的吸附位置依次为 30帧、60帧、无限制，默认无限制。两种后端均为 GPU 渲染，保留系统原生 DPI。设置保存后立即应用到当前和之后创建的模型视图，取消不改变设置。帧率单独变化不重建 Engine。

帧率键 `modelFrameRate` 只保存字符串 `30`、`60`、`unlimited`；旧 `display`、试验版 `120`、缺失及非法值按无限制读取，已保存的 30、60 帧保留。30、60 帧调度按动画回调的真实时间累计额度并保留相位余量，继续使用修正后的整数限帧逻辑；无限制档不设额外上限，每个回调最多请求一帧，不新增独立渲染循环或垂直同步控制。长停顿不补帧，切档和恢复时重置额度。实际帧率还受窗口回调频率和渲染耗时限制。

偏好键为 `modelCompatibilityMode`，只有 JSON 布尔值 true 选择 OpenGL；缺失、false、非法值均选择 Vulkan。旧 `modelGpuAcceleration` 不再读取，下次保存移除旧键，保留主题、帧率和其它字段。保存失败时本次应用保留并提示，下次启动使用实际保存的文件。

| 平台 | 默认后端 | 兼容后端 |
| --- | --- | --- |
| Windows x64 / ARM64 | Vulkan | OpenGL / WGL headless |
| Linux x64 / ARM64 构建准备 | Vulkan | OpenGL / EGLHeadless |
| macOS x64 / ARM64 构建准备 | Metal | 不显示3D模型渲染API选择项 |

固定版本实际最低要求 Vulkan 1.1，并检查相应设备能力及扩展；不能仅依据上游 README 的 Vulkan 1.0 描述判断兼容。Vulkan loader 动态加载，未安装 Vulkan 的机器仍可使用 OpenGL。应用不捆绑 GPU ICD 或替用户安装驱动。Linux 的 OpenGL 路径保留 `FILAMENT_SUPPORTS_EGL_ON_LINUX=ON`，需要系统 EGL 与兼容驱动。

Vulkan 不可用时使用合法静态外观并提示手动选择 OpenGL，缺少同款图片的演示使用通用耳机图标。OpenGL 失败显示普通不可用提示。设置和实际后端不自动改变；缺失组件、ABI 错误及模型错误单独分类，避免误导用户切换后端。

## 接口、帧与生命周期

`HeyMelody.Rendering.Filament` 为不依赖 Avalonia、Host、Windows 项目或默认 RID 的 net10.0 托管组件。App 管理完整 GLB 校验、两份字节缓存、资源租约、运动与 WriteableBitmap。

C ABI 为版本 2，创建、配置、帧信息、诊断结构体仍分别为 16、64、48、96 字节。创建参数以 requested_backend 明确指定 OpenGL=1、Metal=2、Vulkan=3，不接受隐式自动后端。托管工厂接收明确后端及取消令牌；诊断记录请求与实际后端，实际值由 Engine 获取并校验一致。ABI 1 不能被新托管库静默使用。

原生 worker 串行管理 Engine、模型与回读。帧仍为物理像素、左上原点、RGBA8、opaque、sRGB，最多三份缓冲，调用方复制最新完整帧。Vulkan 没有改变 GPU 读回→CPU→Avalonia 位图上传这条链路。Avalonia 的 Skia / ANGLE 与窗口 composition 配置独立。

切换后端时先使旧加载代次失效、停止发布旧帧并取消加载，完成旧 renderer 的未完成回读和销毁，再用原有已验证字节创建所选后端。保留相机、拖动惯性、缓存、租约和 FPS；隐藏、最小化、卸载仍暂停，过期结果及时释放。

模型大小仍限 64 MiB；原始模型 SHA 校验、四个已审计 SHA 的材质 1001 修正及欢律单源约束继续生效，未知资源保留原始材质语义。

## 原生探测

Filament 的部分初始化失败发生在自己的驱动线程或 noexcept 路径，外围 catch 无法完整捕获。运行库因此携带 `hm_filament_probe`（Windows 为 .exe），先在隐藏子进程实际创建、诊断并销毁请求后端。

探测在后台执行，支持取消；30 秒无结果终止该次探测。只缓存当前进程中已完成的成功结果，按后端与原生产物摘要区分，运行失败后使其失效；失败不缓存。探测程序、库和 manifest 缺失或不匹配归类为组件错误。初始化探测不提供持续渲染阶段的驱动崩溃隔离。

## 构建与产物

需要 PowerShell 7、.NET 10 SDK、Git、CMake 3.22.1+ 和 C++20 工具链。Windows 使用 MSVC；ARM64 交叉构建需要 Hostx64→ARM64 工具。macOS 使用 Xcode / Apple Clang；Linux 使用 Clang / clang++、Python、Ninja 和 EGL 开发包；固定 Filament 不支持默认 GCC 工具链。普通托管构建不要求 APK 或 research 目录。

```powershell
pwsh -NoProfile -File tools/filament/Build-FilamentNative.ps1 -RuntimeIdentifier win-x64 -FilamentSource B:/github/heymelody-windows/research/filament -FetchBuildTools
pwsh -NoProfile -File tools/Verify-FilamentRuntime.ps1 -Directory artifacts/filament-compat/runtimes/win-x64/native -RuntimeIdentifier win-x64 -RequireCurrent
dotnet run --project src/HeyMelody.App -- --3d-demo
```

源码目录可以替换为同版本独立 clone，也可显式指定 -FetchSource 下载固定 commit；受限网络支持 -Proxy http://127.0.0.1:59991。默认原生输出为 artifacts/filament-compat；App 和 Build-Client 支持显式 FilamentRuntimeRoot。Windows 手工获取源码时，对 Git clone / checkout 使用命令级 `git -c core.longpaths=true ...`，避免第三方依赖的深层路径超过限制；无需修改持久 Git 配置。

Ubuntu 构建示例（ARM64 主机将 RID 改为 `linux-arm64`）：

```bash
sudo apt-get update
sudo apt-get install -y clang ninja-build libegl-dev libgl-dev libvulkan1
CC=clang CXX=clang++ pwsh -NoProfile -File tools/filament/Build-FilamentNative.ps1 -RuntimeIdentifier linux-x64 -FetchSource -OutputRoot artifacts/filament-compat
```

Linux 桥接与 Filament 统一使用 Clang 默认的动态 libstdc++，运行时需要 `libstdc++6`、系统 EGL 及兼容图形驱动；Vulkan 后端另外使用系统 `libvulkan1`。

Windows ARM64 使用 -RuntimeIdentifier win-arm64。它复用同版本 x64 主机工具，resgen 生成 C 数组交给 ARM64 编译器，BlueGL 使用上游已有的类型化 C++ 分发实现；原始 Filament clone 保持不变。macOS/Linux 使用对应 RID，构建工具验证实际 OS、CPU 和产物架构，不能只改 RID 重命名文件。

Windows/Linux 同时启用 Vulkan 和 OpenGL，由上游材质编译参数一次生成双后端 archive。runtimes/<rid>/native 包含库、探测程序、materials/gltfio.uberz、清单及许可证。schema 2 清单记录 ABI 2、commit、RID、supportedBackends、defaultBackend、材质后端集合和逐文件 SHA256；它描述编译能力，不宣称机器驱动可用。历史 schema 1 包仍可静态校验，新构建必须通过 -RequireCurrent。

链接声明包括 Filament、cgltf、stb、Draco、meshoptimizer、BasisU、MikkTSpace、robin-map、zstd、BlueVK、Vulkan Memory Allocator、smol-v 和 SPIRV-Headers 等。材质工具依赖的来源与许可证另保留于构建档案。

新的单架构包使用独立输出根目录：

```powershell
pwsh -NoProfile -File Build-Client.ps1 -Architectures x64 -OutputRoot release/filament-compat/x64 -FilamentSource B:/github/heymelody-windows/research/filament
pwsh -NoProfile -File Build-Client.ps1 -Architectures arm64 -OutputRoot release/filament-compat/arm64 -FilamentSource B:/github/heymelody-windows/research/filament
pwsh -NoProfile -File tools/Verify-Release.ps1 -Version 0.9.2 -Architectures x64 -OutputRoot release/filament-compat/x64 -OutputPath artifacts/filament-compat/release-x64-static.json
```

## GitHub Actions 原生缓存

Windows 应用构建与 portability 的 Windows job 共用原生运行时准备入口。缓存单位是完整的 `runtimes/<rid>/native/`，包含库、探测程序、材质、清单和许可证，并附独立的缓存身份回执。x64、ARM64 分开保存；源码、CMake 工作目录及宿主工具不进入缓存。macOS/Linux 的构建路径暂时维持原有方式。

缓存键包含固定 Filament lock、原生桥接源码、原生构建脚本、目标 RID、后端与 Release 配置，以及实际选择的 MSVC、Windows SDK、CMake、Ninja、Python 工具链身份。工具链选择由缓存探测和实际构建共用，绝对目录、普通 C#／界面／文档和项目提交 SHA 不参与缓存键。修改 Filament、桥接代码或相关构建输入会自动重建；仅修改托管客户端时可继续复用。

工作流以 `actions: read` 查询指定工作流的历史 artifact，只接纳本仓库可信 push／手动运行生成的匹配缓存。PR 可以读取这些缓存，但不会发布到可信缓存池。缓存可在原生校验完成后使用，不要求整个 portability 工作流成功，因此其他平台失败或后续客户端检查失败不会使已验证的 Windows 运行时失效。

恢复时先下载到独立目录，核对完整身份、文件路径、ABI、commit、RID、后端、材质及 SHA256，校验通过后才交给 `Build-Client -FilamentRuntimeRoot`。最多尝试最近三个合法候选，整个恢复阶段最多三分钟；未命中、过期、权限不足、网络／解压失败或文件损坏均记录原因，并在当前 job 的新目录下载固定源码、编译和重新校验。缓存服务故障不会单独阻断构建；真实原生编译或校验失败仍会中断任务。

原生缓存 artifact 保留 30 天。可信运行在命中后也重新上传已验证的运行时，使活跃构建滚动续存；仅下载旧 artifact 不会延长其有效期。长期没有构建而导致缓存过期时，下次自动重建。缓存上传失败只警告，当前有效运行时继续用于客户端构建。手动运行的 `force_native_rebuild` 默认关闭，开启后跳过历史缓存并重建。每次 job 的 summary 记录缓存状态、来源、回退原因、构建指纹及保留规则。

应用包与原生缓存分别存储：实际 `refs/heads/main` 的 push／手动构建应用包保留 7 天，其他分支和 tag 保留 1 天；PR 编译与验包但不上传应用包，检查回执保留 1 天。应用包上传失败仍视为任务失败。本流程不使用 Telegram secret。

缓存与路由的离线检查不连接 GitHub 或 GPU：

```powershell
pwsh -NoProfile -File tools/filament/tests/Test-FilamentArtifactCache.ps1 -OutputPath artifacts/filament-cache-checks/local.json
```

实际 Actions 验证须分别记录 x64／ARM64 的冷构建、热命中、应用包验包和 artifact 到期时间；ARM64 交叉构建仍不能证明 ARM64 真机运行。

## 验证范围

可移植渲染检查、默认模型运动检查及原十三个默认 runner 保持各自入口。后端选择、偏好迁移、取消、错误分类和静态回退测试使用隔离设置与替代 renderer，不依赖 GPU。

Windows x64 的实际模型及窗口测试分别选择 Vulkan、OpenGL，记录请求/实际后端与精确 DLL / 材质摘要；检查几何、材质、颜色/方向/alpha、DPI、交互、隐藏恢复、切换和释放。原生探测失败、超时、取消和无 Vulkan 时的 OpenGL 路径另行验证。

ARM64 交叉构建和静态验包不等于 ARM64 GPU 或耳机实机验证。macOS/Linux CI 配置的存在不代表在对应系统已完成完整客户端验证。后端迁移证据索引在 artifacts/filament-compat/VALIDATION.md；恢复无限制档的新包与验证记录在 artifacts/model-unlimited-20261020/VALIDATION.md，包输出目录为 release/model-unlimited-20261020。此前固定 120 帧试验的记录与产物仍保留在 artifacts/model-ui-20261020 和 release/model-ui-20261020。

UI 只检查模型、错误提示、渲染设置和底栏设备图标局部，完整图片保留本地。不新增性能、分辨率或工期准入条件。

切换前的构建命令、原生 ABI 1 与 OpenGL 验收记录保存在[历史原稿](history/filament-opengl-2026-10-20.md)，不作为当前设置行为说明。
