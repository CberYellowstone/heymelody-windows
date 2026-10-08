# 云端设备配置：研究验收与后续操作适配

本页是 2026-10-07 研究的交接文档。已实际获取欢律和无线耳机的云端配置，完成请求认证恢复、APK 内置配置解密、云端字节保存与本地缓存加解密验证。**配置获取链可复现；其他型号的 Windows 控制命令、状态事件和实机行为仍须逐项适配。**本页不延续已撤销的模块化草稿，也不把 Android 功能声明直接视为桌面能力。

本次验收只读取已有 APK 和正式成功运行，没有发送新的网络请求或操作耳机。原 52 项离线检查全部通过；另外从两 APK 重新恢复请求认证材料并验证 GCM，重算分析与历史结果一致，历史 `results.json`、`analysis.json`、`verification.json` 的摘要均保持不变。配置专项验收报告为本地 [artifacts/research-handoff-whitelist/verification.json](../../artifacts/research-handoff-whitelist/verification.json)。后续统一用 [accept_cloud_research.py](../../tools/research/accept_cloud_research.py) 同时验收配置、美术资源并生成保留清单，最新统一验收路径见交接入口。

## 从这里继续

后续设备操作适配应从 [新会话交接入口](device-adaptation-handoff.md) 进入，再读本页、[OPPO 静态协议与平台条件研究](oppo-device-support.md) 和 [现有实机协议证据](../history/phase5-protocol-evidence.md)。配置原文按以下**显式运行目录**读取，不依据可能已更新的 `latest-run.json`：

`research/online-whitelist/runs/20261007T095049.319796Z`

| 内容 | 同机路径或入口 |
|---|---|
| 认证、解密、获取器 | [fetch_whitelist.py](online-whitelist/scripts/fetch_whitelist.py) |
| 兼容转换、差异、型号清单分析器 | [analyze_whitelist.py](online-whitelist/scripts/analyze_whitelist.py) |
| 独立离线验证器 | [verify_run.py](online-whitelist/scripts/verify_run.py) |
| 静态契约快照 | [static-contract.json](../../research/online-whitelist/evidence/static-contract.json) |
| 正式获取记录 | [results.json](../../research/online-whitelist/runs/20261007T095049.319796Z/results.json) |
| 配置比较及具体 JSONPath | [analysis.json](../../research/online-whitelist/runs/20261007T095049.319796Z/analysis.json) |
| 原 52 项验收记录 | [verification.json](../../research/online-whitelist/runs/20261007T095049.319796Z/verification.json) |

每个正式运行的 `com.heytap.headset/`、`com.oplus.melody/` 子目录保存 `builtin.encrypted.bin`、`builtin.json`、`cloud.download.bin`、`cloud.json`、`cloud.metadata.redacted.json`，以及 `builtin/cloud.local-cache.encrypted.gz` 和 `builtin/cloud.cache-decrypted.json`。`cloud.download.bin` 是传输原字节，`cloud.json` 是按 HTTP Content-Encoding 处理后的原文；此次两者字节完全相同。

研究脚本保存在 `docs/research/`；APK 位于 `research/apps/`，配置、静态契约、缓存测试样本和运行报告位于 `research/online-whitelist/`。脚本依赖 `docs/research/earbud-artwork/scripts/probe_resources.py`、`android_resources.py`、`research_runtime.py`，不可只拷贝三个白名单脚本而漏掉这些依赖。

## 固定输入与实测结果

| 源 | APK 本地路径 | 安装版本 / versionName | SHA256 |
|---|---|---|---|
| 欢律 | `research/apps/欢律.apk` | `117006005` / `17.6.5` | `eacb314e092cfb6c2f075eee834498412255cadf184978f3dc16400f6e1dd5d4` |
| 无线耳机 | `research/apps/无线耳机_17.6.3.apk` | `17006003` / `17.6.3` | `c1149df9e24ed85bf84f7c81c712cee794299d2b95057c6e221fb547f2717059` |

两 APK 当前仍可用，指纹与原研究一致。下表是 2026-10-07 09:50 UTC 的实际响应，不是对未来服务端结果的预测。

