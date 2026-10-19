# 开发、构建与验证

本文描述 0.9.2 的源码入口和现行脚本。本轮在 Windows 本地构建，尚未发布到 GitHub。用户操作见根目录 [README](../README.md)，协作规则与文档导航见 [AGENTS.md](../AGENTS.md)，过去版本的结果见[版本记录](history/release-notes.md)。所有命令均从仓库根目录运行。

## 环境与模块

日常客户端使用 Windows 与 .NET SDK 10，通过仓库 `NuGet.Config` 中的官方 NuGet 源恢复依赖。GUI 使用 Avalonia、FluentAvalonia；Windows 平台层目标为 `net10.0-windows`。发布包是自包含应用，终端用户无需安装 SDK。

| 入口或模块 | 职责 |
| --- | --- |
| `src/HeyMelody.Host` | 托盘后台、共享蓝牙连接及状态推送；主窗口关闭后继续运行 |
| `src/HeyMelody.App` | 主窗口、设备控制与外观展示 |
| `src/HeyMelody.DeviceManager` | 独立设备管理窗口 |
| `src/HeyMelody.FitTest`、`src/HeyMelody.HearingSetup` | 独立贴合度测试与黄金听感窗口，共用后台连接 |
| `src/HeyMelody.Calibration`、`src/HeyMelody.Calibration.UI` | 校准流程及界面；输入绑定设备版本，操作受每设备租约约束 |
| `src/HeyMelody.Protocol`、`src/HeyMelody.Control`、`src/HeyMelody.Daily`、`src/HeyMelody.Probe` | 协议、设置工作流、日常状态与只读探测 |
| `src/HeyMelody.Contracts`、`src/HeyMelody.Client` | 跨进程契约与客户端通信 |
| `src/HeyMelody.Resources`、`src/HeyMelody.Devices`、`src/HeyMelody.Artwork` | 资源解析、设备能力与外观资源 |
| `src/HeyMelody.Windows` | Windows 平台接口；`OfflineChecks` 是独立检查入口 |
| `src/HeyMelody.Audio`、`src/HeyMelody.Experiments` | 早期驱动与音频实验，另有依赖和历史验证范围 |

架构与连接流程见[后台架构](backend-architecture.md)，保留的研究工具见 [CLI 说明](cli-tools.md)。日常客户端、协议、设备配置和控制模块可从本仓库构建；早期驱动/音频实验使用相邻目录 `../manager/src/LhdcManager.Core`，对应私有伴随仓库为 `https://github.com/CberYellowstone/lhdc-manager`。该依赖属于可选实验，不是普通 GUI 构建的前置步骤。

## 源码构建与运行

按修改涉及的入口构建，例如：

```powershell
dotnet build src/HeyMelody.App/HeyMelody.App.csproj -c Release
dotnet build src/HeyMelody.Host/HeyMelody.Host.csproj -c Release
```

设备管理、贴合度和黄金听感的独立入口分别为表中的 `DeviceManager`、`FitTest` 和 `HearingSetup` 项目，使用同样的 `dotnet build ... -c Release` 形式。

在 Windows 上运行普通客户端，先在一个终端启动指定配置的后台：

```powershell
dotnet run --project src/HeyMelody.Host/HeyMelody.Host.csproj -c Release
```

再在另一个终端启动主窗口：

```powershell
dotnet run --project src/HeyMelody.App/HeyMelody.App.csproj -c Release
```

App 也能自动启动后台；源码运行时，若输出目录没有 Host 可执行文件，会依次查找 Host 的 Debug、Release DLL。开发验证使用上面的显式后台命令确定配置；切换配置前通过托盘退出已有后台。关闭主窗口会保留后台。

`Run-3D-Demo.ps1` / `Run-3D-Demo.cmd` 提供不连接耳机的布局演示，预装模型与演示范围见[用户说明](usage.md)及[模型验证记录](research/enco-free4-dynaudio-model.md)。`Run-ReadOnly.ps1` 和 `Run-HiRes.ps1` 是保留的研究入口，运行范围见 [CLI 说明](cli-tools.md)；涉及蓝牙、设置写入或播放的操作须依实际任务授权与对应硬件条件执行。

