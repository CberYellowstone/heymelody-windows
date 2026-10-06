# HeyMelody Windows 客户端界面

本轮实现 Windows WPF 中文客户端，目标框架 `net10.0-windows`，平台 `AnyCPU`，发布 RID 为 `win-x86`、`win-x64` 和 `win-arm64`。版本为 0.5.0。蓝牙原生调用由主线负责的 `HeyMelody.Host` 子进程处理，GUI 通过 `ClientBridge` 调用 BCL-only `UiContracts.cs`，不会把 SDP 调用放到界面线程上。

界面包含配对耳机列表、手动连接与断开客户端、左右耳和充电盒电量、降噪档位、五种官方声音预设、十段自定义 EQ、双设备连接、游戏模式、空间音频和 Hi-Res。OTA、录音、翻译和健康功能没有加入本轮界面。当前配置与可写能力来自设备回读，不从视觉示例推断设备支持。

视觉使用雾蓝 `#F2F7F9`、深海青 `#126C75`、墨青 `#163C46` 和细边线 `#D8E5E9`；字体使用系统 Segoe UI 和系统中文回退。三组电量与自行绘制的 TWS 耳机轮廓构成主识别区。下拉框、按钮、开关、文本框、垂直 EQ 滑块和滚动条均采用一致的圆角样式，并保留键盘焦点提示。应用资源接入主线绘制的 `Assets/HeyMelody.ico` 和 `Assets/HeyMelody.png`，未使用旧 LHDC Manager 图标或参考项目的 UI/字体素材。

## 交互约束

- 所有选项使用显式原始编号。ANC `UiOption.Key` 是主模式与子档位的组合值；EQ 预设顺序为协议 ID `0/7/2/1/3`，对应丹拿原声、丹拿高解析、纯享人声、清亮高音、澎湃低音；没有用列表下标代替协议 ID。
- 界面操作串行，操作期间不能切换设备。切换设备立即清除旧 snapshot。只有已连接、已适配、无失败、snapshot 身份与本次连接和当前选择全部一致时，才允许写入。
- 单独字段的未知状态显示“未确认”；缺少充电盒回执显示 `—`，不会补造电量。读取失败会清除 snapshot 与写能力，保留尚未提交的 EQ 草稿供用户核对。
- 8 秒轮询只调用 `RefreshAsync`，不会自动调用 SET，也不会自动重放未知结果。降噪、预设和空间下拉框的待选值不会被正常轮询覆盖；脏 EQ 的名称、频段和条目选择同样不会被覆盖。
- 自定义 EQ 编辑、保存和启用是独立操作。尚有未保存改动时不能直接“启用此 EQ”。切换 EQ、切换耳机、新建 EQ、关闭窗口会先确认丢弃；刷新设备可能重建连接标识时也会先确认。新建默认十频段为 `31/62/125/250/500/1000/2000/4000/8000/16000 Hz`，默认范围 `±6 dB`，现有条目的范围采用设备数据。
- 空间与 Hi-Res 互斥时，界面先确认，再给后端 `AllowMutexChange=true`。实际互斥操作及响应、回读由后端执行。部分变更、ACK 未知、回读未确认与明确成功使用不同的中文提示。没有提交而已处于目标状态，视为正常结果。
- 日志仅保留当前进程内最近 80 项，不导出身份；不会记录异常堆栈、地址或 raw payload，并对常见 MAC/设备路径作额外遮蔽。
- 关闭时取消轮询和正在进行的请求，等待服务释放，再经 Dispatcher 结束窗口；没有同步重复进入 Closing 的行为。

## 离屏验证入口

`HeyMelody.exe --ui-preview <新PNG绝对路径>` 使用 `PreviewService` 的虚拟 DTO，无 native/backend 引用，不读取蓝牙，不允许任何 SET。将完整 content 脱离窗口后离屏绘制为 1280×1440 PNG，避免桌面工作区高度截断；文件以 `CreateNew` 写入。

`HeyMelody.exe --ui-live-preview <新PNG绝对路径>` 使用 `ReadOnlyPreviewService` 包装 `ClientBridge`。它仅允许唯一已连接、名称精确为 OPPO Enco X4 的设备枚举与连接，拒绝 `ApplyAsync`；没有可信已适配 snapshot 时退出 1，不产出截图。结束时始终 await `DisposeAsync` 清理 Host。此入口不显示窗口、不点击按钮、不触发轮询和设置。

截至本 GUI owner 的 2026-10-06 02:40 UTC 验证，Debug build 0 warnings / 0 errors，虚拟离屏预览退出 0，完整图为 `src/HeyMelody.App/bin/ui-preview-v5.png`；本 owner 已检查图中的所有卡片、真实名称绑定、十段滑块、开关和底栏。真实只读 PNG、三 RID 发布和实际 ARM64 执行状态由主线报告，不能从 AnyCPU 编译或虚拟图推断。早期 v1–v4 是已修正的开发预览，不作为最终截图证据。