| 源 | 请求兼容版本 | 内置有效内容版本 | 云端有效内容版本 | 云文件 / 元数据文件版本 | 字节 / 主条目 / 展开 PID |
|---|---:|---:|---:|---|---|
| 欢律 | `17006004` | `117006004` | `117006007` | `hey-17.6-0928.headset` / `2609280001` | `240331` / `85` / `85` |
| 无线耳机 | `17006002` | `17006002` | `17006007` | `melody-17.6-0928.headset` / `2609280001` | `379989` / `143` / `144` |

两路元数据 POST 和配置 GET 均为 HTTP 200，业务码均为 0，两路均采纳云端。下载内容的本地 SHA256 为：

- 欢律：`0c0288256928824090543dfaea5bd35e6f4538f190e2e9b673f871af7ab46127`。
- 无线耳机：`d75a8612981ece2011617a0ed4314ade8be766e8365a78d58b4d15b1c10ce4a6`。

`.headset` 是 UTF-8 JSON，虽然 HTTP 类型为 `application/octet-stream`。此次没有 HTTP gzip。服务端元数据仅返回 `downloadUrl/fileName/id/size/version`，没有文件 SHA256 或独立响应签名；上述摘要是本地文件指纹，不能称为服务器签名验证。wire JSON 的 `size` 为十进制数字字符串，与实际字节数相等。

## 认证、解密与缓存不能混用

配置元数据接口为 `POST https://iot-earbuds-cn.heytapmobi.com/v1/earphone/new/latestWhiteList`。两个配置源使用各自 APK 恢复的材料与以下三字段请求体，字段值均为字符串：

```json
{"platform":"android","channel":"2","versionCode":"17006004"}
```

上例是欢律；无线耳机是 `channel="1"`、`versionCode="17006002"`。请求头名称为 `Content-Type/appid/ts/nonce/sv/sign`，固定 `appid=earphone`、`sv=v1`。`sign` 为**最终实际发送的 UTF-8 JSON body 字节**的 HMAC-SHA1 小写十六进制；此次签名不包含 `ts/nonce`。脚本使用紧凑序列化、毫秒时间戳和 UUID nonce。后续 C# 客户端应一次构造 byte[]，用同一份字节签名和发送，不能签名后再次序列化。

| 对象 | 处理链 | 认证材料来源 |
|---|---|---|
| 网络请求材料 | APK DEX 中 Base64 字面量 → nonce12 + AES-GCM 解密 | key 由 APK 签名证书 DER 的 SHA256 得到；解密原文直接用于 HMAC，无 gzip |
| APK 内置名单 | nonce12 + AES-GCM 解密 → gzip → JSON | APK 签名证书派生材料；欢律成员 `res/30.json`，无线耳机 `res/AQ.json` |
| 云下载 | HTTPS → 按 HTTP Content-Encoding 解码 → JSON | 此次云文件无额外 AES 包装 |
| Android 私有缓存 | JSON → gzip → nonce12 + AES-GCM；读取时逆序 | 每次安装产生的本地随机 32 字节 key，SharedPreferences `melody-model-settings/whitelistSecretKey` |

GCM tag 为 16 字节，置于密文末尾；使用认证解密，不能忽略 tag。网络材料、APK 证书派生材料、随机缓存 key 是不同用途，不能替换成同一常量。本页和受控研究脚本不保存实际认证材料、签名、完整认证请求头或缓存测试 key。

研究缓存测试使用独立临时随机 key，完成同进程加密、落盘、读回、解密、逐字节比对；篡改密文及错误 key 均拒绝。临时 key 没有留存，因此**无法跨进程再次解密正式 run 的原缓存密文**。新验收检查其记录摘要和已保存明文，并用新 key 在内存独立重复加解密与负例；报告明确区分这两种证明。

## 三种版本与整份配置选择

1. **请求 `versionCode`**来自 Android application meta-data，表示 App 兼容版本。它不是 manifest 安装版本号，更不是正在使用的白名单版本。请求不包含当前白名单版本。
2. **`WhitelistInfoDO.version`**是云文件版本 long，例如 `2609280001`。官方与本地记录作“是否相等”比较：不同或缓存缺失触发下载，不是只有数值更大才下载。
3. **`WhitelistContentDO.versionCode`**是内容版本 int，兼容转换后用于实际采纳策略。

欢律内置加载器把 `17006004` 加 `100000000`，得到 `117006004`。此前把 opcode `0xb0` 解释为减法是错误；它是 `add-int/2addr`。这一偏移只属于该 APK 的内置加载规则，不能加到请求体或云文件元数据版本。无线耳机内置版本为 `17006002`。

