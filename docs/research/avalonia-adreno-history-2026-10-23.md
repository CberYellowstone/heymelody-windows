# Avalonia 在 Windows ARM64 上回避 Adreno 的历史

核查日期：2026-10-23。本机默认进入 WARP 的测量见 [UI 卡顿调查](ui-lag-arm64-2026-10-23.md)。这是有历史失绘背景的兼容策略，本机成功不能证明所有 Adreno 安全。

## 按日期核对

- **2023-02-20**：[问题 #10405](https://github.com/AvaloniaUI/Avalonia/issues/10405) 报告 Windows 11 ARM64、Avalonia 11.0.0-preview4/5，在 Surface Pro X／Windows Dev Kit 2023 上运行新建模板时只有窗口外框、内容透明；删除 ANGLE DLL 后可显示。这是失绘，不是单纯帧率低。
- **2023-12-01**：[PR #13538](https://github.com/AvaloniaUI/Avalonia/pull/13538) 合并（`ba5dd3ba`，04:58:43 UTC）。作者、维护者 maxkatz6 解释，忽略 Adreno 后使用 Microsoft Basic Renderer 可避开问题，并明确说 **“I would expect it to be slower”**。初始实现仅在 ARM64 上把名称含 `adreno` 的适配器降低排序，没有驱动版本条件；目的是恢复显示并保留 WinUI composition。#10405 随后关闭又重开。
- **2024-07-24**：[driver1998](https://github.com/AvaloniaUI/Avalonia/issues/10405#issuecomment-2246709353) 指出 Qualcomm 下载中的 8cx Gen3 驱动为 **30.0.4035.6900**；[snickler](https://github.com/AvaloniaUI/Avalonia/issues/10405#issuecomment-2246739914) 称修复覆盖 8cx Gen3 和 X 系列，Gen1/2 没有获得。这是社区报告，不是全设备兼容性证明；版本数字不能替代真机验证。
- **2024-09-23**：[PR #17079](https://github.com/AvaloniaUI/Avalonia/pull/17079) 合并（`49ab0b36`），新增 `GraphicsAdapterSelectionCallback`。但当时 ARM64 多适配器先移除 Adreno，再调用回调，因此应用仍无法通过该接口选回已过滤的硬件。
- **2026-07-02**：[问题 #21689](https://github.com/AvaloniaUI/Avalonia/issues/21689) 报告 Snapdragon X Elite 上仍默认进入 WARP。[维护者回复](https://github.com/AvaloniaUI/Avalonia/issues/21689#issuecomment-4862469694)：团队缺少经确认和测试的 GPU／驱动列表，也无法覆盖广泛设备，保守默认可避免部分设备完全失绘；不同意默认改 Vulkan，因为 redirection bitmap 会关闭部分其它功能；赞同开放回调及增加警告。
- **2026-07-03**：[PR #21695](https://github.com/AvaloniaUI/Avalonia/pull/21695) 合并（`bfa2319e`，07:36:21 UTC）。回调现在先收到完整列表，应用可以显式选 Adreno；默认回避导致改选后续适配器时会记录 warning。PR 明确保留保守默认，未纳入按驱动收窄或默认 Vulkan 的提议。

## 目前保留什么

最新发布 **12.1.4**（2026-10-09）与 main（`bd835340`，2026-10-22）的 `AngleWin32EglDisplay.cs` 相同。条件是 Win32 ANGLE／D3D11、ARM64、无选卡回调且适配器多于一个，选择首个名称不含 `adreno` 的适配器；本机由此进入 WARP。没有驱动版本或 Gen3／X 白名单。指定回调则使用完整列表及其返回索引，绕过默认规则。

本次检索 Avalonia 主仓库当前源码、提交消息和相关 issue／PR，只确认这一条显式 Adreno 规则。[#16035](https://github.com/AvaloniaUI/Avalonia/pull/16035) 是通用 D3D11 driver type 优化，被 [#16063](https://github.com/AvaloniaUI/Avalonia/pull/16063) 撤销；后继 #16069 未合并。没有穷举历史分支或 ANGLE／Skia 内部的驱动 workaround。

## 如何向上游贡献

优先补充 #21689，提交可复现的版本、硬件／驱动、实际 renderer、相同场景 A/B 和画面正确性证据，区分回调节拍与上屏 FPS，避免重复开题。本文未提交外部 issue 或评论。

收窄规则前，需要 GPU／驱动矩阵及失绘、透明／合成、缩放、休眠恢复等兼容性验证，覆盖旧设备与新设备；不能凭本机提速直接删除整条规则。

原始响应、diff、来源与 SHA256 见本机 [研究档案](../../research/ui-lag-arm64-20261023/upstream-adreno-history/)，通常不随 clone 提供；其中 `issue-10405-history.md`、`later-findings.md` 保存时间线，`qualcomm-scope-findings.md` 记录检索范围。
