# HeyMelody Avalonia 界面

0.6.0 将 Windows 上现有 Enco X4 功能从 WPF 迁移到 Avalonia 12.1.3、FluentAvaloniaUI 3.1.0 和 CommunityToolkit.Mvvm 8.4.2。其他耳机型号与 macOS/Linux 的实现暂缓。界面和客户端状态模型分别位于 `HeyMelody.App` 与 `HeyMelody.Client`；DTO 和现有 schema-1 stdio 协议位于 `HeyMelody.Contracts`。Windows 蓝牙调用仍由 `HeyMelody.Host` 子进程处理，不进入界面线程。

## 界面与现有功能

参考 `research/main-page.png` 和 `research/headphone-configs.png`，默认窗口 1280×900，最小 900×640：左侧展示产品，右侧显示身份、电量和设置，底部固定设备选择区。宽度小于 1080 时改为上下布局，设置区域单独滚动。产品图为本项目原创矢量示意，仅在已确认的 `06EC10` 身份上展示；未知或断开的设备使用通用图标。

使用 FluentAvalonia 的 `FAAppWindow`、`FASettingsExpander`、`FASettingsExpanderItem`、`FAInfoBar` 和 `FAContentDialog`，以及主题提供的按钮、下拉框、开关、文本框和滑块。系统、浅色、深色主题可在应用设置中切换，偏好保存到 `%LOCALAPPDATA%/HeyMelody/preferences.json`。无效偏好会回退到系统主题。项目图标沿用原创的 `Assets/HeyMelody.ico`。

保留配对设备枚举、手动连接和断开、左右耳及充电盒电量与充电状态、降噪、五种声音预设、十段自定义 EQ、双设备连接、游戏模式、空间音频及 Hi-Res。当前值与可写能力全部来自后端回读。无法读取的电量显示 `—`，未知开关显示“未确认”并禁用写入，部分读取失败有单独提示。

## 交互约束

- 选项使用原始协议编号，不以列表下标代替。降噪、声音预设与空间音频先选择再点击“应用”；开关仅由用户点击提交。
- 操作串行。写入要求快照已连接、已适配、无失败，且设备身份与当前选择及连接会话一致。刷新失败立即禁用写入。
- 8 秒轮询只读取状态，不自动发送设置或重放未知结果。正常刷新保留下拉框待选值和未保存的 EQ 名称、条目与频段。
- EQ 保存和启用分别执行。未保存的草稿不可直接启用；切换条目、切换或断开耳机、新建、刷新可能改变连接的设备列表及关闭窗口，会先确认丢弃草稿。
- 空间音频与 Hi-Res 互斥时，用户确认后才传递 `AllowMutexChange=true`，具体操作和回读由原后端负责。部分提交、ACK 未知、回读未确认、明确成功使用不同提示，不自动重发。
- 应用设置不会与正在执行的控制确认重复弹出。关闭先收起设置框和当前控制确认，再处理草稿确认，取消轮询、等待服务释放后结束窗口。
- 操作日志只在当前进程保留最近 80 项；遮蔽设备标识、MAC 与设备路径，不记录原始包或异常堆栈。

## 构建与界面验证

```powershell
dotnet build src/HeyMelody.Host -c Release
dotnet run --project src/HeyMelody.App -c Release
dotnet run --project tests/HeyMelody.Client.Tests -c Release
dotnet run --project tests/HeyMelody.App.Tests -c Release
dotnet run --project tests/HeyMelody.App.Tests -c Release -- --native-smoke
```

App.Tests 使用真实 Skia 绘制 Avalonia 控件，覆盖连接、无设备、未知型号、部分读取、断开五种状态的浅色/深色和 1280×900/900×640，共 20 张截图；另验证绑定、真实鼠标点击、协议 ID、刷新不写入和十段编辑器。输出在 `artifacts/avalonia-ui-*`，包含 PNG 哈希与 `validation.json`。测试仅使用模拟服务，不调用原生蓝牙。Headless 调度显式使用 `Dispatch<bool>`，避免无返回值 async 委托变为 `async void` 而漏报失败；在外部调用线程等待及释放会话。

`--native-smoke` 单独在 Windows 创建屏幕外真实 Win32 `FAAppWindow`，验证标题栏、图标、两个主题和宽窄布局、设置对话框关闭及服务只释放一次。该测试同样使用内存服务，不启动蓝牙后台。本轮 179 项离线测试和 34 项原生窗口检查已通过，构建无警告和错误。

离屏预览使用相同界面和状态模型，输出文件必须不存在：

```powershell
dotnet src/HeyMelody.App/bin/Release/net10.0/HeyMelody.dll --ui-preview artifacts/preview-light.png
dotnet src/HeyMelody.App/bin/Release/net10.0/HeyMelody.dll --ui-preview artifacts/preview-dark.png --theme dark --size 900x640
dotnet src/HeyMelody.App/bin/Release/net10.0/HeyMelody.dll --ui-preview artifacts/preview-eq.png --size 1280x1800 --expand-settings
```

`--scenario` 可选 `connected`、`empty`、`unsupported`、`partial`、`disconnected`。模拟服务与预览状态模型分别拒绝设置写入。预览不包含系统原生标题栏。

`--ui-live-preview <新PNG路径>` 通过只读包装访问原 Windows 后台，仅允许唯一已连接且名称精确为 OPPO Enco X4 的设备。没有可信快照即退出 1，不生成连接截图；结束时等待 Host 释放。该入口允许枚举、连接和读取，不允许设置写入，也不启动轮询。

三 RID 的自包含发布使用 `Build-Client.ps1`。历史 0.5.0 硬件证据不能证明本次迁移；模拟截图、构建和静态检查也不代表真实耳机写入或 ARM64 实机验证。
