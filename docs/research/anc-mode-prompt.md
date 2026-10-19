# ANC 主模式、子档与提示音差异

> 当前客户端仅使用欢律配置与资源，见[当前单源规则](heymelody-only.md)。下文涉及已退出支持来源的覆盖、接口和操作要求仅用于历史研究复核，已由当前规则取代；原版本、路径、摘要和验证范围保留。

本文为公开整理稿；[整理前完整原文](../../research/migrations/organization-2026-10-09/originals/docs/research/anc-mode-prompt.md)保存在本地研究档案，不随普通Git clone提供。历史版本、来源及验证范围保留，完整研究对象与产物由 `research/` 保存。

2026-10-08 对两份本地 APK 的 DEX 复核确认：官方主模式按钮发送父 `protocolIndex`，子档控件发送子 `protocolIndex`。两者均使用 `0404 [01,01,LSB bitmap…]`，没有不同的提示音标志。原 Windows 主模式按钮会选取记忆或首个子档，再沿子档路径写入。因此 Enco X4 点击“自适应”时，Windows 发送 bit 12/13/14，官方主按钮发送 bit 11；模式正确而提示音不同的现象与此差异吻合。字节差异有静态证据，提示音因果尚未实机验证。

## 固定样本与只读复现

| 样本 | 包名 / 安装版本 | APK SHA256 |
|---|---|---|
| `research/apps/无线耳机_17.6.3.apk` | `com.oplus.melody` / 17.6.3 | `c1149df9e24ed85bf84f7c81c712cee794299d2b95057c6e221fb547f2717059` |
| `research/apps/欢律.apk` | `com.heytap.headset` / 17.6.5 | `eacb314e092cfb6c2f075eee834498412255cadf184978f3dc16400f6e1dd5d4` |

[anc_dex_audit.py](../../tools/research/anc_dex_audit.py) 使用 Python 3.11+ 标准库及既有 DEX 审计 helper，只读 APK，输出到 stdout。运行：

```powershell
python -B -X utf8 tools/research/anc_dex_audit.py
python -B -X utf8 tools/research/anc_dex_audit.py --disassembly
```

脚本先校验完整 APK 指纹，核对主/子 getter 与 repository 的寄存器传递、type=1、0404 命令及未经改写的 payload 转发，再以有界解释器读取 DEX 的构造器、setType、setBit、growArray、getData 和位图类型判断指令。解释器仅处理合成 Python 字段和数组，遇到未支持的指令/调用直接失败，不运行 Android 或 APK。默认输出两包各 13 条负载，共 26 条断言，并检查 10 个 type 判断样例；`--disassembly` 输出完整相关方法便于独立复核。没有解包文件、网络、蓝牙或设备写入。

## 官方调用链与负载

以下偏移均为相应 DEX 的文件绝对偏移，`@` 后为 code_item 起点；另列关键 invoke 地址。

| 定位 | 无线耳机 | 欢律 |
|---|---|---|
| 界面 / repository DEX | `classes2.dex` | `classes3.dex` |
| 主按钮 `NoiseReductionItem$a.c` | `@277778`；顶层 `getProtocolIndex @2779F4 → b.v0 @277A04` | `@33038C`；顶层 `getProtocolIndex @330608 → b.m0 @330618` |
| 子档点击 | `LBa/m;.c @11C230`；子项 getter `@11C27C → b.v0 @11C28C` | `LZb/g;.c @1ED3AC`；子项 getter `@1ED3F8 → b.m0 @1ED408` |
| 默认 type=1 | `earphone/J.v0 @21B3BC → u0(index,1,address)` | `earphone/O.m0 @2DA67C → l0(index,1,address)` |
| `0404` repository | `J.u0 @21B36C` | `O.l0 @2DA62C` |
| 将原始 index 设为唯一 bit | `health/repository/q.get @1D9BB4`，setType `@1D9C2E`，setBit `@1D9C36` | `earphone/y.get @2D7728`，setType `@2D7836`，setBit `@2D783E` |
| SDK DEX | `classes.dex` | `classes3.dex` |
| `CurrentNoiseModeInfo.getData` | `@562A7C` | `@222958` |
| `HeadsetCoreService.I0` | `@576240`；getData `@576292 → 0404 frame @57629E` | `@23719C`；getData `@2371EC → 0404 frame @2371F8` |

