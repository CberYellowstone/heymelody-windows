# OPPO 耳机型号与手机组件调研

> 当前客户端仅使用欢律配置与资源，见[当前单源规则](heymelody-only.md)。下文涉及已退出支持来源的覆盖、接口和操作要求仅用于历史研究复核，已由当前规则取代；原版本、路径、摘要和验证范围保留。

本文为公开整理稿；[整理前完整原文](../../research/migrations/organization-2026-10-09/originals/docs/research/oppo-device-support.md)保存在本地研究档案，不随普通Git clone提供。历史版本、来源及验证范围保留，完整研究对象与产物由 `research/` 保存。

> 本页保留 2026-10-06 静态研究阶段的结论。后续已纠正请求版本来源、欢律内置版本加法及兼容名单的非 null 规则，并取得 X3/Free4 云端模型。当前适配请先读 [研究交接](device-adaptation-handoff.md)、[设备配置](cloud-device-configuration.md) 和 [设备美术](cloud-device-artwork.md)；下文的未联网/未获取状态属于当时范围。

核对日期：2026-10-06。范围是用户提供的两份 APK、项目现有 X4 配置和协议实现。只读分析，没有执行 APK，没有向耳机发送命令，也没有验证真实无线音频。APK 原件的 SHA-256 在读取前后保持一致。

## 当前桌面固件策略（2026-10-14）

所有已核实来源配置的型号默认允许连接，不维护阻止连接的固件白名单。来源声明的最低固件条件未满足、未知或无法解释时，只在主界面右上角 warning 提示实际版本、来源条件和受影响功能；不单凭这些条件禁用日常控制、EQ 或校准。原始条件与 `Pass/Fail/Conditional` 结果仍保留，控制使用独立的 `EvaluateForControl`，提示使用 `GetFirmwareIssues`。App 条件、所选来源、已实现 codec、设备实际回报及状态完整性仍限制控制；warning 不授予未知状态或协议写入权限，也不表示已经完成真机验证。

