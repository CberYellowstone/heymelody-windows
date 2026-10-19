# EQ 兼容、颜色与 App/Firmware 条件

> 当前客户端仅使用欢律配置与资源，见[当前单源规则](heymelody-only.md)。下文涉及已退出支持来源的覆盖、接口和操作要求仅用于历史研究复核，已由当前规则取代；原版本、路径、摘要和验证范围保留。

本文为公开整理稿；[整理前完整原文](../../research/migrations/organization-2026-10-09/originals/docs/research/equalizer-selection.md)保存在本地研究档案，不随普通Git clone提供。历史版本、来源及验证范围保留，完整研究对象与产物由 `research/` 保存。

2026-10-08 已逐指令检查用户提供的两份 APK，以下分支由 DEX 直接恢复，未运行 APK、请求网络或操作耳机。固定输入为 `research/apps/欢律.apk`（SHA256 `eacb314e092cfb6c2f075eee834498412255cadf184978f3dc16400f6e1dd5d4`）与 `research/apps/无线耳机_17.6.3.apk`（SHA256 `c1149df9e24ed85bf84f7c81c712cee794299d2b95057c6e221fb547f2717059`）。

只读复现工具 [equalizer_dex_audit.py](../../tools/research/equalizer_dex_audit.py) 先检查 APK 指纹，再输出指令、调用目标、字段、分支目标。运行 `python -B -X utf8 tools/research/equalizer_dex_audit.py`；附加 `--app-markers` 可一并输出 App 版本来源。不生成或改写研究样本。

## 官方列表构造顺序

两份 APK 的 `WhitelistConfigDTO.Function.getEqualizerModeByColorId(int)` 实现一致：

1. 将非 null 的 `equalizerModeCompat` 全部加入新列表。
2. 将 `equalizerModeByColor` 中 `colorId == 实际设备颜色` 的条目加入。
3. 将 `equalizerModeByVersion` 中 `minAppVersion <= 当前 Android App metadata versionCode` 的条目加入。
4. 将基础 `equalizerMode` 全部加入。
5. 使用 `Comparator.comparingInt(EqualizerMode.getOrder)` 稳定升序排列；DTO 的缺省 `order` 为 0。
6. `EqualizerVO.refreshEqModelList` 从以上列表中移除 `minFirmVersion != 0 && minFirmVersion > 实际固件 marker` 的条目。自定义 EQ 页执行同样的固件过滤。

此处是已确认的追加与过滤逻辑；没有按 `protocolIndex` 替代条目、去重，亦没有把其他来源的配置加入同一列表。排序相同的条目保留上述加入顺序。`minAppVersion` 的选取分支仅作用于 `equalizerModeByVersion`；不能对基础或 Compat 条目额外推断 App 门槛。完整原始条件仍在元数据中保存。

| 分支 | 欢律 / `classes3.dex` 绝对 code 偏移 | 无线耳机 / DEX 绝对 code 偏移 |
|---|---|---|
| `Function.getEqualizerModeByColorId` | `0x24cbc0` | `classes.dex 0x589128` |
| 排序 lambda → `getOrder` | `0x24c174` | `classes.dex 0x5890cc` |
| `EqualizerVO.refreshEqModelList` | `LAb/y0; 0x130b34` | `classes2.dex Lca/F; 0x1a53d8` |
| 自定义 EQ 页列表 / 固件过滤 | `LAb/Z; B 0x126038` | `classes2.dex Lca/r; C 0x1a2a0c` |
| App metadata marker getter | `common/util/S.i 0x25dff0` | `classes.dex common/util/O.g 0x59847c` |
| metadata `versionCode` 初始化 | `common/util/S.r 0x25e4cc` | `classes.dex common/util/O.p 0x5987a4` |

`common/util/S.r`、`O.p` 分别将 Android `PackageInfo.versionCode` 保存至字段 `h`，将 `applicationInfo.metaData["versionCode"]` 解析后保存至 `j`；EQ 分支 getter 读取 `j`。因此须传所选资源源的兼容 App marker（本版欢律 `17006004`、无线耳机 `17006002`），不能传 Windows 程序版本、白名单内容版本或欢律 manifest 安装版本 `117006005`。

## 官方固件 marker

`refreshEqModelList` 对实际固件字符串执行 Java `split("\\.")`。恰好三段时，解析前两段：都非零取较小者；其中一段为 0 则取另一段。其他段数只解析第一段。第三段不参与比较；Java split 会舍弃末尾空段。例：`118.119.0`、`119.118.0` 都是 marker 118，`0.130.0` 是 130，`118.1` 是 118。这是该 EQ 分支的固件整数规则，不是语义版本比较，不能推广到所有功能条件。

Android 在缺失固件时使用 marker 0，解析抛错时会记录日志并返回当前列表。Windows 保留该 marker 解释和原条件结果：缺失或无法解析的正固件门槛为 `Conditional`，已知低于门槛为 `Fail`；异常条件保留原 JSON。自 2026-10-14 起，桌面把全部固件条件改为 warning，不再仅因这些结果排除 EQ 条目；来源 App 条件、实际颜色、可编码条目及设备状态仍需通过。该桌面策略与上文恢复的 Android 固件过滤行为分开记录，不将保留条目写成固件兼容已经验证。

