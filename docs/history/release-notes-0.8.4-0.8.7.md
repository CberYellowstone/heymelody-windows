# 0.8.4–0.8.7 版本记录

以下原段整理自 2026-10-11 文档重整前的根目录 `README.md` 与 `REPOSITORY.md`，保留原版本、日期、设备、平台及验证范围，仅调整链接位置。原段中的 “current”、默认行为与检查数量属于对应历史版本，不作为后续版本的保证。两个来源有重叠时分别列出；原文未给验证日期的条目不补写日期。

历史 `research/`、`artifacts/`、`release/` 链接指本地研究证据或发布产物，通常不随普通 Git clone 提供。完整原稿和摘要清单见本地[本轮整理档案](../../research/documentation-entrypoints-2026-10-11/README.md)；缺少该档案不影响阅读下列公开原段。现行构建与验证入口见[开发说明](../development.md)。

返回[版本索引](release-notes.md)。

本轮核对时，当前工作区缺少下文引用的 0.8.5、0.8.6、0.8.7 `STATIC-VALIDATION.json`。保留原链接作为历史产物路径，缺失文件不能作为本轮验证证据。

## 0.8.7

原 `REPOSITORY.md`（行 31, 33, 35）：

Version 0.8.7 restores the generic vector headphone icon for all device-strip cards without restoring the X4-specific image or custom artwork requests. Copy moves into the bottom of expanded device information. Catalog updates preserve the current identity and restore the actual ListBox selection after binding; an internal empty selection never detaches the control session. The Host remembers the main window's last selected stable device key and stores complete display-only settings per device and resource source. Offline controls remain visible and disabled; a verified reconnect replaces the cached capabilities, including new or removed firmware features. Session identities, write permissions and fresh telemetry are never restored from disk. See [device state](../device-state.md).

Version 0.8.7 verification completed on 2026-10-09: all thirteen default runners passed, with 545 numbered checks across the twelve application/protocol harnesses plus Windows offline ABI/transport checks. Host passed 56, Client 136 and App 88; new regressions cover stable preferred selection, actual ListBox catalog replacement, complete disabled offline controls, restart, invalid caches, failed reads, firmware additions/removals and source isolation, including devices discovered while a source switch waits for commands. The App receipt includes four offline-cache theme/size captures alongside the existing collapsed and information layouts. Separate source runs passed 59 native main-window checks and 40 demo checks, with Automatic selecting OpenGL GPU, 20 meshes, 94,927 triangles and all 34 dynamic controls after expansion. Clipboard tests use an injected writer; these checks do not establish real headset/firmware behavior, full packaged application execution, system clipboard round-trips or native ARM64 execution. See the [aggregate summary](../../artifacts/release-087-20261009/validation-summary.json) and [GUI receipt](../../artifacts/avalonia-ui-20261009-064833-8db56972/validation.json).

All three self-contained architectures contain fifteen version-matched entry assemblies. Both static verifiers passed: [PowerShell receipt](../../release/HeyMelody_Windows_0.8.7_STATIC-VALIDATION.json) verifies 8,507 ZIP paths/sizes, 7,232 critical hashes and nine embedded artwork byte comparisons; [Python receipt](../../artifacts/release-087-20261009/static-python.json) independently checks all three architectures. The ZIP is 586,315,719 bytes, SHA256 `966593a254dc069a2d01761c3d75ff0b871cd889063327b9bb16c96d02783f68`. Final build and verifier logs are `build-client-final.log`, `static-powershell-final.log` and `static-python-final.log` under `artifacts/release-087-20261009`; native logs are in the same directory.

原 `README.md`（行 21）：

0.8.7 恢复底部设备栏的通用耳机图标，仍移除 Enco X4 专属图片，并将复制按钮放到设备信息展开内容的底部。设备连接状态刷新时保留选择与高亮，重启后恢复上次选择的已启用设备。连接成功后缓存功能、选项和设置值；未连接时显示并禁用缓存控件，重新连接并读取后更新，包括固件新增或删除的功能。缓存说明见 [设备选择与离线控件](../device-state.md)。

## 0.8.6

原 `REPOSITORY.md`（行 25, 27, 29）：

