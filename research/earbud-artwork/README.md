> 本页及 `models.json` 保留 2D/功能美术阶段库存，X3/Free4 的旧未获取状态已由后续 `online-artwork` 成功记录更新。当前成果与资源使用方法见 [美术交接](../../docs/research/cloud-device-artwork.md)。统一离线验收入口是 `python -B -X utf8 tools/research/accept_cloud_research.py`；下文完整重建流程还需本地历史脚本，会重写研究产物，不是只读验收入口。

本目录记录 2026-10-06 至 2026-10-07 的耳机外观与功能美术研究，覆盖 OPPO、OnePlus、realme 耳机，音箱只保留型号索引。早期外观研究使用了提供的欢律与系统无线耳机 APK、获准使用的一加 15 只读 ADB 信息及服务器资源；**本次功能美术追加只读取本地已有的两份 APK，没有联网或操作手机**。产物只存在于 `research/earbud-artwork/`，没有接入 Avalonia/WPF 项目，没有修改输入 APK，也没有执行 APK 代码。

本次新增 **471 个按原始字节去重的本地资产**，补充 Hi-Res、无损音质、杜比、降噪、自适应、通透、聆听，以及音效、空间音频、游戏、电量、设备查找、听力、健康、翻译、连接和操作引导等资源。当前共 **695 个资产、538 个图像预览**，其中 **512 个带功能标签**；图册增加了功能筛选、中文搜索词及 **19 页功能美术接触表**。图片、矢量、Lottie 和绘制配置均保留原件，不把颜色或背景配置当作独立图标。

当前 695 项由 **590 个 APK 本地资产**与既有 **105 个远程独立资产**组成；追加前是 224 个资产、198 个图像预览。功能标签可以重叠，共享依赖会保留相关的多个标签，不能把各功能数量相加当作去重数量。新增功能美术均为通用资源，未据此推断某个型号支持相应功能。

早期已取得 **19 个型号的资源包，共 105 张 PNG 和 19 份配置**，覆盖 OPPO 8 个、OnePlus 1 个、realme 10 个型号；这部分原件与型号关联保持保留。全部产品 ID 与响应一致，19 个原始 ZIP 的服务端 SHA256 校验通过。远程原包共 3,046,698 字节，主清单去重为 100 个 PNG 和 5 个配置，但保留全部型号、配色及包成员来源。140 个耳机产品 ID 中，19 个已确认取得外观，38 个默认色查询成功但无资源包，83 个因默认色未知而未查询；另 3 个音箱仅保留索引。未将通用 APK 外观绑定到型号，也没有取得可确认的 3D 网格文件。

以下是已确认取得的型号。PNG 数按每型号关联的独立字节计数；同一图在两个型号中被服务端复用时，两行都会列入，不能把行数之和当作全局去重数量。配色是协议编号，未推断营销颜色名称。

| 品牌 | 产品 ID | 型号 | 配色编号 | 独立 PNG 数 |
| --- | --- | --- | ---: | ---: |
| OPPO | `060410` | Enco Free | 0 | 5 |
| OPPO | `050410` | Enco M31 | 0 | 2 |
| OPPO | `050810` | Enco M32 | 0 | 3 |
| OPPO | `050010` | Enco Quiet | 0 | 2 |
| OPPO | `061010` | Enco W11 | 1 | 3 |
| OPPO | `060810` | Enco W31 | 0 | 3 |
| OPPO | `062C10` | Enco W31 Lite | 1 | 3 |
| OPPO | `060412` | O-Free | 1 | 1 |
| OnePlus | `050414` | Bullets Wireless Z2 | 2 | 3 |
| realme | `3D59CF` | Buds Air | 1 | 9 |
| realme | `BA5D56` | Buds Air 2 | 1 | 9 |
| realme | `1B5374` | Buds Air 2 Neo | 1 | 9 |
| realme | `060812` | Buds Air 3 | 1 | 9 |
| realme | `F85450` | Buds Air Neo | 1 | 9 |
| realme | `8CD10F` | Buds Air Pro | 1 | 9 |
| realme | `693878` | Buds Q2 | 1 | 9 |
| realme | `C1E2B1` | Buds Wireless 2 | 1 | 5 |
| realme | `C70A6D` | Buds Wireless 2 Neo | 1 | 5 |
| realme | `E3009F` | Buds Wireless Pro | 1 | 5 |

