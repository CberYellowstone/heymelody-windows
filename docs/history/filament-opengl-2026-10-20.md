本页保留 Vulkan 默认后端切换前的文档原稿。当前说明见 [Filament 渲染模块](../filament-renderer.md)。

# Filament 渲染模块与跨平台构建

## 版本与职责

上游固定为 Google Filament `ea31afeaf66339e7414aa648aa76c7e61adbde28`。引擎、matc、uberz 和各后端材质 archive 必须来自同一 commit；OPLUS VFX ModelViewer 1.3.8 的 Android 类和 JNI 不作为本项目运行依赖。[上游平台说明](https://github.com/google/filament#apis)。

`HeyMelody.Rendering.Filament` 为不依赖 Avalonia、Host 或 Windows 平台层的 `net10.0` 组件。App 负责已校验 GLB、两份字节缓存、资源 lease、运动和 Avalonia 位图；C ABI 负责 Engine、模型、相机、原生离屏上下文与帧缓冲。离线测试通过替代 renderer 验证业务行为，不要求安装原生库。

## 原生接口与线程

ABI v1 的入口在 `native/HeyMelody.Filament.Native/include/hm_filament.h`：不透明 handle、定宽数值、UTF-8 错误信息；无 HWND、NSView 或 EGL 参数。原生 worker 串行管理 Engine，提交的渲染请求合并，最多保留三份像素缓冲。调用者复制最新完整帧，不接收跨托管边界的异步回调。

帧为物理像素、左上原点、RGBA8、opaque、sRGB，行跨度单独声明。App 在 UI 线程更新缓存位图。尺寸和模型变化使用 generation；销毁先停止发布并完成回读，再释放场景、buffer 和 Engine。GLB 保留 64 MiB 上限和 SHA 校验，已审计四个 SHA 的材质 `1001` 才应用 Opaque/depth-write 修正；未知资产不扩大补丁范围。

| 平台 | 后端 | 图形运行依赖 |
| --- | --- | --- |
| Windows | OpenGL/WGL headless | 支持所需 OpenGL 的系统驱动 |
| macOS | Metal headless | Metal 和相应系统框架 |
| Linux | OpenGL/EGLHeadless | libEGL 和兼容驱动；启用 `FILAMENT_SUPPORTS_EGL_ON_LINUX=ON` |

headless 表示没有可见原生窗口，不表示不需要图形驱动。Linux 不使用默认依赖 XOpenDisplay 的 GLX 路线。Avalonia 的界面合成与三维上下文独立，保留 Mica、圆角和弹窗叠层。

## 构建与产物

需要 PowerShell 7、.NET 10 SDK、Git、CMake 3.22.1 或更高和 C++20 工具链。Windows 使用 MSVC，macOS 使用 Xcode/Apple Clang，Linux 使用发行版 C++ 工具链及 EGL 开发包。普通托管构建不依赖本地 APK 或固定 research 目录。

```powershell
pwsh -NoProfile -File tools/filament/Build-FilamentNative.ps1 -RuntimeIdentifier win-x64 -FilamentSource B:/github/heymelody-windows/research/filament -FetchBuildTools
pwsh -NoProfile -File tools/Verify-FilamentRuntime.ps1 -Directory artifacts/filament/runtimes/win-x64/native -RuntimeIdentifier win-x64
dotnet run --project src/HeyMelody.App -- --3d-demo
```

源码位置可以替换为独立的同版本 clone。构建配置提供 win-x64、win-arm64、osx-x64、osx-arm64、linux-x64 和 linux-arm64；交叉架构需要相应工具链，不能通过改 RID 重命名二进制。`runtimes/<rid>/native/` 包含对应 DLL/dylib/so、`filament-runtime.json` 和实际链接组件许可证。manifest 记录 ABI、源 commit、后端、RID 和逐文件 SHA256。材质随目标后端生成，不能拿 Windows OpenGL archive 充当 Metal archive。

不指定源码目录时可以显式使用 `-FetchSource` 下载固定 commit 至构建缓存；`-Proxy http://127.0.0.1:59991` 可传给依赖下载。macOS/Linux 在相应系统运行同一 PowerShell 7 构建入口，分别选择 `-RuntimeIdentifier osx-arm64`／`osx-x64` 或 `linux-x64`／`linux-arm64`。Linux 需要 CMake、Ninja、Python 3、C++20 编译器和 EGL 开发包；跨架构预设没有匹配工具链时明确失败。

本分支 Windows 打包默认 x64：

```powershell
pwsh -NoProfile -File Build-Client.ps1 -Architectures x64 -FilamentSource B:/github/heymelody-windows/research/filament
pwsh -NoProfile -File tools/Verify-Release.ps1 -Version 0.9.2 -Architectures x64 -OutputPath artifacts/filament-release-static.json
```

Windows x64 主机安装 MSVC ARM64 编译工具链后，可以交叉构建 ARM64。下例将包放在独立目录，保留已有 x64 产物：

```powershell
pwsh -NoProfile -File Build-Client.ps1 -Architectures arm64 -OutputRoot release/arm64 -FilamentSource B:/github/heymelody-windows/research/filament
pwsh -NoProfile -File tools/Verify-Release.ps1 -Version 0.9.2 -Architectures arm64 -OutputRoot release/arm64 -OutputPath artifacts/filament-arm64-release-static.json
```

ARM64 原生模块与 .NET 自包含运行时均按目标架构生成；构建主机工具仍使用 x64。交叉构建不执行 ARM64 图形代码，实际 OpenGL 驱动、窗口和耳机功能需在 Windows ARM64 硬件上验证。

Windows ARM64 构建复用同 commit 的 x64 主机工具；resgen 使用 C 输出，再由 ARM64 编译器生成资源对象，BlueGL 选择上游已有的类型化 C++ 分发实现，避开写死 x64 的 COFF／MASM 路径。适配由本项目 CMake 和构建脚本复现，不改写原始 Filament clone。

发布校验强制检查原生库、架构、固定 commit、ABI、文件摘要及许可证，不允许缺少 renderer 仍通过。保留版本号不代表该试验已对外发布。

## 验证范围

便携检查：`dotnet run --project tests/HeyMelody.Rendering.Filament.Tests -c Release` 与 ModelPreview 默认运动检查。完整默认 runner 使用 `Build-Client.ps1 -TestsOnly`；Windows 原生模型模式另外执行。

`.github/workflows/filament-build.yml` 提供 Windows、macOS、Linux 的原生编译、payload 校验及离线测试，使用各 runner 实际进程架构。配置文件的存在不代表远端 CI 已执行。当前原生窗口运行验收范围为 Windows x64；macOS/Linux 构建成功也不代表蓝牙后台、安装包或对应硬件已验证。

UI 验证只载入变更的模型／设置局部，完整 PNG 留本地，其他回归使用 JSON 和日志。不设性能或工期停工闸门；保留跟随显示器、60、30 和系统 DPI。

## 2026-10-20 本轮验证

Windows x64／RTX 2060 的原生运行验证与包静态检查分别记录：

- 原十三个默认 runner 全部通过，其中最终 App 默认检查为 113 组；独立 Filament 便携检查 47 项，默认模型运动检查 180 项。
- 最终原生模型 277 项通过（Free4、丹拿、X3 黑），实际 App 窗口 59 项通过。覆盖完整源几何、材质白名单、RGBA/sRGB、窄视口、真实鼠标、隐藏恢复、卸载与回读生命周期；新增内部亮度层次检查，拒绝错误曝光产生的纯白轮廓。
- 原始 KTX 环境加载／清除、代次隔离和坏输入拒绝通过原生检查。当前 App 仍使用内置灯光配置，未宣称完整复刻 OEM 场景。
- 最终 DLL SHA256：`78861c7f3979e1170ed2f7d5cb87523ef2bf647b1893b8d94f132f8fc490fbf9`。两份最终原生收据为 `artifacts/filament-final-model-e6fbeb10/report.json`、`artifacts/filament-final-demo-55a96fec/native-demo-receipts.json`。
- x64 自包含试验包经 PowerShell、Python 两套静态校验；ZIP 核对 1,900 个路径／大小和 1,474 个关键摘要。完整记录在 `artifacts/filament/release-static.json` 与 `release-static-python.json`。另以隔离副本验证 ABI 声明、缺库、字节篡改、错误架构、路径越界、缺许可和错误 commit 会被拒绝。

受限构建进程通过命令级 `-p:UsedAvaloniaProducts=` 跳过写入用户目录的构建遥测，并从已有官方 NuGet 缓存恢复所缺 Crossgen2 包；未更改持久 SDK/Git 配置。上述窗口测试没有操作蓝牙、Host 或用户首选项。模型局部图已目检；其他完整 UI 图只留本地。macOS/Linux CI 已配置，当前未在对应系统执行或验证完整客户端。

### 同日补充：Windows ARM64 构建

在 Windows x64 主机使用 MSVC Hostx64→ARM64 工具链生成 Filament、主程序、设备管理器、校准向导与 Host 的 ARM64 自包含包。产物为 `release/arm64/HeyMelody_Windows_0.9.2_arm64.zip`，独立于已有 x64 包。原生 DLL 的 PE machine 为 `0xAA64`，10 个 ABI 导出齐全，只依赖系统 DLL；SHA256 为 `f8af4fea1420f392f80baab4663b7e04a09df1057b2f74940c3f78295fa3b128`。

新增 RID 后，可移植检查 51 项通过，PS/Python 架构与 manifest 正负例 16 项通过。ARM64 包通过两套静态校验，ZIP 核对 1,900 个路径／大小和 1,474 个关键摘要。收据分别在 `artifacts/filament/arm64-release-static.json`、`arm64-release-static-python.json`，构建记录在 `artifacts/filament/win-arm64-native-validation.json`。Native smoke 只编译、未执行；当前没有 ARM64 图形、完整客户端或蓝牙硬件验证，前述 x64 窗口结果不外推为 ARM64 结果。

独立 PE 审计覆盖 470 个文件：43 个原生和 261 个 ReadyToRun 文件均为 ARM64，166 个为合法 IL-only AnyCPU，未发现 x64 混入。此前 x64 ZIP 的摘要与提取目录的 1,900 个文件逐项摘要均保持一致。