## 界面渲染与字体

三个 GUI 入口与 Host 托盘在 AppBuilder 阶段调用 `src/HeyMelody.App/RenderingSetup.cs` 与 `Typography.cs`（以源码链接编入各程序集，不新增 DLL）：固定 Win32 的 AngleEgl 与 WinUI／DirectComposition 合成（Mica 依赖前者），Skia GPU 资源上限 256 MiB、字形缓存 64 MiB；界面区域性固定为 zh-CN，默认字体为系统 Microsoft YaHei UI，汉字回退先于按区域打分固定到简体中文字体，550–699 字重使用真实粗体字面。没有雅黑的系统使用随附的 Noto Sans SC（`fonts/`，OFL-1.1）；Linux 无头预览与测试把该目录登记到 fontconfig，以路径方式打开字体，避免 Avalonia 12.1 从流加载字体时随窗口累积原生内存。共享样式与间距令牌在 `src/HeyMelody.App/Styles/HeyMelodyTheme.axaml`，代码构建的界面使用 `UiMetrics.cs` 中的同名数值。

设 `HEYMELODY_RENDER_DIAGNOSTICS=1` 或在窗口中按 Ctrl+Shift+F12 显示 Avalonia 的帧率、渲染与布局耗时叠加层，三维模型另显示 Ava3D 帧统计。验收 120 Hz 时在 120 Hz 显示器上展开／折叠设置项、拖动模型，并关注叠加层而不是空闲窗口的数值。

## 按改动选择验证

`tests/` 下的检查是可执行项目，使用 `dotnet run`，不是 `dotnet test`。Linux 上可以运行无头的 App、DeviceManager、Calibration.UI、Client、Calibration、Protocol、Resources、Devices、Control 与 Daily 检查；ModelPreview 的运动检查需加 `-r linux-x64` 构建。`--native*` 模式、Host／Probe／Audio 检查与 `Windows.OfflineChecks` 只在 Windows 运行。可用内存较少时，构建加 `-nodeReuse:false -p:UseSharedCompilation=false` 并在测试前执行 `dotnet build-server shutdown`，以免常驻编译进程挤占测试内存。先选择能覆盖改动的检查，例如协议或界面：

```powershell
dotnet run --project tests/HeyMelody.Protocol.Tests/HeyMelody.Protocol.Tests.csproj -c Release
dotnet run --project tests/HeyMelody.App.Tests/HeyMelody.App.Tests.csproj -c Release
```

完整默认检查入口为：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Build-Client.ps1 -TestsOnly
```

`Build-Client.ps1` 当前编排十三个默认 runner，包含资源、校准、GUI 和 `Windows.OfflineChecks`。默认检查通过模拟、资源与静态断言验证实现；应按改动选取验证，完整发布不是每次编辑的默认步骤。`HeyMelody.ModelPreview.Tests` 是独立模型检查；App、DeviceManager 和模型相关的 Windows 原生窗口检查也需单独运行。具体命令和适用条件见[三维模型渲染](model-rendering.md)及[研究文档索引](research/README.md)。

欢律单源改造的 UI 检查限于资源设置区、变更的电量／标题图标和校准步骤，其他默认 runner 照常执行。测试生成的完整 PNG 保留本地，不向会话载入整套截图；局部原生窗口检查可使用 App.Tests 的 `--native-smoke`，该模式不连接耳机或打开系统设置。

验证记录须说明对应层级与条件：

- 协议模拟、资源解析和无头 UI 检查证明对应代码或布局行为。
- Windows 原生窗口检查证明所用架构、驱动和渲染后端下的结果。
- 静态发布校验检查包结构、版本、运行时、资源及许可材料，不连接蓝牙。
- 真机记录须注明型号、固件、应用版本、命令范围与环境；旧 X4 记录不能证明所有目录型号、后续版本或 ARM64 实机行为。

内置资源仅包含欢律的 85 个耳机产品 ID。这是目录覆盖数量；未知 codec、缺少 SPP 或仅外部控制路由的功能会被禁用，目录条目本身不是硬件验证结论。跨窗口并发、校准流程、手机同时操作、休眠恢复及特定硬件的渲染仍需相应范围的实测证据。

## 发布与包校验

仅在任务涉及构建发布包时执行。脚本默认版本为 0.9.2，`-Version` 同时选择程序集与包版本，默认生成自包含 Windows x64、x86、ARM64 目录和合并 ZIP；本地生成产物不等于向 GitHub 发布：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Build-Client.ps1 -Version 0.9.2
```