主按钮类的完整 descriptor 是 `Lcom/oplus/melody/ui/component/detail/noisereduction/NoiseReductionItem$a;`。两个主按钮方法都从顶层 `mNoiseReductionModeList` 取 DTO，直接发送它的 `protocolIndex`，没有把 `modeType` 或默认子档作为协议编号。两份 SDK 的位图类型为 1/5，此发送链固定 type=1；getData 首字节固定 1。

SDK SHA256：无线耳机 `classes.dex = 34a108859f70f524f9415fd390735073968b90bb70f2a52a284c1d186e52c4fb`；欢律 `classes3.dex = b1ef4c90f60bdc6b333b61a14b6ea99d01a7ad2d7c7ed07776f8a2b46de04025`。

X4 两份型号配置一致。下表只列 `0404` payload，不含 SPP 帧头和序号：

| 用户动作 | protocolIndex | payload |
|---|---:|---|
| 主按钮：关闭 | 0 | `01 01 01` |
| 主按钮：降噪 | 1 | `01 01 02` |
| 主按钮：通透 | 2 | `01 01 04` |
| 主按钮：自适应 | 11 | `01 01 00 08` |
| 子档：关闭 / 通透 | 3 / 8 | `01 01 08` / `01 01 00 01` |
| 子档：智能 / 深度 / 中度 / 轻度 | 7 / 4 / 5 / 6 | `01 01 80` / `01 01 10` / `01 01 20` / `01 01 40` |
| 自适应子档：更少噪音 / 标准 / 更多噪音 | 12 / 13 / 14 | `01 01 00 10` / `01 01 00 20` / `01 01 00 40` |

欢律主点击后直接回调也已只读核对：`Zb/g.d @1ED454` 处理子控件布局；成功回调 `LYa/f.accept @1E4D9C` 的自适应分支调用 `LAb/E.run` branch 18（`11DD9C–11DE0C`），关闭教程气泡并保存 `adaptive_noise_tips_showed`；`Zb/k.e → Zb/h.invoke @1ED6C8` 最终作埋点。这些直接 UI 回调未发现追加子档 SET 或为选择子档而追加 GET；不能扩展为对全部 SDK 后台刷新路径的否定结论。`NoiseReductionItem.setItemSoundEffectDisabled @3313A4` 仅调用 Android `View.setSoundEffectsEnabled(false)` 关闭控件触控音，不是耳机提示音开关。

## Windows 修复契约与验证边界

- `UiAction.Kind="AncMode"` 的 `Value` 为父协议编号，Host 路由到 `DailyClient.ApplyAncModeAsync(primary)`，编码父 bit；原 `"Anc"` 的 `(primary << 8) | level` 保留为子档调整接口。所有主模式统一使用父路径；不补默认子档写入，不增加提示音命令。
- `UiSnapshot.AncPrimary` 独立表达已确认主模式，`CanSetAncMode` 独立于子档 `CanSetAnc`。父 bit 只确认所属模式；有子项的模式在未返回具体子 bit 时，Level/子档保持空值。没有子项的有效模式仍可进入。UI 不用首个选项或本地记忆值伪造当前子档。
- IPC 保持 schemaVersion=3，新增字段采用安全缺省：缺失 `AncPrimary` 是 null，缺失 `CanSetAncMode` 是 false。新 Client 接收旧 Host 快照时禁用新主按钮，不把父请求退回旧子档动作；已有 `Anc` 子档接口保持原语义。这是 schema 3 内的增量接口，不能把同号 schema 当成新主模式能力已存在的证明。
- 主操作回读按已确认 Primary 匹配；允许固件回报父 bit 或同组已配置子 bit。子档操作继续要求精确 Primary+Level。未知 kind/type、零位、多位、配置之外 bit 不开放写入，保留 RawMask；kind 4 只表示已核实的智能实时 ANC 子档，不扩展到自适应。
- 主/子操作共用 ANC 待确认分组；跨刷新、取消、离线 Demo、GET 恢复及 ACK/回读证据分别保留原有操作语义。已经回读确认的相同主模式不重复 SET，主操作待确认时子档仍不自动选中。主切换未确认时，重新写入须经过明确重试确认及新鲜 GET；取消后恢复实际状态，不把暂存选中视为已成功。ACK 接受、状态匹配与提示音是否播放是三个不同证据。

