# 0.8.8–0.8.10 版本记录

以下原段整理自 2026-10-11 文档重整前的根目录 `README.md` 与 `REPOSITORY.md`，保留原版本、日期、设备、平台及验证范围，仅调整链接位置。原段中的 “current”、默认行为与检查数量属于对应历史版本，不作为后续版本的保证。两个来源有重叠时分别列出；原文未给验证日期的条目不补写日期。

历史 `research/`、`artifacts/`、`release/` 链接指本地研究证据或发布产物，通常不随普通 Git clone 提供。完整原稿和摘要清单见本地[本轮整理档案](../../research/documentation-entrypoints-2026-10-11/README.md)；缺少该档案不影响阅读下列公开原段。现行构建与验证入口见[开发说明](../development.md)。

返回[版本索引](release-notes.md)。

## 0.8.10

原 `REPOSITORY.md`（行 45, 47）：

Version 0.8.10 uniformly hides automatic volume (the official Smart volume control feature), removes feature 1C from daily queries and writes, and prevents its notifications from affecting other settings. Existing cached rows and Demo omit the feature as well. User feedback reports it absent on Free4, X3 and X4; the other configured models remain unconfirmed. Status notices retain their actual notification or read failure reason instead of claiming that every warning disables settings. The [decision and prior research](../research/auto-volume.md) distinguish this policy from hardware support verification.

Version 0.8.10 implementation verification passed all thirteen default runners on Windows x64 in Release: Windows offline checks plus 625 checks across Protocol 65, Resources 25, Calibration 50, Calibration.UI 11, Devices 19, Control 19, Daily 116, Probe 23, Host 60, Client 139, App 90 and DeviceManager 8. New cases exercise retired feature isolation, cached presentation, status notices and hearing profile notification/save paths using synthetic data. No native Bluetooth, acoustic, native-window smoke or ARM64 execution was performed. Local publishing and static package verification were not run; the existing push workflow builds x64 and ARM64 packages.

原 `README.md`（行 11）：

0.8.10 统一隐藏“自动音量”（原厂“智能音量调节”），停止该项查询、写入与通知联动，旧缓存展示和 Demo 也隐藏此开关。Free4、X3、X4 的使用反馈均为没有此功能，其余机型尚未确认，暂不开放。右上提示保留具体原因：通知订阅不完整时说明仍可手动刷新，不再一律声称设置已禁用。决定、原厂用途和验证范围见 [智能音量研究与处理决定](../research/auto-volume.md)。

## 0.8.9

原 `REPOSITORY.md`（行 41, 43）：