`compatToContent()` 对非 null 的 `compatVersionCode/compatWhiteList/compatCaseNumSite/compatAppMarket` 优先采用，包括版本 `0` 和名单 `[]`；只有 null 或缺失才回退普通字段。欢律云 JSON 普通 `versionCode=0`，实际取 `compatVersionCode=117006007`。不能用 C# 空列表判断或 Python truthy 判断替代 null 判断。

官方启动时选内容版本较高的**整份配置**，相等时内置优先；运行中仅在新内容版本严格更大时发布更新。不能把云端条目叠加到内置字段，或把两 App 能力字段取并集。用户所要求的“云端优先”按已恢复行为应解释为兼容且内容版本更新的云端优先。静态契约另记载 provisional 仅补所选官方内容中缺少的 ID；本次 52 项没有独立验收 provisional 合并实现，后续若要复刻该路径须补对应 DEX/边界证据。

离线验收覆盖了 compat null、空列表、0，以及云端较旧、相等、较新、兼容版本覆盖、bool 伪版本、gzip 解压恰好 8 MiB 和超过上限。它核实研究工具执行的语义与此前恢复的契约一致；**不是运行 Android App 的端到端缓存刷新测试**。

## 配置源切换的实现约束

后续设置必须让用户整体选择欢律或无线耳机源。选择范围包括 APK 内置基线、请求认证材料、channel、请求兼容版本、兼容转换、云文件版本记录、内容缓存及全局配置。两源应分别缓存和判断版本，不能比较欢律 `117…` 与系统 `17…` 来决定谁“更先进”；切换源也不能被旧源运行中的版本单调规则阻止。

切换后按新源重新选择完整有效内容并重算当前 product ID 的能力；不要在配置源切换时自动写耳机设置。缺失的源、未知型号、下载失败或无有效缓存应有明确基线和状态。后端只需载入身份、配置和操作翻译数据；型号的图片、3D 包另走资源管线，不能让配置解析器隐式加载 GPU/图片资源。

这些是后续桌面实现需要满足的约束，设置项、缓存服务、云更新服务和跨型号控制适配**尚未接入应用**。本轮只交付研究依据。

## 覆盖范围、差异与操作适配样本

欢律含 OPPO 53、OnePlus 32，共 85 explicit PID。无线耳机主条目为 143，展开后 OPPO 58、OnePlus 35、realme 51，共 144 explicit PID，其中 141 耳机、3 音箱。`whiteList[16].children[2]` 显式声明 `065814 / OnePlus Nord Newton`，继承主条目 `065854`，导致多一个 ID；只有 name 的 children 是别名，不能凭名字生成新 PID。分析器的 child profile 展开采用父字段继承、子字段覆盖，这是清单分析方法，仍须在正式解析器中核对 Android 识别与 child DTO 合并入口。

欢律 85 PID 全部存在于系统版；但两份云文件的 85 个共同 profile **全部有字段差异**，不能因产品 ID 相同就认为两源等价。原配置到云配置各改变 7 个产品，没有新增/删除 PID：`066414`、`066814`、`067814`、`06C410`、`06E010`、`06E410`、`06EC10`。X4/Pro4 以外的五个产品增加 `dialogTags`；X4 和 OnePlus Buds Pro 4 增加 `firmwareTrack`，系统版同时删除其 `immersiveRecord`。详见正式 `analysis.json` 的 comparisons，原文应以两份 JSON 直接对照。

以下定位均指正式 run 的对应 `cloud.json`；原文目录明确、可离线用于解析与操作 UI 输入，**不是可直接发送的 SET 模板**。

| 型号 / PID | 欢律 JSONPath | 无线耳机 JSONPath | 基础 EQ `protocolIndex` 顺序 | EQ 段数 / UI 默认 |
|---|---|---|---|---|
| X4 / `06EC10` | `$.compatWhiteList[0]` | `$.whiteList[0]` | `0,7,2,1,3` | 显式 10 段 / UI2 |
| X3 / `067410` | `$.compatWhiteList[1]` | `$.whiteList[120]` | `0,1,2,3,7` | APK 默认 6 段 / UI1 |
| Free4 / `068C10` | `$.compatWhiteList[8]` | `$.whiteList[134]` | `0,1,2` | APK 默认 6 段 / UI1 |
| Free4 丹拿版 / `06C010` | `$.compatWhiteList[9]` | `$.whiteList[135]` | `0,1,2,3` | APK 默认 6 段 / UI1 |