Version 0.8.6 restores Automatic/GPU as the model-rendering default while retaining an explicitly saved software choice and the 0.8.5 software materials and lighting. Device-strip cards show only name and status; their icon host, converter and capsule requests are removed. The product view has no duplicate title or caption beneath it. ANC stays visible in a rounded card, while fixed and resource-defined settings groups start collapsed for both real and demo devices and retain deliberate expansion choices. Device information offers a Copy icon followed by its label; it copies the current name, product ID and firmware, handles clipboard failures locally and ignores late feedback after switching devices or closing. The fit-test and information cards share the surrounding eight-pixel spacing.

Version 0.8.6 verification completed on 2026-10-09: all thirteen default runners passed, with 519 numbered checks across the twelve application/protocol harnesses plus Windows offline ABI/transport checks. App.Tests passed 84 checks, including real/demo light/dark wide/narrow default-collapsed layouts and scrolled information-region captures. Separate x64 source runs passed 59 native main-window checks, 40 demo checks with Automatic selecting OpenGL GPU and all 34 dynamic controls available after expansion, and 151 software-model checks retaining the 0.8.5 lighting/material evidence. Clipboard tests inject a writer; these runs do not validate system clipboard round-trips, full packaged client/Host startup, Bluetooth, GPU failure fallback or native ARM64 execution. See the [aggregate summary](../../artifacts/release-086-20261009/validation-summary.json), [GUI notes](../gui-notes.md) and [rendering settings](../model-rendering.md).

Self-contained x64, x86 and ARM64 publishing produced fifteen version-matched entry assemblies. Both static release verifiers passed: [PowerShell receipt](../../release/HeyMelody_Windows_0.8.6_STATIC-VALIDATION.json) verifies 8,507 ZIP paths/sizes, 7,232 critical content hashes and nine embedded artwork byte comparisons; [Python receipt](../../artifacts/release-086-20261009/static-python.json) independently verifies all three architectures. The combined ZIP is 586,273,253 bytes, SHA256 `631aef782a534e0d65ccf9415e7330ed394009370328a8ac070494b11798342c`. Build logs are under `artifacts/release-086-20261009`.

原 `README.md`（行 23）：

0.8.6 恢复默认 GPU 优先渲染，保留已保存的渲染选择及软件回退。底部设备卡片只显示名称与状态，左侧外观下方不再重复设备文字；降噪使用常显圆角区域，其他设置默认折叠。设备信息提供带图标的“复制”按钮，复制当前显示的名称、产品编号和固件版本，贴合度测试与设备信息之间保留一致间距。

## 0.8.5

原 `REPOSITORY.md`（行 19, 21, 23）：

Version 0.8.5 corrects the flat, over-bright software model appearance. Each mesh receives a cloned CPU material whose metallic, roughness and emissive factors represent the map regions covered by its UV triangles, weighted by world surface area; emissive samples are decoded using the pinned renderer's sRGB gamma approximation before averaging. Software lighting preserves curvature without clipping the lit vertex colors. GPU rendering restores the original material references and previous lighting, and Automatic applies the fallback when its actual renderer is software. Original GLB and base-color bytes, full geometry and rotation interaction are retained. This is a bounded approximation of maps the pinned CPU backend omits, not per-pixel normal or occlusion rendering. See the [software material audit](../research/software-model-materials.md) and [rendering settings](../model-rendering.md).

Version 0.8.5 verification completed on 2026-10-09: all thirteen default runners passed, with 516 numbered checks across the twelve application/protocol harnesses plus Windows offline ABI/transport checks. Self-contained x64, x86 and ARM64 publishing produced fifteen version-matched entry assemblies. The PowerShell static verifier passed 8,507 ZIP file paths/sizes, 7,232 critical hashes and nine embedded artwork byte comparisons; the independent Python verifier passed all three architectures. Evidence is in the [aggregate summary](../../artifacts/release-085-20261009/validation-summary.json), [PowerShell receipt](../../release/HeyMelody_Windows_0.8.5_STATIC-VALIDATION.json) and [Python receipt](../../artifacts/release-085-20261009/static-python.json). The combined ZIP is 586,275,471 bytes, SHA256 `240e1aa759e4b539af6b34873cd6deabdb571043e10d1278f5eb566ece05e908`.

