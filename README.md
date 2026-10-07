# HeyMelody for Windows

独立源码仓库、克隆后的构建步骤和本地研究资料依赖见 [REPOSITORY.md](REPOSITORY.md)。

当前版本为 **0.7.1**，使用 **Avalonia + FluentAvalonia**。独立工作区验证的 Enco Free4 三维模型与交互已迁入 `arch/modular-devices`，主界面左侧支持拖动、松手惯性和 20°/秒水平自转。设备控制仍沿用 0.7.0 的 X4 架构，Free4 此次只用于外观展示。独立后台负责蓝牙通信和任务栏托盘；主界面及设备管理器通过当前用户、当前登录会话的双向命名管道订阅设备状态。后台自动监听白名单中已由 Windows 连接的受支持耳机，以 RFCOMM 通知和 OPPO BLE 广播更新电量、设置及开盖观测。

OPPO Enco X4 支持电量、降噪、五种声音预设、十段 EQ、双连、游戏和空间模式、Hi-Res 互斥控制。状态通知与用户设置共用每台设备的持续接收通道；手动刷新和设置验证仍执行读取。更新期间可以继续选择设置和编辑 EQ，只限制正在提交的功能组再次提交。展示型号的名称提示不替代蓝牙控制所需的 Product ID 验证。

执行 `Build-Client.ps1 -Version 0.7.1 -Architectures x64` 后，完整解压 `release/HeyMelody_Windows_0.7.1_x64.zip`，双击 `Start-3D-Demo.cmd` 体验 Free4 模型；正常客户端入口为 `Start-HeyMelody.cmd` 或架构目录中的 `HeyMelody.exe`。本次 x64 包自带 .NET 运行时。正常使用时先在 Windows 中配对并连接耳机，再通过设备管理器保存白名单。关闭主界面后后台继续运行；右键托盘选择“打开主界面”或“退出”。直接运行 `backend/HeyMelody.Host.exe` 只启动后台。本版本不添加登录自启，尚未实现独立开盖弹窗。

在仓库根目录运行 `Run-3D-Demo.ps1` 或双击 `Run-3D-Demo.cmd` 可构建并打开开发版模型演示，无需连接耳机；构建需要 .NET 10 SDK。模型来源、适配方法和迁移前体验包验证见 [Free4 记录](docs/research/enco-free4-0.7.1.md)。

界面接收后台推送，保留手动刷新及设置前后读取确认。界面中的连接/断开用于恢复或暂停所选耳机的控制监听；暂停持续到手动恢复或后台重启，BLE 观测继续。设备设置必须经过型号、能力、后台实例和连接代次校验；回执丢失不会自动重发。详细规则见 [界面说明](docs/gui-notes.md) 和 [后台架构说明](docs/backend-architecture.md)。

当前范围为 **Windows 上已有 Enco X4 功能、托盘后台及事件同步**。其他型号、macOS/Linux 后台和 2.3 Mbps 无损链路的推进已暂缓；已完成的资料调研保留在 [docs/research](docs/research)，不构成本版本支持承诺。

源码入口为 `src/HeyMelody.App`，独立管理器为 `src/HeyMelody.DeviceManager`，共享界面契约为 `src/HeyMelody.Contracts`，界面状态和进程通信为 `src/HeyMelody.Client`，蓝牙后台为 `src/HeyMelody.Host`，日常功能为 `src/HeyMelody.Daily`。使用 .NET SDK 10 构建；官方 NuGet 源已写入 `NuGet.Config`。常规离线测试不需要本地 APK 或旧证据 ZIP，完整步骤见 [REPOSITORY.md](REPOSITORY.md)。OTA、录音、翻译和健康功能留待后续阶段。

验证入口与执行范围见 [REPOSITORY.md](REPOSITORY.md)。模拟协议、IPC、界面测试及三架构静态包检查不证明真实耳机、手机并发修改或 ARM64 实机行为。生成的发布包与本地验证报告不随 Git 提交。

历史 0.5.0 的 x64/x86 蓝牙 GET 与真实界面预览证据保留在 [阶段五历史记录](docs/history/phase5-notes.md) 和本地 `artifacts/phase5-release-validation.json`，仅证明该旧版本。本分支不会自动沿用旧报告作为新发布包的硬件验证；交叉构建也不代表已在 ARM64 电脑执行。

文档入口见 [docs/README.md](docs/README.md)。保留的只读 CLI 与 Hi-Res 受控实验工具见 [CLI 工具说明](docs/cli-tools.md)，其发送范围和历史硬件证据独立于当前 GUI/后台。