修改前 Daily 模拟测试基线为 **85 项通过**。本轮集成验证共 **390 项通过**：Protocol 46、Devices 19、Daily 97、Host 42、Client 122、App 64。App 与 Host 的 Release 构建均为零警告、零错误。本轮 APK 审计的双样本、26 条负载和 10 个 type 断言通过。代码回归覆盖四个主模式父负载、既有子负载、父/子回读、主模式能力、纯父模式、未知状态及 ACK 不确定性；Host/Client/Avalonia 还覆盖路由、主子操作互斥、父态展示，以及断连或会话世代改变时清除主模式暂存选择。这些离线测试不能证明耳机提示音已修复。

本地集成记录位于 `artifacts/anc-main-mode-20261008-204319`（Protocol、Devices、Daily、Host）、`artifacts/anc-main-mode-ui-20261008-204747/Client.log`（最终 Client）、`artifacts/anc-main-mode-final-20261008-205016`（最终 App、Release 构建与 APK 审计）。较早 App 日志保留当时的失败断言，不能作为最终通过凭证。最终界面报告为 `artifacts/avalonia-ui-20261008-125019-bbb40a39/validation.json`。

## 后续 Enco X4 实机 A/B 步骤

本轮没有执行下列写入。手工验证需使用同一副 Enco X4、同一固件、相同提示音音量、双耳佩戴与播放状态，固定手机参考应用版本。逐次只由一个控制端发起操作，等待回读和提示音结束再进行下一项；记录命令 payload、ACK、回读父/子编号、是否发声及音色描述。手机没有对应抓包时标记“未抓包”，不以静态样本冒充运行时手机字节。

1. 先分别用手机参考与修复后的 Windows 做以下三种主切换：关闭→自适应、通透→自适应、手动深度降噪→自适应。每种来源各重复三次，先恢复同一起始模式。对照 Windows `0404 01 01 00 08`、进入的主模式及提示音；不要把历史子档状态误判为主模式失败。
2. 自适应内依次调整“更少噪音→标准→更多噪音→标准”，对照 `01 01 00 10/20/40/20` 与手机同类子档调整的声音行为。切换前后保留具体子档回读，验证主模式保持自适应。
3. 对“降噪”“关闭”“通透”主按钮分别做从其他主模式进入的 A/B，对照父 bit 1/0/2；再独立调整智能、深度、中度、轻度，确认子档命令仍为 7/4/5/6。
4. 在每个主模式已选中的状态再次点击其主按钮，确认不产生额外 SET；再在关闭与通透间交替切换，确认无待确认分组残留或子档默认写入。直控耳机进入自适应可作为听感参考，记录它与手机/Windows 的听感是否一致。

声音验收要求三种起始模式进入自适应均产生与手机参考相符的提示音，其他主模式与子档声音行为也相符；若回读正确而声音仍不符，保留该差异作为未解决现象，继续根据运行时捕获研究，不将本次字节修复报告为实机声音成功。

**资料归档（2026-10-08）：** 本文访问链接与复现路径已按新目录更新；研究时的原始位置为仓库根 `research/`，原报告与 manifest 内路径保留不变。新旧映射及提交边界见 [资料归档入口](README.md)。