The source native model check passed 151 checks. A second [151-check native run](../../artifacts/software-lighting-085-package-20261009/report.json) loaded the published x64 `HeyMelody.dll` and `Ava3D.dll` in the independent model harness, matching their published hashes. It confirmed actual Software rendering of the Dynaudio model, original map bytes and software/GPU material-reference round-trip restoration without initializing a GPU device, with front-case luminance span 18.2126 versus the prior flat image's 5; changing key-light direction changed 38.429% of interior pixels and removing base color changed 97.398%. This runs published GUI and renderer assemblies in a model harness; it does not launch the full client executable, Host or Bluetooth. Ordinary Free4 and X3 were additionally inspected with offline Preview rendering. Native ARM64 and Snapdragon 8cx Gen 3 execution remain unverified.

原 `README.md`（行 25）：

0.8.5 修正软件三维模型过亮、曲面缺少层次的问题：按每个网格实际使用的贴图区域估算金属度、粗糙度和发光参数，并调整软件光照。保留原始模型、基础色贴图及旋转交互；切换到 GPU 时恢复原始材质与原有光照。该版默认关闭 GPU 加速；软件修正在 0.8.6 的软件选择与自动回退时继续应用。依据与限制见 [软件模型材质研究](../research/software-model-materials.md)。

## 0.8.4

原 `REPOSITORY.md`（行 15, 17）：

Version 0.8.4 persists artwork appearance per device and corrects X4 product animation. The Host stores each whitelisted device's product ID and protocol color in `device-appearance.json`, keyed like `devices.json`, and publishes them as display-only `UiDevice` hints. Control sessions read the color once with GET `0x010B`; this adds one GET per connection and the command is unverified on hardware. Cached art is served before any network check, and identical re-resolutions keep the attached renderer. Verified X4 product rotation comes from module 14's animated `modelWebp`, decoded with frame timing and compositing; control-package Lottie lists are gesture guides and never product heroes. The settings dialog adds a default-off 3D GPU switch with software model rendering and a validated "re-download online resources" action (`redownloadResources`, still IPC schema 3). `.github/workflows/windows-build.yml` skips the test runners and publishes x64 and ARM64 packages as artifacts; `Build-Client.ps1 -TestsOnly` runs only the default runners locally. See [artwork persistence](../artwork-persistence.md), [X4 resource audit](../research/enco-x4-artwork-audit.md) and [model rendering](../model-rendering.md).

Version 0.8.4 verification completed on 2026-10-09: all thirteen release test runners passed, with 515 numbered checks across the twelve application/protocol harnesses plus the Windows offline ABI/transport checks. Self-contained x64, x86 and ARM64 publishing and both static release verifiers passed; the final ZIP has 8,507 verified file paths/sizes and 7,232 critical hashes, with nine embedded artwork comparisons. Build logs and the Python receipt are under `artifacts/release-084-20261009`; the final ZIP and PowerShell static receipt are under `release/`. Separate source checks verified both real X4 packages (26 resource checks) and actual x64 software model rendering (125 motion/native checks). These checks do not establish native ARM64 or Snapdragon 8cx Gen 3 execution, Bluetooth behavior or ANC prompt-tone behavior on hardware.

原 `README.md`（行 27, 28, 29, 30, 31, 32, 33, 34, 35, 36）：

0.8.4 修正 Enco X4 的产品动画，并让耳机外观在第一次取得后持续可用：

- 后台按设备记住产品 ID 与颜色，未连接、重启或重新打开界面时都会立即显示图片、模型或动画；只有资源真正变化时才更换。

- 控制连接会只读读取一次颜色（GET `0x010B`），失败时只记为未知；这比此前实机记录多 1 次 GET，需要在 X4 上复验。

- 两种来源均使用官方模块 14 的动态 WebP 播放 X4 产品旋转，保留原始帧时长与局部帧合成；control 包中的捏柄、滑动 Lottie 保持教程角色，主图回退到该型号的 detail 图片。

- 新增“三维模型 GPU 加速”开关，默认关闭；关闭时仍以软件后端显示完整三维模型，并保留拖动与自动旋转。设置保存后即时应用。

- 应用设置改为 FluentAvalonia 设置卡片，新增“重新下载在线资源”。

- 新增 GitHub Actions，构建 Windows x64 与 ARM64 包。

详见 [外观资源持久化](../artwork-persistence.md)、[X4 美术复核](../research/enco-x4-artwork-audit.md) 与 [三维模型渲染设置](../model-rendering.md)。
