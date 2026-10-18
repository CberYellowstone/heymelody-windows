# HeyMelody for Windows

在 Windows 上查看和调整 OPPO 耳机设置，让欢律里的常用功能也能在电脑上使用。

这是一个社区维护的 Windows 客户端，使用 Avalonia 和 FluentAvalonia 构建。项目提供耳机状态、降噪、均衡器和校准界面，支持 OPPO、OnePlus、realme 的部分耳机型号；与 OPPO 官方没有隶属关系。

[下载与发布](https://github.com/CberYellowstone/heymelody-windows/releases) · [使用说明](docs/usage.md) · [版本记录](docs/history/release-notes.md) · [反馈问题](https://github.com/CberYellowstone/heymelody-windows/issues)

## 可以做什么

- 查看耳机电量、连接状态、设备信息和固件版本。
- 切换降噪、通透等模式，调整型号提供的细分选项。
- 使用内置均衡器预设，编辑、保存自定义 EQ；支持相应型号的 BassWave™ 动态低音。
- 运行耳塞贴合度测试和黄金听感向导，查看左右耳结果，并比较增强前后的试听。
- 调整耳机支持的游戏模式、Hi-Res、空间音效及其他设置。
- 展示耳机图片、动画或三维模型；关闭主窗口后，托盘后台继续管理耳机。

项目仅使用欢律 App 的配置与资源，预装目录包含 85 个耳机产品 ID；这是目录覆盖，不代表所有型号和功能都已通过实机验证。实际可用设置还取决于欢律配置、设备能力和项目已实现的协议，固件条件仅产生提醒。当前来源与升级说明见[欢律单源说明](docs/research/heymelody-only.md)。

## 安装与开始使用

平台基线为 **Windows 10 2004（Build 19041）及以上**。发布包自带 .NET 运行时，日常使用无需安装 SDK；下载时以发布页实际提供的架构为准。

1. 在[发布页](https://github.com/CberYellowstone/heymelody-windows/releases)选择可用的预编译包，完整解压到一个目录。单架构包应与 Windows 架构匹配。
2. 先在 Windows 蓝牙设置中配对并连接耳机。
3. 双击 `Start-HeyMelody.cmd`，通过设备管理器选中要管理的耳机并保存名单。
4. 在主界面选择耳机，等待连接和状态读取完成，再调整设置。

请保留包内的 `backend`、`ResourceBundles`、DLL 和许可证文件。关闭主窗口后后台仍在托盘运行；完全退出时，右键托盘图标选择“退出”。更新前先退出旧版后台，再使用完整的新包，避免新界面连接到旧后台。

没有耳机也可以体验界面：运行包内的 `Start-3D-Demo.cmd`，进入离线演示。Demo 的控件操作只修改模拟数据，不代表演示型号的实际功能。

详细的连接、EQ、校准、资源设置和常见问题见[使用说明](docs/usage.md)。

## 当前进展

当前源码版本为 **0.9.2**，用于本轮 Windows 本地构建，尚未发布到 GitHub。修复控制服务缓存缺失或过期可能阻止连接的问题，并在连接失败时显示具体步骤和原因。双击底栏中的当前耳机刷新状态，三击重新连接（也可按 F5／Shift+F5）。Air4 Pro 真机恢复与 ARM64 硬件尚未验证；改动与验证范围见[版本记录](docs/history/release-notes.md)。

项目仍在完善设备适配。不同型号、蓝牙适配器、手机同时控制及 ARM64 硬件行为需要分别验证；OTA 固件更新、录音、翻译和健康功能尚未提供。默认开启在线资源，可在设置中关闭；缓存和本地设置的说明见[使用说明](docs/usage.md#资源与本地数据)。

## 构建与贡献

源码构建需要 **.NET 10 SDK**。从仓库根目录运行：

```powershell
dotnet build src/HeyMelody.App/HeyMelody.App.csproj -c Release
```

普通构建使用仓库自带的资源包，不需要原始 APK 或本地研究档案。模块、运行、测试和打包步骤见[开发指南](docs/development.md)。

欢迎通过 [Issues](https://github.com/CberYellowstone/heymelody-windows/issues) 提交问题、设备兼容性反馈和功能建议，也欢迎提交 Pull Request。反馈时请注明应用版本、Windows 版本、耳机型号与固件、配置内容版本和复现步骤；分享日志前移除蓝牙地址、密钥等个人数据。开始修改项目时，请阅读[协作规则与文档导航](AGENTS.md)。

## 许可与来源

当前仓库尚未提供顶层源码许可证。第三方库和原厂耳机资源保留各自的版权、许可及来源声明；具体信息见 [SOURCE-NOTES.md](packaging/SOURCE-NOTES.md)、[第三方许可](docs/licenses/)和发布包中的 notices。项目的研究依据与公开结论见[研究索引](docs/research/README.md)。
