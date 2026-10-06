# HeyMelody Avalonia 界面

0.6.0 完成 Windows Enco X4 功能的 Avalonia 初版迁移，0.6.1 改进后台刷新、交互及失败处理。当前界面使用 Avalonia 12.1.3、FluentAvaloniaUI 3.1.0 和 CommunityToolkit.Mvvm 8.4.2。其他耳机型号与 macOS/Linux 的实现暂缓。界面和客户端状态模型分别位于 `HeyMelody.App` 与 `HeyMelody.Client`；DTO 和现有 schema-1 stdio 协议位于 `HeyMelody.Contracts`。Windows 蓝牙调用仍由 `HeyMelody.Host` 子进程处理，不进入界面线程。

本轮发布包为 0.6.1；源码项目与 `Build-Client.ps1` 的默认版本仍为 0.6.0，发布时通过 `-Version 0.6.1` 覆盖。界面页脚和关于说明读取 App 程序集的三段版本，因此普通源码构建与本轮发布包可显示不同版本。旧 0.6.0 发布包不包含此次改进。

## 界面与现有功能

参考 `research/main-page.png` 和 `research/headphone-configs.png`，默认窗口 1280×900，最小 900×640：左侧展示产品，右侧显示身份、电量和设置，底部固定设备选择区。宽度小于 1080 时改为上下布局，设置区域单独滚动。产品图为本项目原创矢量示意，仅在已确认的 `06EC10` 身份上展示；未知或断开的设备使用通用图标。

使用 FluentAvalonia 的 `FAAppWindow`、`FASettingsExpander`、`FASettingsExpanderItem`、`FAInfoBar` 和 `FAContentDialog`，以及主题提供的按钮、下拉框、开关、文本框和滑块。系统、浅色、深色主题可在应用设置中切换，偏好保存到 `%LOCALAPPDATA%/HeyMelody/preferences.json`。无效偏好会回退到系统主题。项目图标沿用原创的 `Assets/HeyMelody.ico`。

保留配对设备枚举、手动连接和断开、左右耳及充电盒电量与充电状态、降噪、五种声音预设、十段自定义 EQ、双设备连接、游戏模式、空间音频及 Hi-Res。当前值与可写能力全部来自后端回读。无法读取的电量显示 `—`，未知开关显示“未确认”并禁用写入，部分读取失败有单独提示。

## 交互约束

- 选项使用原始协议编号，不以列表下标代替。降噪、声音预设与空间音频先选择再点击“应用”；开关仅由用户点击提交。
- 蓝牙/RPC 操作串行，界面编辑独立进行。8 秒轮询合并重复请求，按身份、功能开关、降噪、EQ、电量和设备信息分段读取；用户提交优先于下一段读取，不抢占已经开始的 RPC。`IsRefreshing` 只表示读取活动；`Busy` 表示连接等生命周期操作或待执行的用户提交，不作为统一禁用条件。提交期间禁用同组的提交按钮或开关，下拉选项、EQ 名称和滑块仍可编辑，其他组仍可提交。空间音频与 Hi-Res 共用一组，预设和自定义 EQ 共用一组；每组最多保留一个提交。应用设置在读取和写入期间仍可打开。
- 写入要求快照已连接、已适配、无失败，且设备身份与当前选择及连接会话一致。读取失败的状态保持未知，相关写入禁用。轮询始终只读；不持续强制执行已结束的用户目标。下拉框按协议编号保留待选值，EQ 按条目与编辑版本保留本地调整；相同内容的列表不会反复替换。提交使用冻结的设置，回读不会覆盖提交后继续编辑的内容。
- “保存均衡器”执行保存并选用；“启用此 EQ”只选用已有且无未保存调整的条目。切换条目、切换或断开耳机、新建、刷新可能改变连接的设备列表及关闭窗口，会先确认丢弃草稿。取消这些草稿丢弃或关闭确认会保留草稿；普通状态刷新不会询问丢弃草稿。
- 每个用户目标最多执行两次逻辑尝试。收到完整后台回执、连接及同一设备身份可核实的绝对值设置失败时，等待 500 ms 并重新读取：目标已达到则结束，仍未达到且安全才自动重试一次。需要再次创建 EQ、联动已部分提交、状态或能力不足以安全自动继续时，使用 `FAContentDialog` 询问“确认 / 取消”，默认取消。确认后再次读取和校验；确认本身不能绕过身份、连接或能力检查。后端每次尝试均重新核对能力和状态，不在传输层重放 SET。手机改变设置不会单独触发冲突确认；本次 Windows 提交仍使用冻结的明确目标。
- 仅取消再次写入确认或首次空间音频/Hi-Res 联动确认时，会清空全部待选设置、EQ 草稿与尚未开始的写入，然后立即完整刷新。该行为不适用于普通草稿丢弃或关闭确认。第二次失败只弹出“确定”提醒；关闭提醒后立即完整刷新，不再重试。明确断连、控制连接状态未知或未取得完整后台结果时，第一次就提醒，关闭提醒后只读刷新，不自动重发。最终刷新失败只更新状态提示，不再次打开同一操作的对话框。设置、重试、失败与应用设置对话框共用协调器。
- 空间音频与 Hi-Res 互斥时，用户确认后才传递 `AllowMutexChange=true`。再次尝试联动必须取得再次确认，并跳过已完成的前置切换。新建 EQ 失败后，先核对不在原列表中的唯一同内容条目：已选中则结束，未选中则复用该 ID；有歧义或重名时不再次创建。后续保存使用已核实的 ID，避免创建重复条目。
- ACK 接受与当前目标已回读到是两项独立事实。未知或拒绝的响应不会因匹配回读而改成成功响应；失败及未知响应提示不会被普通成功轮询清除。同版本的待选值在目标已观察到后解除保护，后续手机端修改可正常显示；提交期间的新编辑仍保留。
- 应用设置不会与正在执行的控制确认重复弹出。关闭先停止接收新操作并取消尚未开始的写入，收起设置框和当前控制确认，再处理草稿确认；取消关闭会保留草稿并恢复窗口操作。确认关闭后取消轮询、等待服务释放再结束窗口。
- 操作日志只在当前进程保留最近 80 项；遮蔽设备标识、MAC 与设备路径，不记录原始包或异常堆栈。