GitHub Actions 的 x64、ARM64 构建产物保留 2 天，到期自动删除；工作流运行记录和日志继续按既有策略保留。

自 0.9.0 起所有 `dotnet publish` 统一启用 ReadyToRun（`-p:PublishReadyToRun=true`），预编译只含 IL 的界面库，避免首次展开设置、拖动模型或打开向导时的 JIT 停顿；发布包因此变大，x64 机器可交叉编译 ARM64。`-Architectures x64` 可选择单架构；`-SkipTests` 跳过默认 runner。普通构建拒绝已有目标目录或 ZIP；`-FinalizeOnly` 要求对应目录已存在且 ZIP 尚不存在，并跳过默认测试与 `dotnet publish`，封装已有输出。调整参数前先阅读脚本入口与条件，避免把跳过测试的产物称为完整验证。

App、DeviceManager、FitTest、HearingSetup 共用每个 GUI 目录的运行时与依赖，Host 位于 `backend`。GUI 和后台分别携带资源及第三方声明；提取资源、Ava3D 和 Skottie 的声明必须随包保留。脚本从 `packaging/README.md` 复制用户说明，从 `packaging/SOURCE-NOTES.md` 复制来源与验证说明，两者是发布素材，更新时需核对发布消费关系。

PowerShell 7 的 `tools/Verify-Release.ps1` 固定检查三架构目录和 `_AllArchitectures.zip`，收据固定写入 `release/HeyMelody_Windows_<版本>_STATIC-VALIDATION.json`，会覆盖同名文件。仅在收据尚不存在时使用下面的入口；已有收据时改用 Python 并指定新的输出文件：

```powershell
$staticReceiptPath = 'release/HeyMelody_Windows_0.9.2_STATIC-VALIDATION.json'
if (Test-Path -LiteralPath $staticReceiptPath) { throw '已有收据，请使用 Python 校验器另存新文件。' }
pwsh -NoProfile -File .\tools\Verify-Release.ps1 -Version 0.9.2
```

可选 Python 3 校验器通过 `--package` 选择发布目录，通过 `--output` 选择新收据文件；已有输出文件会被拒绝。它检查目录中的程序集、运行时、资源与许可，不读取 ZIP；压缩包与目录的一致性须另外核对。例如：

```powershell
New-Item -ItemType Directory -Path artifacts -Force | Out-Null
python tools/verify_release.py --package release/HeyMelody_Windows_0.9.2 --output artifacts/release-0.9.2-static-new.json | Out-Null
```

Python 默认检查三架构；上述目录由单架构构建生成时，添加对应的 `--architectures x64` 参数。输出文件名按本次验证另选；上例将正文输出留在收据中。两套校验均不执行应用，需保留准确的版本、架构与摘要。真实执行收据、原生窗口收据和历史只读蓝牙证据是额外证据，不能由静态检查替代。

## 来源与研究边界

研究检查若显式比较 APK 或原始归档，需原始本地输入；普通设备配置检查使用已跟踪 JSON 和内嵌资源，不要求获取完整研究原件。生成证据和二进制发布包通常不随 Git 源码提供。完整研究对象、失败分支和验证产物归档到 `research/`，公开整理与复现工具分别遵循[研究资料规范](research/organization.md)与 [tools/research 说明](../tools/research/README.md)。

Surface App 是布局参考；项目不分发其安装包、美术或字体资源。高码率/无损音频协商与耳机控制协议是独立问题，GUI 迁移或控件存在并不证明对应音频链路已验证。