固件显示版本、App 兼容标记与配置内容版本分别使用自己的域，不按耳机固件固定历史配置。当前 `0x0114` 的精确错长兼容以已核实配置和本次请求为依据，不再依赖产品 ID 或固件 125；无法解析时可在既有两次读取预算内建立新会话并跳过该查询。具体边界见 [Air4 Pro 调查](enco-air4-pro-control.md#2026-10-14固件提示与编码查询恢复)。

## 可以据此采取的实现决策

1. 迁移 Avalonia 时保留已经验证的 X4 profile。当前内嵌 JSON 与本次恢复的欢律 `compatWhiteList` 中 `id=06EC10` 条目完全相等。
2. 后续增加按 product ID 注册的型号配置，先选择使用现代 SPP UUID 的 X3/X3s/Free4/Air 系列开展只读查询。配置中的功能版本、EQ ID、降噪编号、频率和互斥条件分别保存；未验证型号先显示身份、连接与电量，逐项启用已确认能力。
3. OEM 专属无损能力独立研究。`highToneQuality=2`、手机属性和图标状态都不能证明 Windows/macOS/Linux 已获得相同编码与传输能力。
4. 当前 UI 使用项目原创产品示意图。APK 中通用默认耳机插图没有可确认的 X4 身份，不将其标作真实 X4 渲染图。

## 输入与可复现方法

| 文件 | 包名 | versionName | versionCode | SHA-256 |
|---|---|---|---|---|
| `research/apps/欢律.apk` | `com.heytap.headset` | 17.6.5 | 117006005 | `EACB314E092CFB6C2F075EEE834498412255CADF184978F3DC16400F6E1DD5D4` |
| `research/apps/无线耳机_17.6.3.apk` | `com.oplus.melody` | 17.6.3 | 17006003 | `C1149DF9E24ED85BF84F7C81C712CEE794299D2B95057C6E221FB547F2717059` |

包名和版本来自 `AndroidManifest.xml` 的二进制 XML 属性，不依据文件名推断。两个 APK 均 compileSdkVersion=36。

复现工具：[inspect_oppo_apk.py](../../tools/research/inspect_oppo_apk.py)；依赖版本见 [requirements.txt](../../tools/research/requirements.txt)。示例在仓库根目录执行：

```powershell
python -m pip install -r tools/research/requirements.txt
python tools/research/inspect_oppo_apk.py research/apps/欢律.apk research/apps/无线耳机_17.6.3.apk --output research/apk-research-new --include-plaintext
```

工具不执行 Android 代码，不调用网络/蓝牙，不解包可执行程序到应用目录。它读取资源表和签名证书，在内存验证 GCM，输出本地 evidence JSON、型号 CSV，以及显式请求后的完整原始 JSON。新研究输出位于 `research/`，默认目录为 `research/apk-research/`；旧 `artifacts/apk-research/` 证据保留并已另存完整研究档案。输出不成为应用运行依赖。APK、完整厂商配置和厂商艺术资源不随仓库重新分发。

### 白名单来源与解码链

| 项目 | 欢律 | 无线耳机 |
|---|---|---|
| Android raw resource | `heymelody_app_whitelist` | `melody_app_whitelist` |
| resource ID | `0x7F110007` | `0x7F100004` |
| ZIP member | `res/30.json` | `res/AQ.json` |
| encrypted bytes | 8947 | 11849 |
| plaintext bytes | 120294 | 205817 |
| certificate ZIP member | `META-INF/ANDROID.RSA` | `META-INF/ANDROIDO.RSA` |
| certificate SHA-256 | `49BD5F8F3F687AA3C71E79D570034455D68945C3EC45BB5F186169B8503C8E3E` | `B0A9BBFC05EEE5E7D0A2C97C030586E15BB3301152078F54473BB82DF6D8C818` |
| plaintext SHA-256 | `0EF07D3361D4AE3E4BF1E5F6AEA5A07CA010AF57B6630588FC1F532AE13CC8B9` | `926B248CFE5B157CD17D1A8F21E9483DF30FD130DD0662B48D4836135DA4D56B` |
| model list | `compatWhiteList` | `whiteList` |

这些 `.json` ZIP 成员实际是加密二进制。欢律 `classes3.dex` 中 `A9.a.get()` 的白名单加载分支调用 `com.oplus.melody.common.util.T.b(Context)`，打开 `0x7F110007`，将流与返回的 byte[] 传给 `com.oplus.melody.model.repository.whitelist.a.n(InputStream, byte[])`。

`T` 的日志名称为 `RepackagingDetector`。`T.g(File)` 读取 APK 的 `META-INF/*.RSA/*.DSA`；`T.a(byte[])` 将 X.509 证书编码作 SHA-256。这个公开证书摘要就是白名单的 AES-256 key。`a.n` 读取 12 字节 nonce，使用 `GCMParameterSpec(128, nonce)`、`AES/GCM/NoPadding`，再用 `GZIPInputStream` 和 `JsonUtils.f` 解码为 `WhitelistContentDO`。工具按照此顺序对两 APK 均完成 authenticated decryption；不是按名称猜测或尝试不校验 tag 的解密。

`WhitelistContentDO.compatToContent()` 是旧字段到当前结构的适配入口。`WhitelistConfigDTO` 和其 `Function`、`Child`、`NoiseReductionMode`、`EqualizerMode`、`LeFilterFunction` 等数据类保留实际编号与版本条件。静态配置只声明官方应用如何识别和呈现产品，不等同本项目对型号的运行测试。

## 型号清单与控制通道

欢律恢复 85 个唯一 product ID：OPPO 53、OnePlus 32，均声明 `supportSpp=true`。OEM 恢复 143 个唯一 product ID：OPPO 58、OnePlus 34、realme 51；其中 `supportSpp=true` 120、false 6、字段缺失 17。欢律全部 85 个 ID 都出现在 OEM 列表中；名称和配置不一定完全相同。

以下表格完整列出恢复的 **OPPO** inventory。它是官方本地配置的清单，不能作为“桌面客户端已经支持 58 个型号”的公告。SPP UUID 缩写：A=`0000079A-D102-11E1-9B23-00025B00A5A5`；B=`00001107-D102-11E1-9B23-00025B00A5A5`。HQ 列为 `function.highToneQuality` 原值（欢律/OEM），`—` 表示字段缺失，不自动解释为 false。EQ/C 列分别为 `equalizer/customEqualizer` 原值，采用欢律条目，OEM 独有条目则采用 OEM。降噪不能仅按是否在表中判断，具体 `noiseReductionMode` 编号见本地 evidence JSON。

| product ID | 型号（配置原文） | 来源 | SPP | UUID | EQ/C | HQ H/O |
|---|---|---|---|---|---|---|
| 061810 | OPPO Enco Air | H/O | true | A | —/— | —/— |
| 068810 | OPPO Enco Air 3i | H/O | true | A | 2/— | —/— |
| 062810 | OPPO Enco Air Lite | H/O | true | A | —/— | —/— |
| 063410 | OPPO Enco Air2 | H/O | true | A | 1/— | —/— |
| 063810 | OPPO Enco Air2 Pro | H/O | true | A | 2/— | —/— |
| 064410 | OPPO Enco Air2i | H/O | true | A | 2/— | —/— |
| 066C10 | OPPO Enco Air2（新声版） | H/O | true | A | 2/— | —/— |
| 064C10 | OPPO Enco Air3 | H/O | true | A | 2/— | —/— |
| 065C10 | OPPO Enco Air3 Pro | H/O | true | A | 2/— | 1/1 |
| 067010 | OPPO Enco Air3i | H/O | true | A | 2/— | —/— |
| 067810 | OPPO Enco Air3s | H/O | true | A | 2/— | —/— |
| 069010 | OPPO Enco Air4 | H/O | true | A | 2/— | —/— |
| 067C10 | OPPO Enco Air4 Pro | H/O | true | A | 2/— | 1/1 |
| 069850 | OPPO Enco Air4i | H/O | true | A | 4/1 | —/— |
| 06F010 | OPPO Enco Air4s | H/O | true | A | 2/— | —/— |
| 06B050 | OPPO Enco Air4（新声版） | H/O | true | A | 3/— | —/— |
| 06D810 | OPPO Enco Air5 | H/O | true | A | 4/1 | —/— |
| 06C410 | OPPO Enco Air5 Pro | H/O | true | A | 4/1 | 1/1 |
| 06C810 | OPPO Enco Air5s | H/O | true | A | 4/1 | 1/1 |
| 06E810 | OPPO Enco Air5s（星光版） | H/O | true | A | 4/1 | 1/1 |
| 062410 | OPPO Enco Buds | H/O | true | A | —/— | —/— |
| 064810 | OPPO Enco Buds2 | H/O | true | A | 2/— | —/— |
| 068010 | OPPO Enco Buds2 Pro | H/O | true | B | 2/— | —/— |
| 06A450 | OPPO Enco Buds3 | H/O | true | A | 3/— | —/— |
| 06A850 | OPPO Enco Buds3 Pro | H/O | true | A | 4/1 | —/— |
| 06A010 | OPPO Enco Buds3 Pro+ | H/O | true | A | 2/— | —/— |
| 06F450 | OPPO Enco Buds3s | H/O | true | A | 4/1 | —/— |
| 06B810 | OPPO Enco Clip | H/O | true | A | 4/— | —/— |
| 06E010 | OPPO Enco Clip2 | H/O | true | A | 4/— | 1/1 |
| 060410 | OPPO Enco Free | H/O | true | B | —/— | —/— |
| 062010 | OPPO Enco Free2 | H/O | true | A | 1/— | —/— |
| 064010 | OPPO Enco Free2i | H/O | true | A | 1/— | —/— |
| 066010 | OPPO Enco Free3 | H/O | true | A | 2/— | 1/1 |
| 068C10 | OPPO Enco Free4 | H/O | true | A | 4/1 | 1/1 |
| 06C010 | OPPO Enco Free4（丹拿版） | H/O | true | A | 4/1 | 1/1 |
| 050410 | OPPO Enco M31 | H/O | true | B | —/— | —/— |
| 050810 | OPPO Enco M32 | O | false | A | —/— | —/— |
| 050C10 | OPPO Enco M33 | H/O | true | B | 2/— | —/— |
| 061C10 | OPPO Enco Play | H/O | true | A | —/— | —/— |
| 050010 | OPPO Enco Quiet | O | false | B | —/— | —/— |
| 063010 | OPPO Enco R | H/O | true | A | 1/— | —/— |
| 065010 | OPPO Enco R Pro | H/O | true | A | 2/— | —/— |
| 065810 | OPPO Enco R2 | H/O | true | A | 2/— | —/— |
| 068410 | OPPO Enco R3 | H/O | true | A | 2/— | —/— |
| 069C10 | OPPO Enco R3 Pro | H/O | true | A | 2/— | 1/1 |
| 06AC50 | OPPO Enco R4 | H/O | true | A | 4/1 | —/— |
| 06B450 | OPPO Enco R5 | H/O | true | A | 3/— | —/— |
| 061010 | OPPO Enco W11 | O | false | B | —/— | —/— |
| 060810 | OPPO Enco W31 | H/O | true | B | —/— | —/— |
| 062C10 | OPPO Enco W31 Lite | O | false | B | —/— | —/— |
| 060C10 | OPPO Enco W51 | H/O | true | B | —/— | —/— |
| 061410 | OPPO Enco X | H/O | true | A | 1/— | 1/1 |
| 063C10 | OPPO Enco X2 | H/O | true | A | 1/— | 1/1 |
| 067410 | OPPO Enco X3 | H/O | true | A | 4/1 | 1/1 |
| 066810 | OPPO Enco X3i | H/O | true | A | 2/— | 1/1 |
| 06BC10 | OPPO Enco X3s | H/O | true | A | 4/1 | 1/1 |
| 06EC10 | OPPO Enco X4 | H/O | true | A | 4/1 | 1/2 |
| 060412 | OPPO O-Free | O | false | B | —/— | —/— |

OEM 比欢律额外收录的 OPPO O-Free/W11/W31 Lite/Quiet/M32 均明确 `supportSpp=false`。不能只使用配对名称和 UUID 尝试把它们接到现有 RFCOMM 后端。旧 UUID B 即使 `supportSpp=true` 也需要独立核对帧格式、命令版本和响应，不能以 UUID 不同或相同直接判定协议相容。

名称不是唯一主键。例如 `067010` 的 `OPPO Enco Air3i` 与 `068810` 的 `OPPO Enco Air 3i` 为不同 ID；别名 `children` 也独立存在。解析按 product ID、实际能力和固件版本决定，不使用包含“Enco”或“AIR”的模糊名称推断。

## X4 两方案差异

相同基础：`id=06EC10`、`children[0].name=OPPO Enco Bohr`、type=T1、supportSpp=true、SPP UUID A、minVersion=16010000。这里 minVersion 是应用配置门槛，不能直接当成固件版本 16.1 或实际当前固件。

| 字段 | 欢律 | OEM | 桌面含义 |
|---|---|---|---|
| `highToneQuality` | 1 | 2 | 显式保存功能版本，继续查 host/codec 条件 |
| `sendHostCapabilities` | 1 | 1 | host 能力声明需按平台真实能力构造 |
| `equalizer/customEqualizer` | 4/1 | 4/1 | 独立内置/自定义命名空间 |
| `customEqFrequency` | 31/62/125/250/500/1000/2000/4000/8000/16000 | 相同 | X4 十段，不套用给无该字段型号 |
| `spatialTypes` | 0/1/2 | 相同 | 保留三模式 |
| `spatialHiResMutex` | 1 | 1 | 保留现有切换前置回读规则 |
| `multiDevicesConnect` | 2 | 2 | 功能版本，不是当前启用状态 |
| `gameMode` | 1 | 缺失 | OEM 可能通过系统联动；不因此删除已验证欢律命令 |
| `dialogTags` | [2] | [1] | UI/host 条件不同 |
| `heyTranslate/heyFindDevice` | 1/1 | 缺失 | 不能由缺失推断耳机没有相应功能 |
| `aiTranslate/aiSummary/meetingAssistant` | 缺失 | 1/1/1 | Android 系统与服务依赖需独立界定 |
| `brLeCoexist/autoSwitchLink` | 缺失 | 1/1 | BR/LE 共存与系统切链不能由 Avalonia 自动获得 |
| `leFilterFunctions` | 缺失 | 按功能、固件、应用版本过滤 | 支持 LE 不表示每项控制都能离开 SPP |

OEM X4 `leFilterFunctions` 中 `goldHearing/fitDetection` 的 needConnectSpp=false，`personalTone/firmwareUpdate/collectLogs` 的 needConnectSpp=true；相应 minFirmVersion=25、minAppVersion=14003000。须研究实际生效逻辑，不把静态条目变成新的实机发送授权。

X4 两配置的 noiseReductionMode 与 equalizerMode 完全相同：ANC 子 7/4/5/6，自适应父 11 下子 12/13/14，关闭子 3，通透子 8；内置 EQ `protocolIndex=[0,7,2,1,3]`。可继续复用已经核实的协议事实；不同产品必须使用自己的编号。控制 gesture support 位图也有 OEM 差异，不能仅复制 UI 按钮数量。

## 无损、host 与 2.3 Mbps 的证据边界

两 APK 都包含 `HIGH_QUALITY_LOSSLESS` 跟踪分类、`highToneQuality` 功能字段、HiQualityAudioItem，以及同名字符串资源：

- `melody_common_high_lossless_title`：高品质无损音质。
- `melody_common_high_lossless_summary`：开启后，支持无损规格音质传输。
- `melody_common_high_lossless_tips_text`：开启前先去手机 Bluetooth 设置开启媒体音频，并切到 LHDC/LHDC Lossless。

欢律 title/tips 资源 ID 为 `0x7F1203BB`/`0x7F1203BA`；OEM 对应为 `0x7F110334`/`0x7F110333`。它们说明此功能与手机媒体编码选择有关，不是单纯耳机控制 UI。

OEM `classes.dex` 的 `r7.b.c(MelodyApplication)`（日志名称 FeatureOptions）读取 `persist.sys.bt.lossless` 存入静态布尔字段 `s`，同时读取 `persist.sys.bt.leacoexist`、`persist.sys.oplus.bt.le_audio_over_spp` 等系统属性。欢律相应入口是 `classes3.dex` 的 `U8.b.c(HeyMelodyApplication)`。`HiQualityAudioItem._init_$lambda$0` 根据实际 `EarphoneDTO` 状态及本地偏好决定 canShowLosslessIcon；图标属于状态呈现证据，不是码率测量。

OEM `classes2.dex` 中 `HiQualityAudioItem$a.a(Function)` 只有下面四个条件同时满足才返回 true：`highToneQuality==2`、FeatureOptions.s=true、`PhoneUtils.h()>=40`、`PhoneUtils.o("wireless.settings.DEVICE_PROFILES_SETTINGS")`=true。`PhoneUtils` 的混淆类为 `com.oplus.melody.common.util.O`；`O.h()` 调用 `com.oplus.os.OplusBuild.getOplusOSVERSION()`，`O.o()` 通过 PackageManager 查询该 Intent 的可用 activity。**40 是 Oplus 自身版本枚举门槛，不能解释为 Android API 40 或某个已确认的 ColorOS 名称**。

`sendHostCapabilities` 在 OEM 的 `classes2.dex: A9.f.h(Object)`（EarphoneRepository 事件分支）首先检查设备能力中有命令十进制 1049=`0x0419`，再检查配置开关，最终生成 featureByte 0x04/0x08，并以内部 API 事件 1108 传递 param_address/param_value。这里的条件调用 `e9.b`（日志 AITranslationUtils）：检查 `aiTranslate`、`aiTranslateCompat.autoRecording`、`heyTranslate`、命令 1064=`0x0428`，以及手机翻译包的 `ear_translate_support`/`ear_translate_support_ai` metadata。因此本条已定位路径的 bits 2/3 表示翻译支持相关 host 声明，**没有证据把它们解释为无损编码/2.3 Mbps 位**。工具及本轮都未发送 0419；其他 host capability 位仍需核对对应构造入口。

可选保存这些类的本地逐指令证据：

```powershell
python tools/research/inspect_oppo_apk.py research/apps/无线耳机_17.6.3.apk --output artifacts/apk-research --dump-class 'Lcom/oplus/melody/ui/component/detail/tonequality/HiQualityAudioItem$a;' --dump-class 'Lcom/oplus/melody/common/util/O;' --dump-class 'Le9/b;' --dump-class 'LA9/f;'
```

evidence JSON 同时保存所读 DEX 的 SHA-256，反汇编包含方法描述符和指令字节偏移，避免依赖不可复现的反编译行号。

在本轮 DEX 字符串池、恢复的配置和可见资源文本中，未发现明确的 `2.3 Mbps`/`2300 kbps`/Find X10 机型条件。没有做 codec Native 层、手机 Bluetooth service/firmware 或实际 AVDTP 包的完整反向工程。因而只能确认 **OEM X4 使用与通用欢律不同的高品质音频功能版本和系统条件**，不能宣称桌面平台已能实现 2.3 Mbps 或真实无损播放，也不能宣称此能力不存在。

后续验证顺序：确认 host capabilities 每个位含义与生成条件；确认耳机 feature/codec 状态上报；确定手机系统编码器及 BT 控制器依赖；在合法取得的样本上检查 codec 协商与实际 payload。桌面 UI 将耳机 Hi-Res 开关、host 编码能力、协商格式与实测传输证据分开保存。项目已有 `tools/audio-evidence` 的 AVDTP 分析可作为实测入口。

## 图片与动态资源

两 APK 的恢复型号条目都没有 icon/image/resourceUrl 字段。`com.oplus.melody.model.net.data.ResourceFetchDO.resourceUrl` 与 DEX 中 `v1/earphone/resource/fetch` 表明产品资源另有动态获取路径；本轮未调用厂商接口，也未获得能够关联 X4 product ID 的资源响应或资源包。

检查了全部 PNG/WebP 的尺寸与欢律全部 ≥350 px 图片。可见候选均不能核实名为 X4：

| APK member | Android resource | 尺寸 | 结论 |
|---|---|---|---|
| `res/iO.webp` | `melody_ui_image_ear_left_default` | 408×660 RGBA | 通用左耳默认图 |
| `res/Dv.webp` | `melody_ui_image_ear_right_default` | 408×660 RGBA | 通用右耳默认图 |
| `res/qa.webp`/`res/Wh.webp` | 上述 xxhdpi 版本 | 272×440 RGBA | 同样是默认图 |
| 欢律 `res/5q.webp` | 通用开盒插图；未绑定型号 | 1080×570 RGBA | 缺 X4 身份证据 |

后续若取得动态资源包，需记录请求 product ID/color、原包 hash、config/member 路径和图片尺寸，再选取 hero 与设备切换缩略图。当前分支原创 SVG 是确定可追溯的交付路径。

## 支持其他型号的落地边界

现有 `src/HeyMelody.Devices/X4Profile.cs` 中 `DeviceModelResolver.FindByProductId` 仅识别 06EC10；本次不修改它。建议下一阶段新增独立型号配置注册表和声明来源，按型号版本及设备实际上报的 command bitmap/feature 清单取交集。配置存在但命令未回报时不提供写入；未知响应保留原始值。

第一批候选选择现代 UUID A 的 X3/X3s/Free4，它们都有预设 EQ、自定义 EQ、高品质模式与双连声明，但配置未必包含 X4 的十段频率；第二批可选 Air4 Pro/Air3 Pro，它们以 EQ 版本 2、不同自定义能力检验 UI 是否真正由能力驱动。每批先离线 replay、身份/能力/状态 GET，再在有实机与明确验证范围时开放每项 SET。

本文是静态互操作研究，不复制厂商 Android 实现到应用；原件和完整逆向输出仅保留本地。已有协议事实及实机边界继续参阅 [phase5-protocol-evidence.md](../history/phase5-protocol-evidence.md)。

**资料归档（2026-10-08）：** 本文访问链接与复现路径已按新目录更新；研究时的原始位置为仓库根 `research/`，原报告与 manifest 内路径保留不变。新旧映射及提交边界见 [资料归档入口](README.md)。