“连接或同一设备身份无法核实”包括：后台进程崩溃、超时或返回损坏/不匹配的 RPC 记录，导致客户端没有完整回执；重新读取时无法取得有效型号、能力或连接状态；返回的设备身份与当前选择/连接会话不一致；后台重启后丢失原地址到随机身份的映射。此时设置可能已经到达耳机，不能把旧请求转移到另一副同名耳机。程序提示并只读刷新，身份映射丢失时要求重新选择并连接。手机修改某个设置本身不属于身份无法核实，也不能仅凭一次 GET 超时断言蓝牙已断开。

## 构建与界面验证

```powershell
dotnet build src/HeyMelody.Host -c Release
dotnet run --project src/HeyMelody.App -c Release
dotnet run --project tests/HeyMelody.Client.Tests -c Release
dotnet run --project tests/HeyMelody.App.Tests -c Release
dotnet run --project tests/HeyMelody.App.Tests -c Release -- --native-smoke
```

App.Tests 使用真实 Skia 绘制 Avalonia 控件，覆盖连接、无设备、未知型号、部分读取、断开五种状态的浅色/深色和 1280×900/900×640，共 20 张截图及 4 项交互检查；交互检查覆盖绑定、真实鼠标点击、协议 ID、刷新不写入、读取阻塞时继续编辑与提交、对话框协调及关闭取消。输出在 `artifacts/avalonia-ui-*`，包含 PNG 哈希与 `validation.json`。测试仅使用模拟服务，不调用原生蓝牙。Headless 调度显式使用 `Dispatch<bool>`，避免无返回值 async 委托变为 `async void` 而漏报失败；在外部调用线程等待及释放会话。

`--native-smoke` 单独在 Windows 创建屏幕外真实 Win32 `FAAppWindow`，验证标题栏、图标、两个主题和宽窄布局、设置及重试/失败对话框和服务只释放一次。该测试同样使用内存服务，不启动蓝牙后台。交互测试另外覆盖读取阻塞期间的真实鼠标提交、继续编辑、对话框串行和关闭取消。

`Build-Client.ps1` 默认执行 Protocol 37 项、Devices 13 项、Control 19 项、Daily 52 项、Probe 23 项、Client 60 项、App 24 项，共 228 项检查；`--native-smoke` 另外通过 38 项检查，不由发布脚本默认执行。本轮完整发布前的界面报告位于 `artifacts/avalonia-ui-20261006-143139-7a87a6af/validation.json`，早先的报告仍保留在 `artifacts/refresh-ui-final/validation.json`。文档核对时另补齐旧绑定输入替身的成功回执字段，并断言鼠标提交确实进入已确认成功分支；修正后重新运行 App 的 24 项检查通过，报告为 `artifacts/avalonia-ui-20261006-145245-daeb44ae/validation.json`。当前 App.Tests 报告的 `version` 字段硬编码为 `0.6.0`，不能作为新发布包的专属执行回执。这些源码测试没有真实蓝牙调用，不是已发布 0.6.1 二进制的逐架构硬件执行回执，不能替代耳机和手机同时操作的实机验证。

离屏预览使用相同界面和状态模型，输出文件必须不存在：

```powershell
dotnet src/HeyMelody.App/bin/Release/net10.0/HeyMelody.dll --ui-preview artifacts/preview-light.png
dotnet src/HeyMelody.App/bin/Release/net10.0/HeyMelody.dll --ui-preview artifacts/preview-dark.png --theme dark --size 900x640
dotnet src/HeyMelody.App/bin/Release/net10.0/HeyMelody.dll --ui-preview artifacts/preview-eq.png --size 1280x1800 --expand-settings
```

`--scenario` 可选 `connected`、`empty`、`unsupported`、`partial`、`disconnected`。模拟服务与预览状态模型分别拒绝设置写入。预览不包含系统原生标题栏。

`--ui-live-preview <新PNG路径>` 通过只读包装访问原 Windows 后台，仅允许唯一已连接且名称精确为 OPPO Enco X4 的设备。没有可信快照即退出 1，不生成连接截图；结束时等待 Host 释放。该入口允许枚举、连接和读取，不允许设置写入，也不启动轮询。

三 RID 的自包含发布使用 `Build-Client.ps1`。0.6.1 的静态包检查位于 `release/HeyMelody_Windows_0.6.1_STATIC-VALIDATION.json`，验证可执行文件架构、哈希、图标、原生绘图库和许可；该报告明确未验证真实蓝牙、真实只读 GUI 预览或 ARM64 硬件执行。`manifest.json` 和可选执行回执只绑定 GUI/Host 的 EXE 哈希，不涵盖应用托管 DLL，不能单凭这些哈希证明包与当前源码完全对应。历史 0.5.0 硬件证据不能证明本次迁移。模拟截图、原生窗口测试、构建和静态检查不代表真实耳机写入；只读 live preview 也不能证明本次提交、重试及手机并发行为。