四者两源均声明 `equalizer=4`、`customEqualizer=1`，但此值是功能版本，不能转成“4 段/1 个预设”。X4 频率为 `31,62,125,250,500,1000,2000,4000,8000,16000` Hz；其余三个没有 `customEqFrequency`，研究 APK 的默认频率为 `62,250,1000,4000,8000,16000` Hz。`customEqUiVersion` 默认 1、`customEqMax` 默认 3 是编译代码行为，非缺失字段的原 JSON 值；对应欢律 `LAb/Z;`、无线耳机 `Lca/r;` 的构造器/静态初始化，定位记录在 `analysis.json` 的 `eq_examples`。四个样本均未显式设 `customEqMax`；默认段数和容量不能推广成所有未知协议的保证，也不确定增益边界、步长或自定义 EQ 存储命令。

Free4 普通版和丹拿版均有 `equalizerModeCompat`：`protocolIndex=7`、`minFirmVersion=118`；丹拿版另有 `equalizerModeByVersion`：`protocolIndex=8`、`minFirmVersion=136`、`minAppVersion=15011000`。2026-10-08 已逐指令确认两 APK 的追加顺序、App/Firmware 过滤、颜色和稳定 `order` 排序，详见 [EQ 条件与定位](equalizer-selection.md)；没有按协议 ID 替代或去重。普通 Free4 和丹拿版的资源或操作适配不能只按名称共用。

`noiseReductionMode[].protocolIndex` 与 `modeType` 各有用途，应保留父子层级。X3 有通透子 `protocolIndex=8/9`；Free4 的自适应项是父 `11` 且没有 X4 的 `12/13/14` 子档；X4 自适应父 `11` 下有 `12/13/14`。UI 文字不能直接当线上的命令编号。`control/callControl` 的 `action/support/defaultFunction` 也需按版本解释；`support` 是位图，未确认的位不能自动生成写入值。

这些样本均为现代 SPP UUID `0000079A-D102-11E1-9B23-00025B00A5A5`。名单中仍存在旧 UUID、显式 `supportSpp=false` 及省略 `supportSpp` 的型号；相同 UUID 不证明帧格式相同，字段省略也不应推断支持。无线耳机 `WhitelistConfigDTO` 的 Kotlin 默认构造/Gson 路径使省略的 primitive `supportSpp` 为 false，相关逐指令记录在资源研究 `probe_resources.py` 中；不能用普通桌面 RFCOMM 配对名匹配扩大支持范围。

X4 的 `highToneQuality` 为欢律 1、系统 2。已有 DEX 证明系统无损入口还依赖 `persist.sys.bt.lossless`、OplusOS 枚举及系统 Bluetooth 设置 activity；`sendHostCapabilities` 已定位 bits 2/3 为翻译支持相关声明，不能标作无损传输位。OEM 专属功能不意味着 Windows 已获得对应编码器、控制器或系统服务；详细证据与已知空白见静态协议研究。

## 强证据定位与尚未证明的部分

| 恢复事实 | 欢律 DEX 定位 | 无线耳机 DEX 定位 |
|---|---|---|
| 网络认证材料入口 | `classes.dex Ll4/a;->d`，code `0x62bacc`、字面量指令 `0x62bafc` | `classes2.dex LC7/a;->c`，code `0x120c44`、字面量指令 `0x120c74` |
| AES-GCM 认证材料解密 | `classes3.dex common/util/E.x`，`0x25a480` | `classes.dex LY6/b.F`，`0x21ce7c` |
| 三字段请求 body | `classes3.dex net/F.t`，`0x2c7320` | `classes2.dex net/D.q`，`0x20a494` |
| 内置版本覆盖 | `classes3.dex A9/a.get`，`0x108684–0x1086ba` | `classes.dex M6/h.get`，`0x1e895a–0x1e8962` |
| `compatToContent` | `classes3.dex`，`0x2522d4` | `classes.dex`，`0x58db58` |

偏移为对应 DEX 的绝对字节偏移，必须与记录中的 APK/DEX 指纹一起使用。APK 内置 JSON 的 GCM 解密和新恢复认证材料均再次验证成功；正式网络 200 记录证明当时服务端接受该请求契约。

