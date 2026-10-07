# HeyMelody for Windows

**0.8.1** 使用 Avalonia、FluentAvalonia 和共享托盘后台，在 Windows 上提供资源驱动的耳机控制、状态监听及图片、三维模型、Lottie 展示。

0.8.1 修复了资源目录包含两种 OPPO 控制服务、底层却只允许其中一种而造成的连接失败，并修正连接尚未建立时误报“型号未适配”的提示。本机 Enco X4 已通过两套预装及缓存资源的 x64 后台初始化、状态读取和通知订阅验证，全程未发送设置命令；详见 [实机验证记录](docs/research/enco-x4-0.8.1.md)。

预装欢律与 ColorOS“无线耳机”两套独立资源，目录分别包含 **85、141 个耳机产品 ID**，覆盖 OPPO、OnePlus、realme，排除音箱。这是资源目录覆盖；控制仍须同时满足所选配置、已实现的协议 codec、固件条件和设备实际能力。未知协议、无 SPP 及需要外部 App 的型号不会开放设置，图片或名称识别也不授予控制权限。

设置默认选择欢律、开启在线资源，可切换资源来源并填入自定义请求密钥。本版保留各来源的预置密钥。两套来源各自缓存，不合并参数或外观；完整控制配置采用内容版本较高的一版，同版本采用预装版。美术更新按厂商摘要比较，新包校验并发布后清理旧在线版本，正在显示的旧包等待释放；预装离线资源保持不动。关闭在线资源停止网络请求，已下载的有效新版仍可使用。用户密钥和缓存加密密钥以本地 JSON 明文保存。

降噪使用圆形模式按钮和细分选项，EQ 按型号提供频段和容量，内置与自定义方案分组显示。普通控制采用开关、Slider、RadioButton、文本框和 ComboBox。耳塞贴合度测试由独立 `HeyMelody.FitTest.exe` 处理；黄金听感由独立 `HeyMelody.HearingSetup.exe` 向导创建，主界面的 ComboBox 选择本机保存及耳机当前读回的完整方案。校准组件共享 Host 的蓝牙连接，不另行建立控制连接。

先在 Windows 中配对、连接耳机，再通过设备管理器保存白名单。完整解压发布包后运行 `Start-HeyMelody.cmd` 或所选架构目录中的 `HeyMelody.exe`；发布包自带 .NET 运行时。主界面关闭后后台继续运行，右键托盘可以重新打开界面或退出。设备管理器、贴合度测试和黄金听感向导均随包提供。本版本不添加登录自启或独立开盖弹窗。

耳机设置和手机推送共用每台设备的持续接收通道。回执、设备确认和观测状态分别处理，丢失回执不自动重放操作；未提交的 EQ 草稿保留。校准期间限制同一设备的冲突写入，其他设备继续工作；关闭、断连、休眠和切换资源来源会终止当前校准并回读状态。

源码构建需要 .NET 10 SDK，官方 NuGet 源见 `NuGet.Config`：

```powershell
dotnet build src/HeyMelody.App/HeyMelody.App.csproj -c Release
dotnet run --project tests/HeyMelody.Resources.Tests
dotnet run --project tests/HeyMelody.Calibration.Tests
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Build-Client.ps1 -Version 0.8.1
```

常规构建和离线测试使用仓库中的资源包，不需要本地 APK。重新提取资源需要固定的 `research` 原件，入口是 `tools/bundle_resources.py`；运行中的软件不读取研究目录。原始资源版权与溯源保留在发布包的 notices 和资源清单中。

本轮没有把静态解析、模拟协议或 UI 测试当作所有型号的实机验证。新增型号、校准流程、手机并发操作、休眠恢复和 ARM64 行为仍需对应硬件验证；历史 X4 证据只证明记录中的版本与设备。OTA、录音、翻译、健康功能、macOS/Linux 后台及 2.3 Mbps 无损链路保持后续范围。

完整模块、构建和验证说明见 [REPOSITORY.md](REPOSITORY.md)，协议与资源依据见 [研究交接](docs/research/device-adaptation-handoff.md)，历史记录见 [文档入口](docs/README.md)。发布包、研究原件和生成报告不随 Git 提交。