Enco W11 与 W31 Lite 的三张 PNG 字节相同；这项共享来自两次响应及各自资源包的证据，没有按外观相似度推断。型号代表图可以直接查看 [confirmed-models-001.png](contact-sheets/confirmed-models-001.png)。全部远程成员的独立复核结果见 [remote-verification.json](evidence/remote-verification.json)。

入口与文件用途如下。图册直接双击 `index.html` 即可打开，不依赖本地服务器或网络；搜索、功能、类别、来源和类型筛选使用内嵌数据。

| 文件 | 用途 |
| --- | --- |
| [index.html](index.html) | 离线图册；按功能筛选，查看透明底预览、尺寸、型号状态、配色编号及来源，跳转原件 |
| [assets.json](assets.json) | 按 SHA256 去重的资产、全部来源、功能标签及绘制依赖关联 |
| [models.csv](models.csv) / [models.json](models.json) | 143 个产品 ID、品牌、市场别名、默认色声明、3D 声明、取得状态及资产关联 |
| [inventory.csv](inventory.csv) / [inventory.json](inventory.json) | 全量候选库存，含未选中的通用 UI 与非耳机资源 |
| [sources.json](sources.json) | APK 版本、条目与资源种类数量、处理前后哈希 |
| [contact-sheets/](contact-sheets/) | PNG 总览、型号专图及功能美术接触表；功能美术优先展示音质与降噪 |
| `originals/` | APK 原始位图、二进制 Android XML、Lottie、SVG、配置与待识别资源 |
| `previews/` | PNG/SVG 派生预览；二进制 XML 的 `decoded.xml` 位于对应原件目录 |
| [remote/](remote/) | 服务端资源包、提取成员及独立远程资产清单 |
| [evidence/validation.json](evidence/validation.json) | 最终原件、来源、型号关联、图像与离线图册校验结果 |
| [feature-extraction.json](evidence/feature-extraction.json) | 本地功能提取统计、逐条选择依据及 XML 引用闭包 |
| [音质美术证据](evidence/feature-audio-quality.md) / [JSON](evidence/feature-audio-quality.json) | Hi-Res 渐变、无损、杜比、丹拿与空间音频的资源路径和引用 |
| [降噪美术证据](evidence/feature-noise-control.md) / [JSON](evidence/feature-noise-control.json) | 模式语义、状态、主题、语言变体及代码引用 |

两份输入文件的身份与扫描结果如下。条目数不是型号数；Lottie 内嵌图和 UPK 内资源另计入库存，资产则按原始字节的 SHA256 去重。

| 来源 | 包名 / 版本 | ZIP 条目 | PNG + WebP | Lottie | vector / animated-vector | 型号条目 |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| `../欢律.apk` | `com.heytap.headset` / 17.6.5，117006005 | 3008 | 274 + 159 = 433 | 46 | 480 / 38 | 85 |
| `../无线耳机_17.6.3.apk` | `com.oplus.melody` / 17.6.3，17006003 | 2823 | 250 + 124 = 374 | 32 | 500 / 24 | 143 |

欢律 SHA256 为 `eacb314e092cfb6c2f075eee834498412255cadf184978f3dc16400f6e1dd5d4`；无线耳机 SHA256 为 `c1149df9e24ed85bf84f7c81c712cee794299d2b95057c6e221fb547f2717059`。提取前后及最终校验均与这两项吻合。美术候选库存由初次扫描的 2065 条扩展为 **2197 条**，增加了相关组合 drawable 与颜色文件；扫描处理错误为 0。该库存包含未选中的通用 UI，不表示 APK 的所有 XML 都已提取。

型号表从两包经过 APK 证书摘要绑定的 AES-GCM 认证、gzip 解压的原配置恢复：欢律为 `res/30.json` 的 `compatWhiteList`，系统包为 `res/AQ.json` 的 `whiteList`。两个来源按产品 ID 合并，85 项欢律配置均在系统包中。并集为 OPPO 58、OnePlus 34、realme 51；realme 中 3 个是 Cobble、Pocket、Brick 音箱，耳机范围实际为 140 个产品 ID。别名保留为名称关联，不把名称相近的不同 ID 推断为共享美术。[本地与 ADB 复核](evidence/local-adb-review.md)记录了比对口径。

