# 0.8.3 及更早版本与实测背景

以下原段整理自 2026-10-11 文档重整前的根目录 `README.md` 与 `REPOSITORY.md`，保留原版本、日期、设备、平台及验证范围，仅调整链接位置。原段中的 “current”、默认行为与检查数量属于对应历史版本，不作为后续版本的保证。两个来源有重叠时分别列出；原文未给验证日期的条目不补写日期。

历史 `research/`、`artifacts/`、`release/` 链接指本地研究证据或发布产物，通常不随普通 Git clone 提供。完整原稿和摘要清单见本地[本轮整理档案](../../research/documentation-entrypoints-2026-10-11/README.md)；缺少该档案不影响阅读下列公开原段。现行构建与验证入口见[开发说明](../development.md)。

返回[版本索引](release-notes.md)。

## 0.8.3

原 `REPOSITORY.md`（行 55）：

Version 0.8.3 corrects ANC main-mode commands to use the configured parent protocol index. Adaptive child adjustments retain their independent child commands, and a parent-only readback leaves the child unknown. Both pinned official APKs establish the main/child payload distinction; this inspection package still needs the user's hardware check for prompt-tone behavior. See the [ANC evidence and manual A/B recipe](../research/anc-mode-prompt.md).

原 `README.md`（行 38）：

0.8.3 修复降噪主模式按钮的协议编号：进入“自适应”等主模式时发送父编号，细分档位仍独立发送子编号，父态回读不会猜选首个子档。Enco X4 自适应主切换与手机端的字节差异已由两份官方 APK 复核；本检查包的提示音效果仍待用户真机确认，详见 [协议研究与 A/B 步骤](../research/anc-mode-prompt.md)。

## 0.8.2

原 `README.md`（行 40）：

0.8.2 新增会话内布局 Demo：长按主界面“+”两秒即可添加，使用 Enco Free4 丹拿版官方三维模型，展示全部已实现控件。控件修改仅在本地模拟数据中生效，关闭主窗口后移除；也可通过发布包的 `Start-3D-Demo.cmd` 直接进入离线演示。

## 0.8.1

原 `README.md`（行 42）：

0.8.1 修复了资源目录包含两种 OPPO 控制服务、底层却只允许其中一种而造成的连接失败，并修正连接尚未建立时误报“型号未适配”的提示。本机 Enco X4 已通过两套预装及缓存资源的 x64 后台初始化、状态读取和通知订阅验证，全程未发送设置命令；详见 [实机验证记录](../research/enco-x4-0.8.1.md)。

## 0.7.0

原 `REPOSITORY.md`（行 13）：

Version 0.7.0 verification completed on 2026-10-07: all 338 default checks passed (Protocol 46, Devices 13, Control 19, Daily 62, Probe 23, Host 24, Client 96, App 47, DeviceManager 8). Windows ABI checks and separate native Win32 runs passed: 54 main-window checks and 8 shared-backend lifecycle checks. Published x64 and x86 private workers were executed with an uninitialized command and stdin EOF to verify redirected stdio and exit; no Bluetooth was invoked. All three self-contained architectures passed static verification, including 1759 ZIP paths/sizes, 466 critical hashes, independent backend notices and six embedded PNG matches. Repository cleanup also verifies that manager forced termination preserves the shared backend and that staged dropdown targets survive a control generation change. Logs and the aggregate source-test receipt are under `artifacts/release-070-20261007`; the package and static receipt are under `release/`. Real headset broadcasts, phone concurrency, sleep recovery, tray interaction and ARM64 hardware execution are not verified by these checks.

## 早期原生窗口验证（原段涉及 0.6.5）

原 `REPOSITORY.md`（行 114）：

`App.Tests --native-smoke` is a separate Windows run. The 0.6.5 run passed 54 checks of a real Win32 window, geometry, themes, dialogs, image/icon rendering, an eight-device overflowing strip and lifecycle using an in-memory service. It does not start the Bluetooth backend, measure DWM pixels or exercise actual earbud settings. The default App harness writes PNGs and `validation.json` to a new `artifacts/avalonia-ui-*` directory; `--output-dir <path>` selects the directory. Separate manager native modes inspect its DWM backdrop, caption hit regions, resizing, themes and close behavior with mocked devices; the 0.6.3 Close-button fix passed 75 checks each in `--native-smoke` and `--native-alt-f4-smoke`. Those historical runs do not establish execution of every later packaged binary. Script success, simulated transport tests and GUI previews do not establish hardware execution or phone/headset concurrency behavior.

## 2026-10-09 校准重新研究与后续实测

原 `REPOSITORY.md`（行 9, 11）：

The 2026-10-09 hearing enhancement and fit detection reassessment stores its full process, independent APK reports, X4 v116 machine-code audit and implementation comparison under [research/calibration-reassessment-20261009](../../research/calibration-reassessment-20261009/README.md). The [versioned summary](../research/calibration-reassessment.md) corrects UI routing, filter sample counts, scan/preview/save handling, name-byte limits and wear queries (`0109`, whereas `0107` is OTA capability). The subsequent [calibration repair](../research/calibration-repair.md) applies those findings to Protocol, Calibration, Daily, Host and the separate calibration interfaces. That source fix kept the release version at 0.8.8 and first ships in 0.8.9; earlier release test counts describe the preceding implementation.

After the initial static research, the user authorized [live X4 v116 tests](../../research/calibration-reassessment-20261009/LIVE-TEST.md) with temporary research tools. Fit returned Good for both ears; ear scan, twelve wearer-selected thresholds, both filter responses, save/enable and readback completed. The previous profile's public fields were then restored and matched the original backup byte for byte; hidden description NV bytes remain outside that guarantee. A cancelled attempt exposed an unbound research slider callback, which was corrected and independently checked against actual frames. A prior control disconnect remains unexplained. No application build, test or release receipt was created.

原 `README.md`（行 13, 15）：

2026-10-09完成 [黄金听感与耳机贴合度重新研究](../research/calibration-reassessment.md)，并据此[修复现有校准组件](../research/calibration-repair.md)：改正佩戴查询、测听路由、双耳滤波解析、滑块调档与扫描/保存流程。完整研究过程和结果在 [research/](../../research/calibration-reassessment-20261009/README.md)。该次源码修复当时未改变版本号，随 0.8.9 一并发布。

用户授权的[X4 v116实测](../../research/calibration-reassessment-20261009/LIVE-TEST.md)已完成：贴合度双耳良好；黄金听感12项手动测听、两类滤波、保存/启用与读回成功，原方案公开字段恢复并逐字节核对。实测使用研究工具；本次应用修复的验证范围与该实测分别记录，不将其当成修复后的完整客户端实测。