Version 0.8.9 redesigns the separate fit-test and Golden Sound windows to match DeviceManager: a dialog-style FAAppWindow (520×720, minimum 400×520) with the shared Mica Alt backdrop on Windows 11 build 22621 or later and an opaque 72 px action bar ordered Back · Next (accent) · Cancel. The Golden Sound wizard follows the supplied ColorOS setup screenshot: an ear-scan → hearing-test stepper, a theme-aware ripple hero mirrored for the right ear, a centred instruction with the tested ear and frequency (Hz only for the verified X4 mapping), twelve ProgressBars in two six-step groups (completed 100, current 50, pending 0), a 12-stop native slider with ticks, toggle-button preview comparison and a profile-name card. Fit results become left/right status cards with an advice InfoBar; Host pause, refusal, noise and inter-ear messages appear once in an InfoBar instead of replacing the page text. `CalibrationViewModel.Phase` derives the page group from stage transitions; commands, ACK gating, STOP confirmation and IPC are unchanged. `MicaAltBackdrop` moves to the `HeyMelodyWindows.Windowing` namespace and is compiled by both DeviceManager and Calibration.UI, which also compiles `OppoGlyphs.cs`; no assembly, package or extracted resource is added. See the [calibration window notes](../gui-notes.md#校准窗口).

Version 0.8.9 verification ran on Linux x64 on 2026-10-09 with the Release configuration. Eleven of the twelve default harnesses passed 545 numbered checks: Protocol 64, Resources 25, Calibration 48, Calibration.UI 11, Devices 19, Control 19, Daily 110, Probe 23, Client 137, App 81 and DeviceManager 8. Calibration.UI adds a phase-transition group and headless assertions for the stepper, the bottom-aligned 72 px action bar and its command order, segment values across both ears, the mirrored right-ear hero, a single inter-ear notice and the dialog window chrome, with light and dark captures. FitTest, HearingSetup and DeviceManager also built for `win-x64`, and Host built with Windows targeting, all without warnings. Host.Tests, Windows.OfflineChecks, the calibration and DeviceManager `--native` window runs, real Mica Alt rendering, packaging and both static verifiers were not executed, and no release receipts were produced. Linux screenshots lack CJK fonts, so they verify layout rather than text rendering; no Bluetooth or acoustic test was performed.

原 `README.md`（行 17）：

0.8.9 重新设计贴合度测试与黄金听感两个独立窗口：沿用设备管理器的上部 Mica Alt 与底部不透明操作栏，按钮按 Windows 向导顺序排列为“上一步 · 下一步 · 取消”。黄金听感参照 ColorOS 设置向导，显示“耳道扫描 → 听音测试”步骤、随主题变化的波纹主图、居中说明与“当前测试左耳/右耳”，进度由左右两组共 12 个进度条组成，当前频段显示为半满；音量条使用原生刻度。结果页用切换按钮比较原音与黄金听感，再命名保存；贴合度结果改为左右耳状态卡片加建议提示。暂停原因、设备拒绝与错误统一显示在信息栏中。详见 [界面说明](../gui-notes.md#校准窗口)。

## 0.8.8

原 `REPOSITORY.md`（行 37, 39）：

Version 0.8.8 reorganizes the main window's control pane. The device name, a connection dot and the connect/refresh commands form a header pinned above the scrolling settings; the dot is green for verified control, yellow while connecting or while a setting write is pending, and grey otherwise (background reads do not change it). The connected caption no longer appends firmware, and an offline cached presentation shows the last connection time beneath the name instead of the cached-settings info bar. The activity ProgressBar keeps a fixed three-pixel slot and fades, so it never shifts the controls. Every card gains an icon and a short description: icons resolve Segoe Fluent Icons code points from the installed font or FluentAvalonia's bundled symbol font at the same code points, with Wireless Earphones vector paths where Segoe has no equivalent. Dynamic feature rows are native settings items, the battery card shows earbud/case glyphs with a charging mark, and the operation log becomes a settings card. See [GUI notes](../gui-notes.md).

Version 0.8.8 verification ran on Linux x64 on 2026-10-09 with the Release configuration. Eleven of the twelve default harnesses passed 483 numbered checks: Protocol 47, Resources 25, Calibration 18, Calibration.UI 5, Devices 19, Control 19, Daily 101, Probe 23, Client 137, App 81 and DeviceManager 8. App.Tests reports 81 on non-Windows (89 expected on Windows); new checks cover the pinned header, the three-state dot, the last-connection caption, card icons and descriptions, and an activity slot whose content positions stay identical during a blocked read and a queued write. The App also cross-compiled for `win-x64`, and Host.Tests compiled with Windows targeting. Host.Tests (Windows-targeted), Windows.OfflineChecks, every `--native-*` window run, packaging and both static verifiers were not executed, and no release receipts were produced. Linux screenshots lack CJK fonts, so they verify layout and icons rather than text rendering; none of this establishes real headset behavior or native ARM64 execution.

原 `README.md`（行 19）：

0.8.8 重新整理右侧控制面板：设备名称、连接状态点和连接操作固定在顶部，圆点以绿、黄、灰表示已连接、正在连接或写入、未连接；连接时不再在“已连接”后显示固件版本，离线时在名称下方显示上次连接时间，取代原先的缓存提示框。进度条使用固定槽位淡入淡出，显示和消失都不会推动下方控件。各卡片补齐 Segoe Fluent Icons 风格图标（无对应字形时使用 OPPO 无线耳机资源中的矢量图标）和简短描述，动态功能行按原生设置条目对齐，电量卡改为图标加充电标记。详见 [界面说明](../gui-notes.md)。