目前仍未证明：其他型号每个控制功能的命令号和 payload、事件与响应解码、命令版本和实际能力位图交集、写入成功后的状态回读、未知固件分支、通道切换规则、自定义 EQ 边界及槽位语义、系统专属音频链路。配置能证明识别与呈现声明，不能替代这些协议事实。继续适配时先离线 replay 和只读身份/能力/状态查询，再结合对应型号实机开放已确认的写入；现有 X4 实机事实可复用其已验收范围。

本次报告的两个证据限制必须保留：元数据 HTTP 完整响应原文没有保存，只有摘要和脱敏解析结果，不能重新计算其传输摘要；服务器未给独立下载摘要/签名，离线验收只能证明保存字节与获取记录一致。研究没有运行 APK，不要求 AVD/root/ADB；如果后续要确认 Android 系统条件或真实缓存刷新，静态分析和当前离线测试尚不够。

## 复现命令与避免覆盖历史证据

以下命令在仓库根目录运行。单独白名单脚本使用 Python 3.11 与 `cryptography`；同时验收美术资源的统一入口还需要 Pillow。当前机器已有本地依赖缓存，脚本不会安装依赖。显式在线模式才访问网络；本次验收未执行在线命令。

```powershell
# 推荐：同时离线验收固定配置与美术样本，自动创建新的 artifacts 验收目录。
python -B -X utf8 tools/research/accept_cloud_research.py

# 可选：指定新目录；不能复用已经存在的目录。
python -B -X utf8 tools/research/accept_cloud_research.py --output-dir artifacts/research-acceptance-next-session

# 离线复核固定的正式成功 run；输出目标必须是 artifacts 下尚不存在的文件。
python -B -X utf8 docs/research/online-whitelist/scripts/verify_run.py --run research/online-whitelist/runs/20261007T095049.319796Z --output artifacts/config-review-new/verification.json

# 仅恢复 APK 认证材料、解密内置配置并生成新的离线 run。
python -B -X utf8 docs/research/online-whitelist/scripts/fetch_whitelist.py --offline

# 获取两个配置源，创建新的在线 run；需要请求时才运行。
python -B -X utf8 docs/research/online-whitelist/scripts/fetch_whitelist.py --online --proxy http://127.0.0.1:59991

# 手动填入新产生的 run，不对历史正式 run 原位重写分析/验证报告。
$configRun = 'research/online-whitelist/runs<新的运行目录>'
python -B -X utf8 docs/research/online-whitelist/scripts/analyze_whitelist.py --run $configRun
python -B -X utf8 docs/research/online-whitelist/scripts/verify_run.py --run $configRun
```

`--packages com.oplus.melody` 或 `--packages com.heytap.headset` 可选择单源获取。分析/验证器要求完整两源云样本，不能把单源或 offline-only run 当作正式两源云获取验收。无 `--proxy` 时忽略系统代理环境，直接连接；HTTP 403/429 会停止后续网络请求。默认正常 TLS 证书验证，未复刻 Android TLS pinning。

分析/验证 CLI 默认写 `RUN/analysis.json`、`RUN/verification.json`；验证器新增 `--output` 可写入 artifacts 内的新文件，并拒绝覆盖已有文件。本次先通过模块函数生成新的 artifacts 报告，保护历史原件。获取器还会更新 `latest-run.json` 和静态契约，因此正式证据应按固定 run 读取。8 MiB 解压限制、1 MiB 元数据限制、域名限制和禁止重定向是研究工具的约束，不是已证明的官方 App 规则。

旧 [inspect_oppo_apk.py](../../tools/research/inspect_oppo_apk.py) 的名单选择曾用 truthy `compatWhiteList`；它可复现当时两份非空内置名单，但不能验证空兼容名单的官方语义。后续解析与验收以本页所指新分析器的非 null 规则为准。完整美术资源的获取另见 [云端美术资源交接](cloud-device-artwork.md)：白名单没有模型 URL，`support3DModel` 声明不能替代 product ID、颜色、module ID 与实际资源响应的关联。

**资料归档（2026-10-08）：** 本文访问链接与复现路径已按新目录更新；研究时的原始位置为仓库根 `research/`，原报告与 manifest 内路径保留不变。新旧映射及提交边界见 [资料归档入口](README.md)。