Windows API 位于 [DeviceProfile.cs](../../src/HeyMelody.Devices/DeviceProfile.cs)：`GetEffectiveEqualizerModes(DeviceVersionContext? versions = null, byte? color = null)`。未知颜色只保留已经证明的非颜色条目，不假定黑色或第一色；实测颜色 0 是有效颜色。条目的 `EvaluateConditions(context)` 保留 [EqualizerVersionConditionEvaluator.cs](../../src/HeyMelody.Devices/EqualizerVersionConditionEvaluator.cs) 的官方 marker 结果；实际列表使用 `EvaluateConditionsForControl`，`GetFirmwareIssues` 提供固件提示。通用 `VersionConditions.Evaluate` 保持独立域及语义版本规则，不能用原固件结果再次筛除已通过控制条件的 EQ。

## 样本与验收

两套预置资源的 Free4 `068C10` Compat 为 protocol 7 / modeType 34 / firmware 118；X2 `063C10` 为 4 / 16 / 112。Clip2 `06E010` 与 OnePlus Buds Clip `06E410` 的 ByVersion 为 1 / 41 / firmware 130 / App 16009000。Free4 丹拿版 `06C010` 在固件 136、App 15011000 时有效列表的协议顺序为 `0,1,2,7,3,8`。

[Devices.Tests](../../tests/HeyMelody.Devices.Tests/CatalogProfileTests.cs) 验证全部欢律 85、无线耳机 141 预置耳机 profile 可解析、完整原 JSON 保留，单靠配置不能开放 SET，省略 SPP 或外部 realme 路由不获得传输许可；另覆盖 Compat/ByVersion 临界值、缺失域、已知失败优先、未知/实际颜色、稳定排序及重复协议 ID、畸形数组和整数、固件 marker 的三段与其他段数。未知 optional 字段不影响完整元数据保留；错误形状的控制条目不投影为可编码值。自定义 EQ 的异常频率数组不推断替代频段，显式异常容量设为 0。

这些是当时固定样本的静态与模拟验证，不证明所有固件实机行为，也不代替 2026-10-14 warning 策略的回归检查。SPP、已报告命令能力、功能协议版本、App 条件和已知设备状态仍需由调用方共同约束。

## 预设中文标签

名称选择器为欢律 `classes3.dex LAb/h0.d 0x1281e0`、无线耳机 `classes2.dex Lca/y.d 0x1a4bc4`，两个方法的 packed-switch 范围均是 modeType 1–46。每个分支的 `Context.getString(resourceId)` 对应各自 APK 的 `resources.arsc`，所选 `zh-rCN` 文本一致；不能将 modeType 当协议 ID。只读 [preset_name_audit.py](../../tools/research/preset_name_audit.py) 以有界常量/equality/switch 解释复现所有分支、实际 RID 与 Android 字段名，遇到未证明的指令或调用直接失败，不加载 Android 运行时。

| modeType | 默认文本 | modeType | 默认文本 |
|---:|---|---:|---|
| 1 | 经典 | 24 | 悠扬开阔 |
| 2 | 动感低音 | 25 | Reno特调（幻紫星河） |
| 3 | 清澈人声 | 26 | 至臻原音 |
| 4 | 明亮通透 | 27 | 高清解析 |
| 5 | 默认 | 28 | 纯享人声 |
| 6 | Dynaudio 淳朴悠然 | 29 | 澎湃低音 |
| 7 | Dynaudio 温暖轻柔 | 30 | 丹拿特调 |
| 8 | Dynaudio 澎湃爽朗 | 31 | 脉冲低音 |
| 9 | Dynaudio 纯粹原音 | 32 | 悠扬人声 |
| 10 | 久石让大师调音 | 33 | 星河独白 |
| 11 | 均衡（默认） | 34 | 活力动感 |
| 12 | 深海低音 | 35 | 默认 |
| 13 | 明快清亮 | 36 | Dynaudio 淳朴悠然 |
| 14 | 纯净人声 | 37 | Dynaudio 温暖轻柔 |
| 15 | 微醺柔音 | 38 | Dynaudio 澎湃爽朗 |
| 16 | Enco X 怀旧经典 | 39 | Dynaudio 纯粹原音 |
| 17 | 均衡 | 40 | 丹拿人声 |
| 18 | Reno特调（破晓曙光） | 41 | 清晰通透 |
| 19 | 汉斯季默大师调音 | 42 | 丹拿原声 |
| 20 | 自然竹韵 | 43 | 丹拿高解析 |
| 21 | Reno特调（灿烂朝霞） | 44 | 丹拿低音 |
| 22 | 纯粹原音 | 45 | 丹拿经典 |
| 23 | 澎湃爽朗 | 46 | 清亮高音 |

modeType 1/2/3 在 `equalizer` 功能版本等于 2，或设备名称为 `OPPO Enco R` / `OPPO Enco Air2` 时，分别改为“纯粹原音 / 脉冲低音 / 悠扬人声”；modeType 4 仅在设备名称为 `OPPO Enco R` 时改为“微醺柔音”。这里名称只参与已识别 profile 的显示文本选择，不能用来解析未知设备身份。

**资料归档（2026-10-08）：** 本文访问链接与复现路径已按新目录更新；研究时的原始位置为仓库根 `research/`，原报告与 manifest 内路径保留不变。新旧映射及提交边界见 [资料归档入口](README.md)。