APK 中可直接拿到多种耳机外观，但没有发现把这些内置外观可靠地绑定到某个产品 ID 的配置。具有参考价值的通用原件包括：

| Android 资源名 / 示例 ZIP entry | 内容与原尺寸 |
| --- | --- |
| `melody_ui_detail_default_img` / `res/GZ.webp` | 通用双耳外观，810 × 810 |
| `melody_ui_detail_default_img_neck` / `res/AV.webp` | 颈挂外观，810 × 810 |
| `melody_ui_detail_default_img_ows` / `res/Dy.webp` | 开放式耳夹外观，810 × 810 |
| `melody_ui_image_ear_left_default`、`...right_default` | 左右单耳，含 408 × 660 与 272 × 440 版本 |
| `melody_ui_image_ear_left_ows`、`...right_ows` | 左右耳夹，含 408 × 660 与 274 × 443 版本 |
| `heymelody_app_connect_tips_with_button` / `res/5q.webp` | 充电盒按键配对示意，1080 × 570 |
| `...connect_tips_without_button` / `res/JM1.webp` | 无盒按键配对示意，1080 × 504 |
| `heymelody_app_connect_tips_buds` / `res/SE.webp` | 盒内耳机触控示意，1080 × 564 |

具体映射以 `assets.json` 的所有 `origins` 为准，表中的 ZIP entry 是当前 APK 版本的名称，不是稳定接口。相同 Android 资源名可能包含密度、夜间模式和不同尺寸的变体；保留原件及所有来源，避免只复制一个同名文件而遗漏变体。耳机形状相似或渲染质感像 3D，都不能证明它属于某个型号或是可旋转的 3D 模型。

本次功能美术的重点原件如下，完整默认、night、密度与语言变体以各资产的 `origins` 为准。可直接查看 [功能美术第一页](contact-sheets/features-001.png)，或在图册使用功能筛选。

| 功能 | Android 资源名 / 示例 APK entry | 已取得内容 |
| --- | --- | --- |
| Hi-Res | `melody_app_hi_res_tag` / `res/Z1.xml`、`res/42.xml`；`melody_ui_hi_res_audio_icon` / `res/L_.xml` | LHDC + Hi-Res 组合徽标与独立 Hi-Res 图标；保留 default/night 和径向渐变依赖，预览还原原始金色 |
| 高品质无损音质 | `melody_ui_hi_lossless_audio_icon` / `res/hq.xml` | 83 × 16 蓝底 LHDC Lossless 矢量徽标 |
| 杜比与丹拿 | `melody_ui_ic_dolby` / `res/12.webp`；`melody_ui_equalizer_dynaudio` / `res/s9.xml` | Dolby 位图、Dynaudio 矢量与多密度 Logo |
| 降噪、自适应、通透 | `icon_noise_reduction_strong`、`...weak`、`...adaptive`、`...transparent`；通用矢量自适应 `res/DE.xml`、通透 `res/hF1.xml` | 模式图标、选中与未选中位图、禁用状态及相应背景/颜色依赖 |
| 关闭与聆听 | `icon_noise_reduction_normal`、`...listen` | `normal` 是关闭模式（modeType 1），`listen` 是独立聆听模式（modeType 6），分别标为“降噪关闭”“聆听” |
| 空间音频 | `melody_ui_spatial_audio_closed`、`...fixed`、`...header`、`...preview_anim` | 关闭、固定、头部跟随图标及试听 Lottie |
| 其他功能 | 功能筛选中的音效、游戏、电量、设备查找、听力等 | 图标、说明插图、操作引导、动画及相关绘制配置 |

自适应降噪（modeType 10）与自适应听力教程分别标注；健康界面的 `transparent_appbar` 不归为通透模式。`noise_child_item_bg` 是按下/悬浮背景，不是选中状态。`strong_realtime` 的默认与 `zh-rCN` 是英中文案变体，不是日夜主题。虽然资源名没有直接写出 codec，实际预览确认已取得上述 LHDC 组合标识与 LHDC Lossless 徽标；LDAC 等目前只有文案或名称检索证据，未确认对应图标。

