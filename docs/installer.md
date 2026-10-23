# Windows Inno Setup 安装器

安装器是现有自包含客户端目录之外的本地可选产物，使用与 [VS Code 官方 Windows 安装脚本](https://github.com/microsoft/vscode/blob/main/build/win32/code.iss)相同的 Inno Setup 技术，采用 `WizardStyle=modern`。目前支持 Windows x64 与 ARM64，每个安装器包含一种架构；应选择与系统原生架构一致的安装器。GitHub Actions 继续生成目录与 ZIP，不编译或上传安装器。本轮不调整应用版本，也不发布 GitHub Release。

## 本地打包

使用 Windows、.NET 10 SDK、PowerShell 7.4 及以上和 [Inno Setup](https://jrsoftware.org/isdl.php) 编译器 `ISCC.exe`。先按[开发指南](development.md#发布与包校验)生成并校验客户端，再在仓库根目录的 PowerShell 7 会话运行：

```powershell
.\Build-Client.ps1 -Version 0.9.2 -Architectures x64,arm64
.\Build-Installer.ps1 -PackageRoot release/HeyMelody_Windows_0.9.2 -Architectures x64,arm64
```

`-PackageRoot` 必填，指向含 schema-2 `manifest.json` 和架构子目录的完整客户端目录；版本取自清单。`-Architectures` 默认 `x64`，也可选择 `arm64`。`-OutputRoot` 默认 `release/installers`。可用 `-InnoCompilerPath` 显式指定 `ISCC.exe` 的路径。多架构参数请在 PowerShell 会话内传入数组，避免 `pwsh -File` 把逗号列表当成单个字符串。

每个架构输出 `HeyMelody_Windows_<版本>_<架构>_Setup.exe` 和 `.exe.json` 收据。当前收据为 `schemaVersion: 2`、`installerFormat: "inno-setup"`，记录输入清单及安装器 SHA256、文件清单和验证边界，并以仓库相对的 `helperPath` 与 `helperSha256` 标识构建时保留的独立辅助工具。已有产物会被拒绝，不自动覆盖。每次构建在 `artifacts` 下新的独立工作目录保留中间文件，便于复核。客户端输入目录不被修改；安装器保留选定架构的完整运行时、资源、许可和来源说明，并把载荷清单缩减为实际包含的一种架构。

独立的 `HeyMelody.InstallerSupport` 工具以相同架构的裁剪、自包含单文件构建，临时解压后用于安装与卸载检查，不要求电脑预装 .NET，也不依赖正在删除的客户端运行时。打包脚本不调用 GitHub，不将安装器加入 `Build-Client.ps1` 或 Actions。

## 安装、更新与卸载

仅提供当前用户安装：Inno Setup 使用 `PrivilegesRequired=lowest`，默认 `%LOCALAPPDATA%\Programs\HeyMelody`，目录页允许自定义当前用户有写权限的位置。卸载登记写入 HKCU 的 32 位视图 `Software\Microsoft\Windows\CurrentVersion\Uninstall\HeyMelody_is1`，可从 Windows“应用和功能”移除程序，不添加开机自启。

界面使用 Inno Setup 的 [modern 安装向导](https://jrsoftware.org/ishelp/topic_setup_wizardstyle.htm)，目录页可直接编辑完整路径或浏览文件夹，完成页集中显示桌面快捷方式与启动选项。实际布局、缩放和复选框交互仍须对应版本的运行验收。

最后一页提供“创建桌面快捷方式”：首次默认勾选，此后沿用上次保存的选择，包括取消勾选。点击“完成”或直接用 X 关闭完成页，都会保存并应用当前选择。该选择独立保存在 `%LOCALAPPDATA%\HeyMelody\installer-preferences.json` 的 JSON 布尔字段 `desktopShortcut`，不写入应用的 `preferences.json`；卸载保留该文件。“打开 HeyMelody”默认不选中，仅在勾选后点击“完成”时启动，关闭窗口不启动。

静默安装读取已保存的桌面选项，文件缺失时按 `true` 处理，据此创建或移除本安装器拥有的桌面快捷方式，但不创建、写入或重置 `installer-preferences.json`。因此首次静默安装会默认创建桌面快捷方式。卸载会移除安装器创建的桌面快捷方式，保留已保存的选择。

开始菜单只在当前用户 Programs 根目录添加 `HeyMelody.lnk`，指向主程序；不创建 HeyMelody 子目录，也不添加设备管理器、离线演示或卸载的菜单项。安装器只接受 Windows 10 2004（19041）及以上，并核对原生系统架构。[静默安装参数](https://jrsoftware.org/ishelp/topic_setupcmdline.htm)使用 `Setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /DIR="完整目录"`，含空格的目录用引号包围。未指定 `/DIR=` 时优先沿用已登记的安装目录。

更新前关闭所有窗口，并通过托盘菜单退出 Host。安装器检查目标目录及旧安装的 HeyMelody 进程，不主动结束进程；安装或卸载程序互斥，避免同时修改文件。更新时核对已有安装和目标位置；卸载只删除安装器登记的程序文件，并尝试移除空目录，不递归删除自定义安装位置。设置、名单、缓存等 `%LOCALAPPDATA%\HeyMelody` 数据以及安装目录内额外放置的文件均保留。Inno 卸载器为安装目录内的 `unins000.exe`，需要同目录的 `unins000.dat`；[静默卸载](https://jrsoftware.org/ishelp/topic_uninstcmdline.htm)可直接运行 `unins000.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART`，不要单独复制 EXE 后执行。

无法启动旧卸载器、程序仍在运行或文件无法删除时，更新会停止并返回失败。独立临时工具允许在部分客户端文件已丢失后重试卸载。兼容设置失败时会显示警告并返回非零退出码，已经安装的程序与卸载入口仍然保留；不会把损坏的偏好文件替换为空配置。

## 旧款 Qualcomm GPU 的兼容默认值

安装阶段使用 Windows SetupAPI 枚举当前存在的 DISPLAY 类设备，读取 `SPDRP_HARDWAREID`，仅匹配 `ACPI\VEN_QCOM&DEV_043A`（可带十六进制 `REV_` 后缀）或 `ACPI\QCOM043A` 的硬件标识。匹配后把当前用户 `%LOCALAPPDATA%\HeyMelody\preferences.json` 根对象的 `uiBasicRenderMode` 写为 JSON 布尔 `true`，包括原先明确为 `false` 的情况；其他字段保留，写入使用同目录临时文件替换。已经为 `true` 时不重写。

`043A` 家族覆盖旧款 Adreno 680／685／690。此处按硬件家族提供保守默认，不按产品名称、CPU 架构或 GPU 名称猜测：`0636`（8cx Gen 3／Surface Pro 9 5G 的 Adreno 690）、`0C36`（X 系列）、其他 GPU 和未知标识均不读取或修改偏好文件。未取得证据的 Adreno 540／630／618 不加入匹配范围。用户仍可在应用设置中关闭 Basic Render；再次安装到匹配设备时会重新启用。

若匹配设备上仍有任何目录的 HeyMelody 窗口或后台运行，工具会停止设置步骤，避免覆盖运行中窗口保存的偏好。已有配置损坏、根不是对象、编码无效、无法读取或写入时保留原件并报告失败。Basic Render 的 WARP／Software 后备及重新打开窗口生效规则见[开发指南](development.md#界面渲染与字体)。

依据与边界：

- [Chromium 固定版本的 Windows ARM GPU 映射](https://chromium.googlesource.com/chromium/src/+/6cce000efb9f288a9d51d42c7ab38b38beb5d77c/content/test/gpu/gpu_tests/util/host_information.py) 明确区分旧 680／685／690 的 `043A`、新 690 的 `0636` 和 X 系列的 `0C36`。这些是 Qualcomm ACPI 标识，不能按普通 PCI 数字套用。
- [Avalonia 透明窗口报告 #10405](https://github.com/AvaloniaUI/Avalonia/issues/10405)、[Basic Renderer 策略 #13538](https://github.com/AvaloniaUI/Avalonia/pull/13538) 与本仓库的[上游历史研究](research/avalonia-adreno-history-2026-10-23.md)提供兼容默认的背景；不是每个旧设备和驱动版本均已复现的证明。
- [Microsoft DISPLAY_DEVICEW 文档](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/ns-wingdi-display_devicew) 未承诺 `DeviceID` 的用途，因此不用它推断硬件；使用 [SetupDiGetDeviceRegistryPropertyW](https://learn.microsoft.com/en-us/windows/win32/api/setupapi/nf-setupapi-setupdigetdeviceregistrypropertyw) 读取正式的硬件属性。

## 验证入口

```powershell
dotnet run --project tests/HeyMelody.InstallerSupport.Tests -c Release
dotnet run --project tests/HeyMelody.App.Tests -c Release -- --rendering-settings-tests
dotnet run --project tests/HeyMelody.App.Tests -c Release -- --rendering-policy-tests
.\tools\Test-Installer.ps1 -InstallerPath release/installers/HeyMelody_Windows_0.9.2_x64_Setup.exe -TestRoot artifacts/installer-acceptance-new
```

辅助工具验收使用注入硬件、独立偏好文件和进程快照，不连接耳机。安装器验收会真实写入当前用户的卸载登记、开始菜单与桌面，因此发现已有 Inno／旧 NSIS 登记、平铺入口、旧菜单目录或同名桌面快捷方式时拒绝运行。测试从 schema-2 收据解析实际辅助工具，或用 `-HelperPath` 指定仓库 `artifacts` 内的副本，二者均须匹配 `helperSha256`。先执行工具的只读 `probe-compatibility`：无匹配返回 0，受影响 `043A` 返回 20 并在安装前停止，错误返回 1；不通过启动安装向导进行探测。

随后测试安装到新的指定目录，检查自定义路径、升级、单一开始菜单入口、按现有选择创建或不创建桌面入口、保留额外文件，以及应用与安装器两份偏好文件的摘要不变。锁定文件导致卸载预检失败时，原载荷、卸载登记和快捷方式应完整保留；解除锁定后，测试仅在受控测试目录内精确删除 `hostfxr.dll` 模拟部分运行时缺失，再从原路径重试卸载，验证独立辅助工具不依赖客户端运行时。成功卸载后清理私有 `.installer` 辅助文件，结束后保留本地回执与用户自留文件。不把非匹配机器上的结果当成旧 GPU 真机验证。

ARM64 的交叉打包、PE 架构检查和注入 ID 测试，不等于在 ARM64 或受影响 GPU 上执行了安装器。无需加载整套应用截图；本轮不改变 Avalonia 窗口布局。

## NSIS 历史验证（2026-10-24）

客户端载荷取自 `909d9871682c8b9ff80056249192f0ed6a95f521` 的成功 Windows packages CI（run `39951237018`），应用版本 `0.9.2`。使用 NSIS 3.11 编译 x64 与 ARM64，未修改应用程序集或工作流。最终安装器 SHA256：

- x64：`c3408a8452b3c13f6b86be906274469fff3be8fe5cd449533f4b8ed35453b12b`。
- ARM64：`5f73f15f20a9ac085b4ba07823bd900286e09cbda43a734ce78029d7ae40634a`。

辅助工具 13 组无 UI 验收通过，包含安装器父进程不会阻止兼容值写入的回归检查；裁剪后的自包含 x64 工具与 13 组测试也执行通过。Basic Render 设置验收 3 组与渲染策略 21 项通过；Calibration、Devices、Control、Daily、Probe 和 Filament 便携检查通过。默认检查的 Windows OfflineChecks、Protocol 部分先行通过，Resources.Tests 首次为 39/41、独立复核为 37/41，失败项漂移，与已登记 [BUG-05](optimization-backlog.md) 的现象相近，根因未闭合；本轮未修改该模块。Host／Client 的补充运行在 NuGet 受限恢复阶段停止，没有记为测试通过。

x64 真实静默验收通过：中文与空格自定义目录、注册项和当时的四个开始菜单快捷方式、升级保留额外文件、锁定运行时文件后的失败卸载与再次成功卸载，用户偏好字节不变。该轮验收对应加入只读探测、最后页桌面选项及精确应用进程分类前的安装器；随后依用户要求仅进行最终双架构编译、13 组定向普通／裁剪测试、x64 只读探测与偏好摘要核对、最后页默认勾选及清理逻辑静态审计，没有重跑所有选项或加载完整 UI 截图。此记录不代表当前单入口菜单或桌面选项持久化的运行验收。对应日志、收据和中间文件保留在本机 `artifacts/nsis-validation/`。

未在旧 GPU 或 ARM64 硬件运行本轮安装器，也未进行蓝牙设备操作；不能将注入硬件 ID 测试或交叉构建写成真实设备效果验证。

## NSIS 菜单与界面试验的历史边界

此前 NSIS 试验采用平铺的单一开始菜单入口、三页原生安装界面，以及独立保存的桌面快捷方式选择。安全 UI 预览使用当时的 `InstallerUi.nsh` 和桌面偏好读写源码，但只操作本地 fixture，不安装应用或访问真实用户偏好；该预览已编译，未启动。Computer Use 运行时的 native pipe 缺失，重置后仍无法连接，因此停止 UI 操作，未完成这些页面的视觉布局、缩放或复选框交互验收。测试脚本的语法与五组隔离桌面偏好读取输入检查已通过。该 NSIS 界面方案现已由 Inno Setup 替换。

## Inno Setup 当前验证边界

客户端载荷取自 `52c9c82bed94e32bd24eaa415a6c29327e5d6cfe` 的成功 [Windows packages CI（run 39986991886）](https://github.com/swift-lee/heymelody-windows/actions/runs/39986991886)，应用版本仍为 `0.9.2`。最终 x64、ARM64 安装器均使用 Inno Setup 6.7.3 和 `WizardStyle=modern` 编译，schema-2 收据各列出 1,288 个载荷文件，独立辅助工具的实际 SHA256 与各自收据匹配。最终安装器 SHA256：

- x64：`b94e98a80e8fe3434e2919f3cc1a8b7b06943da434aa058f98ceaac162fe9481`。
- ARM64：`6ef7b02cb1552f8c93772e06c044b77859b3cddc024f9d524c010fb74b542b81`。

辅助工具 28 组无 UI 验收通过。x64 公共静默验收已覆盖自定义目录、安装、同目录升级、文件锁预检阻止、受控运行时缺失后的成功卸载及用户文件保留。该轮使用最终命令行引用修复之前的 Inno 构建；随后最终 x64 产物通过旧 NSIS 迁移、Inno 换目录升级、保存的 `false` 选择及最终卸载，三处用户自留文件全部保留，普通偏好与测试用 `false` 文件的摘要不变；成功后仅删除本次新建的测试偏好文件，恢复其原先缺失的状态。

验收脚本增加有界等待以覆盖 Inno 卸载器的异步自清理，并规范化注册目录及快捷方式路径比较；产品修正了带尾斜杠的旧安装目录传入辅助工具时的命令行引用。最终迁移验收已覆盖该修复，其余已通过选项未重复执行。回执、完整日志与失败现场保留在本机 `artifacts/inno-validation/`。

Computer Use 的 native pipe 仍不可用，未完成新 Inno 向导的视觉、缩放或复选框交互验收；当前验证限于编译、静默执行和静态检查。未在 ARM64 或旧 GPU 硬件执行安装器，未进行蓝牙操作，也未重跑全套无关 UI 测试。