Hi-Res 的渐变来自 `$melody_app_hi_res_tag__0` 和 `$melody_ui_hi_res_audio_icon__0` 的原始颜色 XML；49 个颜色/渐变文件与 74 个组合 drawable 同样保留二进制原件、解码 XML 和引用关系。图册将它们显示为配置卡片；Lottie 保留原始 JSON，不在页面播放动画。Android vector 原件是二进制 XML，PNG/SVG 仅供研究预览；本地可确定的颜色与渐变按原件解析，主题属性仍需运行时状态，未解析项使用明确标注的中性色，动画矢量只显示基础静帧，不能把预览当作完全等效的 Android 渲染或完整动画。

还保留配对、听力、佩戴诊断、翻译、跨设备连接、扫描等插图与动画，以及系统 `assets/melodywidget-1000012-release.upk` 中的 5 个通用耳机 SVG。部分 Lottie 内嵌小图已单独提取，原始 Lottie JSON 同时保留。功能提取重复运行的资产对象和来源列表保持一致，未重复追加条目。

最终离线校验通过：逐项核验所有原件 SHA256、730 条 APK 来源、634 条功能选择记录及 76 条绘制依赖；538 张预览和 52 页接触表均可解码，19 页功能表完整覆盖 370 项功能预览，图册内嵌资产数据与主清单一致。两个输入 APK 哈希不变，既有 19 个型号的代表图与来源关联通过复核。图册搜索 LHDC 可定位准确的 5 项资产；功能筛选中的自适应降噪、通透和聆听分别保留独立标签。

服务器资源通过 APK 静态调用链找到。基础服务为 `https://iot-earbuds-cn.heytapmobi.com/`，元数据是 POST 请求，包名决定 channel（系统包 1、欢律 2），body 带 platform、versionCode、productId、color。普通外观入口为 `v1/earphone/resource/control`，使用响应中的 `cartoonUrl` 与 SHA256 下载 ZIP；外观模块入口为 `v1/earphone/resource/fetch`，额外带 `moduleId=14`，涉及 `model3D`、`modelWebp` 及外观配置。[resource-contract.json](evidence/resource-contract.json)保存接口、DTO、缓存路径和 DEX 类/方法/偏移证据。

元数据请求需要签名。已确认的调用链使用 APK 本地加密应用材料及其公开签名证书，不依赖手机账号或设备标识；复现脚本仅在内存中解密并计算请求签名，不把密钥、签名、nonce、认证头或完整下载 URL 写入研究证据。资源下载只接受厂商 `heytapimage.com` 域下的 HTTPS，禁止跳转，校验服务端给出的 SHA256，并校验 control 响应的产品 ID 与请求一致。ZIP 原包保留，成员有路径及大小限制，未运行其中内容。

首次可确认的结果是 `060410`（OPPO Enco Free）、配色编号 0 的 control 包，SHA256 为 `9ef29c1995fc87d31df0ff608ee6f352a0ac1065f9cac8baa4c67e0ff92eadbf`。包中有 `config.json` 与 5 张透明 PNG：整体外观 780 × 780、左右耳各 408 × 660、盒图 216 × 216、连接卡片图 114 × 114。请求、响应产品 ID、白名单名称及包哈希构成型号关联证据；编号 0 不自动命名为白色。

扩大查询前，已核验应用的默认配色解析：输入不为 -1 时原样返回；输入 -1 优先用配置中的 `defaultColor`；否则 realme 的 `supportSpp=true` 返回 0，false/缺失返回 1；没有显式默认色的 OPPO/OnePlus 保持未知值 -1。DTO 的 `supportSpp` 是 primitive boolean，构造默认 false，普通 Gson 解析缺失字段仍为 false，不需要手机运行状态。选择清单见 [default-color-selection.json](evidence/default-color-selection.json)：57 个有依据的默认配色候选（9 个显式默认、48 个 realme 耳机），跳过 83 个默认配色未确认的耳机及 3 个音箱。每型号只查一个默认配色，已有观察复用，无未知配色或模块扫描。

请求记录见 [server-probes.json](evidence/server-probes.json)。批次单并发，所有网络请求含 CDN GET 速率不超过每秒 1 次；遇 HTTP 403/429 停止，下载和解压大小受限。`HTTP 200 / code 0` 但没有资源地址只代表该次请求没有提供包；请求失败也不能推断该型号没有美术。`models.json` 对未取得型号保留具体状态与原因。服务端内容可能随时间、地区、版本或配色变化，本次仅确认已取得的包和已查询条件。

本轮未取得经确认的 3D 网格文件。两份白名单各有 16 个产品 ID 声明 `support3DModel=1`；该字段是能力声明，不是模型原件。计划阶段曾观察到 Enco X3（`067410`）与 X4（`06EC10`）在配色 0、module 14 下的签名请求返回成功但无资源数据，这几次仅有简要观察记录，未保存精确时间和原响应，没有为补证重新发送。不能据此排除其他配色或条件存在模型。

静态代码还引用下载包内 `res/raw/detail_model.vfxms`，但没有取得该文件，格式未确认。`res/iD.json` 的 image dress 配置与 `res/Hx.json` 的 video 配置包含 `connected_box.png`、`connected_buds.png`、背景及视频目录等相对路径，所指成员没有打包在这两个 APK 中。GLSL `res/29.glsl`、`res/kO.glsl` 用于 RGB/alpha 双区视频合成，也不是网格。`assets/melody.bin` 保留为未知二进制，未声称是模型；这些线索及配置原件可供下一轮研究使用。

手机侧只读研究见 [adb-readonly.json](evidence/adb-readonly.json)。这台一加 15 报告为 PLK110、Android 17 / SDK 37，shell UID 2000、SELinux Enforcing。已在内存读取两包安装 APK，字节与本地输入完全一致；两包都不可调试，`run-as ... id` 失败，应用私有下载目录访问被拒。系统包默认使用设备保护存储，候选目录是 `/data/user_de/0/com.oplus.melody/files/melody-model-download/`；欢律为 `/data/user/0/com.heytap.headset/files/melody-model-download/`。指定进程的现有日志样本与深度受限的共享 cache 查询没有提供可用下载信息，不能推断手机从未缓存资源。

额外读取了手机预装 `com.oplus.wirelesssettings` 的 WirelessSettings APK，仅在内存检查，未另存 APK；其中有通用耳机图标和指向 `com.oplus.melody` 的入口，没有找到型号专图或模型。没有启动/停止应用、调用 provider/service、安装 APK、清除/修改日志、修改手机文件或请求 root。没有保存账号、序列号、蓝牙地址、日志正文或认证材料。本次取得线上资源的突破来自 APK 调用链，ADB 提供了包身份和可访问性验证。

复现时先进入仓库根目录。脚本默认输出均在本研究目录；它们复用本机已有 Python/Pillow/cryptography 与 ImageMagick，不会自动安装依赖。以下流程只重建研究文件，`--offline` 不联网，也不连接手机；重新提取会重建本地资产清单，随后合并既有已校验的远程包。

```powershell
$py = 'C:\Users\LOZAP\AppData\Local\Microsoft\WindowsApps\PythonSoftwareFoundation.Python.3.11_qbz5n2kfra8p0\python.exe'
& $py -B research/earbud-artwork/scripts/extract_artwork.py --apk research/欢律.apk --apk research/无线耳机_17.6.3.apk
& $py -B research/earbud-artwork/scripts/build_model_index.py
& $py -B research/earbud-artwork/scripts/probe_resources.py --offline
& $py -B research/earbud-artwork/scripts/merge_and_verify.py --merge
& $py -B research/earbud-artwork/scripts/extract_feature_artwork.py
& $py -B research/earbud-artwork/scripts/build_gallery.py
& $py -B research/earbud-artwork/scripts/merge_and_verify.py --check-gallery
```

如需复现网络查询，显式运行 `probe_resources.py --known-defaults`；已有同型号、同配色观察会复用，不补查未知配色。单个命名样本可用 `--probe --case enco-free-control`，该参数会发起新的实际请求。ADB 采样单独由 `capture_adb.py --transport <已连接的transport整数>` 触发，不在离线重建流程内；transport 以当时 `adb devices -l` 的已授权连接为准。本次研究已完成所需采样，不需要为阅读成果再次操作手机。

现有资源及复现脚本只整理为研究成果，尚未应用到项目中。
