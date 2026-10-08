# 云端设备美术资源：验收与后续适配交接

**2026-10-09 更新：X4 产品旋转已复核。** 两来源对 `06EC10 / color=1 / moduleId=14` 均返回动态 `modelWebp`，非 GLB 或 Lottie。control 包的 Lottie 属于操作教程，不能成为主展示；`support3DModel` 也不是 WebP 获取的必要条件。原件摘要、APK 加载调用与解码证据见 [X4 美术报告](enco-x4-artwork-audit.md)。下文旧库存和未取得状态保留其原采样范围。

**2026-10-08 更新：Free4 丹拿版已取得。** `productId=06C010`、`color=3`、`moduleId=14` 在两种 App 来源下均返回同一官方资源包，下载及独立离线复核通过。原件、GLB、精确摘要和复现见 [丹拿版模型研究](enco-free4-dynaudio-model.md)。下文 2026-10-07 的色号查询范围及未取得状态属于历史记录，不能继续作为丹拿版当前状态。

本文件整理截至 2026-10-07 已取得、能离线复核的美术研究成果，供下一会话进行设备美术适配。资源请求、请求认证和原包解析已经在本地研究脚本中验证；目前应用没有云端美术下载器。普通 Enco Free4 的原始 GLB 与交互已迁入 `arch/modular-devices` 的 0.7.1，当前仍显示在 X4 控制界面中，不能将此视为 Free4 设备操作已适配。

此次验收没有发起网络请求或操作手机，没有重新提取或覆盖研究原件。两份正式模型下载记录、导出模型以及完整离线美术图册均重新通过校验。原 `verification.json` 和图册 `evidence/validation.json` 的 SHA256 在验收前后保持一致，新报告存放于 [artifacts/cloud-research-acceptance](../../artifacts/cloud-research-acceptance/artwork-summary.json)。

## 读取顺序与现有库存

| 入口 | 能直接使用的成果 | 使用范围 |
|---|---|---|
| [online-artwork/README.md](online-artwork/README.md) 与本文 | X3、普通 Free4 两份官方模型 ZIP，原容器及原字节导出 | 精确产品 ID、请求色号、App 来源与下载哈希绑定 |
| [earbud-artwork/index.html](../../research/earbud-artwork/index.html) | 离线图册，按来源、类型、功能和中文关键词筛选 | 查看外观、Hi-Res、降噪、EQ、空间音频等美术 |
| [earbud-artwork/assets.json](../../research/earbud-artwork/assets.json) | 原件路径、SHA256、全部 `origins`、主题/密度变体、功能标签和绘制依赖 | 使用原件时保留来源及依赖，不以 PNG 预览替代原矢量或动画 |
| [earbud-artwork/models.json](../../research/earbud-artwork/models.json) | 143 个内置产品 ID，140 个耳机、3 个音箱；市场别名与型号关联 | 历史内置索引，不是当前云端白名单全量索引 |
| [earbud-artwork/remote/remote-assets.json](../../research/earbud-artwork/remote/remote-assets.json) | 19 个型号的 control ZIP 与逐型号成员来源 | 响应 `productId` 与请求相同，已确认型号关联 |
| [target-profiles.json](../../research/online-artwork/evidence/target-profiles.json) | 六个精确目标配置的能力/配色字段、下载证据、DEX 调用链 | X3、普通 Free4、Free4 丹拿版 × 两种 App 来源 |
| [资源调用契约](../../research/earbud-artwork/evidence/resource-contract.json) / [容器加载契约](../../research/online-artwork/evidence/model-format-contract.json) / [无线耳机旋转契约](../../research/online-artwork/evidence/rotation-contract.json) | DTO、方法、缓存名、DEX 条目与字节偏移 | 重建请求/加载行为时的静态依据 |

离线图册主清单共有 **695 个按保存资产字节去重的条目**：590 个 APK 本地资产，105 个远程资产；后者为 **100 个独立 PNG + 5 个独立配置**。远程逐来源清单有 124 个条目，即 19 个型号各自的配置及合计 105 张 PNG；不能将这些来源条目等同于全局去重资产数。19 个原 ZIP 合计 3,046,698 字节，保留全部型号与配色关联。

主清单包含 214 个 bitmap、317 个 Android vector、2 个 animated-vector、20 个 Lottie、5 个 SVG、49 个颜色/渐变配置、74 个组合 drawable、10 个普通配置、2 个 GLSL、1 个 UPK 与 1 个未识别二进制。538 个预览、52 页接触表、512 个带功能标签的条目、76 条绘制依赖均重新通过校验。上述数量不含 `online-artwork` 中后来取得的两份模型包。

**历史状态需要纠正读法**：`earbud-artwork/models.json` 当前仍把 X3 与普通 Free4 记录为 `not_obtained`，它没有合并后续 `online-artwork` 模型研究；`earbud-artwork/README.md` 的“未取得经确认的 3D 网格”和 `online-artwork/README.md` 的“没有实现渲染”也分别属于当时阶段。保留旧记录便于追溯，当前模型取得状态以本文及正式 model run 为准；当前桌面展示状态以 [0.7.1 Free4 文档](enco-free4-0.7.1.md) 为准。

## 请求、认证、版本与缓存

公开基础服务为 `https://iot-earbuds-cn.heytapmobi.com/`，请求是 JSON POST。两条资源接口用途不同：

| 项目 | 普通外观 | 模型外观模块 |
|---|---|---|
| endpoint | `v1/earphone/resource/control` | `v1/earphone/resource/fetch` |
| 共有请求字段 | `platform`, `channel`, `versionCode`, `productId`, `color` | 相同 |
| 模块字段 | 不发送 | `moduleId="14"` |
| 下载地址字段 | `cartoonUrl` | `resourceUrl` |
| 响应型号字段 | 有 `productId`，下载前与请求比对 | 当前 DTO/成功响应没有独立 `productId` |
| 包内外层配置 | `detailImageRes`, `leftImageRes`, `rightImageRes`, `boxImageRes`, `capsuleImageRes` 等 | `model3D`, `modelWebp` 分开定义；当前两包仅含 `model3D` |
| OEM 缓存名 | `control_<productId>_<resolvedColor>` | `fetch14_<productId>_<resolvedColor>` |

本文的“资源模块”对应线上字段 **`moduleId`**，没有验证另一个名为 `resourceModule` 的请求字段或通用模块枚举。`support3DModel=1` 和 `customDress=1` 是配置中的能力声明，不含可直接下载的模型，也不证明特定配色已提供模型。三个精确目标在两份云端配置中都声明了这两个能力；六个配置均没有 `defaultColor`、配色枚举或型号专属模型路径。

正式模型获取使用的版本如下。**manifest 安装版本、request meta 版本与白名单有效版本不是同一个字段。**

| 来源 | 包名 | manifest 安装版本 | 资源请求 `versionCode` | 已取得云配置有效版本 |
|---|---|---:|---:|---:|
| 欢律 17.6.5 | `com.heytap.headset` | 117006005 | `"17006004"` | 117006007（`compatVersionCode`） |
| 无线耳机 17.6.3 | `com.oplus.melody` | 17006003 | `"17006002"` | 17006007（`versionCode`） |

正式模型脚本从 **application meta-data.versionCode** 取请求版本；欢律 `channel="2"`，无线耳机 `channel="1"`。例如 X3 的成功欢律请求体为：

```json
{"platform":"android","channel":"2","versionCode":"17006004","productId":"067410","color":"2","moduleId":"14"}
```

全部字段均为字符串。请求签名对实际发送的 UTF-8 紧凑 JSON 字节计算，不能签名一个对象后又以另一种空白/字段顺序重新序列化发送。认证沿用已验证的 APK 证书绑定 AES-GCM 认证材料恢复与 HMAC-SHA1 请求签名流程；只在内存中处理材料、时间戳、nonce 与签名，无需登录账号、AVD、root 或手机设备标识。详细算法与配置解密交接见 [云端设备配置](cloud-device-configuration.md)。本文与研究证据不保存签名、认证材料或完整 CDN 下载 URL。

早期 `earbud-artwork/scripts/probe_resources.py` 使用 manifest 安装版本发出 control 探测，19 份已取得原包的字节及响应型号关联仍有证据价值；但其 `resource-contract.json` 的旧版本描述不能作为新客户端应使用的版本规则。后续适配应复用 `online-artwork/scripts/fetch_artwork.py` 已纠正的版本来源，不能原样照抄旧请求构造。

OEM 文件根目录来自 `Context.getFilesDir()/melody-model-download`；系统 App 候选位置为设备保护存储 `/data/user_de/0/com.oplus.melody/files/melody-model-download`，欢律为 `/data/user/0/com.heytap.headset/files/melody-model-download`。这只是静态缓存路径契约，普通 ADB 未能读取私有目录，不能声称已复核手机缓存内容。桌面将来的持久化缓存需保留来源、产品、配色、模块、版本与摘要，不能因两份当前模型字节相同便推定其他型号/来源可以共用配置。

## 配色与型号归属边界

正式模型研究共记录 20 次串行元数据请求、2 次 CDN 下载；所有网络请求间隔至少一秒，没有继续枚举其他产品或模块。

| 精确型号 | 产品 ID | 两来源查询过的色号 | 返回模型的色号 |
|---|---|---|---|
| OPPO Enco X3 | `067410` | -1、0、1、2 | **2** |
| OPPO Enco Free4 | `068C10` | 0、1 | **1** |
| OPPO Enco Free4（丹拿版） | `06C010` | -1、0、1、2 | 本次均未返回资源 |

X3s `06BC10`、X3i `066810` 都不是 X3；普通 Free4 也不是丹拿版。不能按市场名称相近、模型看起来相同或单个参数成功，把包扩展归属给另一个产品 ID。`color=1/2` 只是成功请求的协议编号，未取得营销颜色映射，不称为默认色，也不将容器中的内部场景名称当作商品配色证据。

模型成功响应含 `fileName/resourceName/resourceDescription/resourceUrl/sha256`。文件名为 `android_all_<productId>_<color>_14.zip`；请求、唯一响应、对应文件名和包哈希一致，但因响应没有独立产品 ID，归属级别为 **`request_only`**。control 资源则具有请求与响应产品 ID 双向匹配，归属级别为 `request_and_response_productId`。两种证据强度不要混写。

其他模型查询的 `HTTP 200 / code 0` 空 `data` 仅证明在该次来源、产品、色号、请求版本和时间未返回包，不能证明不存在模型；丹拿版后来在色号 3 取得模型，X4 或未知默认配色仍保留未确认状态。

早期 control 默认色解析已静态验证：已知输入原样保留；输入 -1 时先取配置的 `defaultColor`；没有显式默认值的 realme 耳机按 `supportSpp=true → 0`、false/缺失 → 1；其余 OPPO/OnePlus 保持 -1。57 个有依据的耳机候选只查一个默认色，19 个取得外观、38 个成功空响应；83 个耳机默认色未知未查询，3 个音箱排除。此规则没有给 X3/Free4 推导出默认色，后来模型请求使用了明确、有限的研究色号。

## 两份官方模型原件与精确摘要

以下路径均相对仓库根目录；验收时原 ZIP、提取容器、各导出段和当前应用 Free4 GLB 都存在且摘要一致。

| 文件 | X3 字节数 / SHA256 | 普通 Free4 字节数 / SHA256 |
|---|---|---|
| `resource.zip` | 9,581,441 / `b4763c5f9f790b0859131807324bf81c4a1855489fc087e715f673de848119bf` | 8,885,630 / `068676a8bb633f35e741f2d0dc7eb919afcb03e89f5c6839b41adbec53287bb1` |
| `config.json` | 67 / `20dd6bcf9fc16b9f42c6ad2835d322ebab4059a6430fdee92eb6efa9c80a2f71` | 相同 |
| `detail_model.vfxms` | 12,932,064 / `5b57fbff64ecc4e2521e693847fc2dc49f5e6d3598a6f76cdacb053a990795aa` | 13,397,063 / `1c5fa169f224b2f7e9f2bc7ee535c39e4accf26ff65fc792aa94ca49e03a6154` |
| `scene-config.json` | 1,096 / `7dd9c8ed394b2e2fcd24d884a49e56d3528eacc57eeabc5e5bec056a4ccb375a` | 1,099 / `9a26de030c63e7d8db96fdf31f813b9eb77c5f1bf9b51064fc15dde3695f75b0` |
| `model.glb` | 10,835,448 / `6f6ab09e3b5067d3f1d4fac732243bb5fe723e3eb982d9b84e629560e5813363` | 9,727,500 / `c49540d3e5afd7af8bb5aa562d3dc00e35246361e6efc14fa142a749b7d22bcc` |
| `ibl.ktx` | 2,095,480 / `4fcb38f18fb9c97c527a3c9191820881d47f6d6b6a57e0af4b2d0cfab436c86c` | 2,095,492 / `a138412af0dbbd96579d3a3603f4aaa546064455582c45a56510fe9e9c270d6e` |
| `skybox.ktx` | 无此段 | 1,572,932 / `270d03d51e6da674bcd1bbb6e2992e7829f2e1f3c5f9c6710867904db1c6f3c2` |

X3 原件目录：

```text
research/online-artwork/runs/20261007T101708.195649Z
  packages/b4763c5f9f790b0859131807324bf81c4a1855489fc087e715f673de848119bf/
    resource.zip
    download.transport.bin
    inventory.json
    extracted/config.json
    extracted/res/raw/detail_model.vfxms
  model-inspection/b4763c5f9f790b0859131807324bf81c4a1855489fc087e715f673de848119bf/
    extracted/res/raw/detail_model/{scene-config.json,model.glb,ibl.ktx}
```

Free4 原件目录：

```text
research/online-artwork/runs/20261007T101352.199515Z
  packages/068676a8bb633f35e741f2d0dc7eb919afcb03e89f5c6839b41adbec53287bb1/
    resource.zip
    download.transport.bin
    inventory.json
    extracted/config.json
    extracted/res/raw/detail_model.vfxms
  model-inspection/068676a8bb633f35e741f2d0dc7eb919afcb03e89f5c6839b41adbec53287bb1/
    extracted/res/raw/detail_model/{scene-config.json,model.glb,ibl.ktx,skybox.ktx}
```

同一型号分别以欢律和无线耳机请求认证查询，两种来源给出相同包摘要，按摘要每份只下载一次。两包各仅有 `config.json` 与 `res/raw/detail_model.vfxms`，ZIP 未加密，外层配置明确指向模型容器。容器加载也不经过 AES 解密；这里不要把配置认证解密套用到 VFXMS。ZIP 成员、原容器及导出片段是可信研究输入，原件不做纹理重编码、轴向修改或通用材质批量修正。

## VFXMS / GLB / 环境光结构

官方 `ModelScene` 加载器直接读取字节。40 字节 VFXMS 头部是 10 个 big-endian 有符号 int32，依次为五段的 `(start,length)`：场景 JSON、GLB、IBL、skybox、animation。APK 按长度顺序读前四段，不按 start seek；本次两包所有非空 start 均与顺序位置相同，累计长度恰好覆盖整个文件，animation 长度为 0。导出文件与原容器独立切片逐字节相同。

GLB 均为 glTF 2，一个 JSON chunk 加一个 BIN chunk，没有外部 buffer/image URI，没有 glTF 动画。X3 有 31 个 mesh，Free4 有 28 个 mesh、67 个 node、93,875 个三角形。各 IBL 是 KTX1、六面 cubemap、256×256、五级 mip；Free4 skybox 是 KTX1、六面 cubemap、256×256、单级 mip。场景 JSON 还包含 OEM 光照、材质、缩放、抗锯齿和阴影设置，已保存原始字节。

研究检查包括头部与段边界、GLB chunk/JSON、KTX header 和面/级别边界；不是完整 glTF accessor 验证器，也没有对 KTX 做像素解码。实际桌面渲染另已由 Ava3DControl 验证，两个验收层次要区分。

Free4 原件唯一材质 `1001` 标为 `BLEND`，但其 RGB 基色纹理与因子完全不透明。Ava3D 的混合材质会关闭 depth write，导致耳机柄和内部结构覆盖外壳。当前 [EncoFree4ModelView.cs](../../src/HeyMelody.App/EncoFree4ModelView.cs) 先校验固定 GLB 长度/SHA256，再通过 SDK 公共 API 将这个**已知模型的已知材质**改为 Opaque 并启用 DepthWrite，原 GLB 字节、几何、纹理与原始正向缩放不变。不能将这项资产特定处理扩展为“所有设备所有 BLEND 都改为不透明”。

当前实现只在 App 展示层加载该模型，后端没有加载 GLB 或 GPU 资源。展示仍使用桌面现有光照，**尚未接入 OEM IBL、skybox 或完整场景配置**。用户已确认遮挡与渲染正常，左右拖动方向已修正、垂直方向保留、强阻尼惯性和空闲 20°/秒自转已验证；无线耳机 APK 默认自转也是 20°/秒。主仓库迁移的 259 项原生检查与 338 项离线检查见 [0.7.1 文档](enco-free4-0.7.1.md)。这次美术离线验收没有重复运行 GPU 检查。

## 可复用的 2D 与功能美术

control 包已确认取得这些产品，完整文件/哈希/角色应读取 `remote-assets.json` 的 origins 与各原 ZIP，不以这份名单替代成员清单：

| 品牌 | 型号与产品 ID（括号为请求色号） |
|---|---|
| OPPO | Enco Free `060410` (0)、M31 `050410` (0)、M32 `050810` (0)、Quiet `050010` (0)、W11 `061010` (1)、W31 `060810` (0)、W31 Lite `062C10` (1)、O-Free `060412` (1) |
| OnePlus | Bullets Wireless Z2 `050414` (2) |
| realme | Buds Air `3D59CF` (1)、Air 2 `BA5D56` (1)、Air 2 Neo `1B5374` (1)、Air 3 `060812` (1)、Air Neo `F85450` (1)、Air Pro `8CD10F` (1)、Q2 `693878` (1)、Wireless 2 `C1E2B1` (1)、Wireless 2 Neo `C70A6D` (1)、Wireless Pro `E3009F` (1) |

Enco W11 与 W31 Lite 三张 PNG 的字节相同，这是两次响应和两份包证明的共享，不能把相似外观作为其他型号共享的依据。部分清单内配置为 `derived_redacted`，用于去除原配置的网络地址；配置原始字节仍以保存的原 ZIP 为准，不能宣称每份脱敏展示配置都与 ZIP 成员逐字节相同。

APK 本地资源包括通用入耳、颈挂、耳夹的整体/左右耳外观，充电盒配对按键说明，佩戴、听力、查找、翻译、电量、扫描和连接引导。未找到这些通用外观可靠绑定某个产品 ID 的配置，保留 `generic`，可作为回退候选。

功能美术可重点从 [音质证据](../../research/earbud-artwork/evidence/feature-audio-quality.md) 与 [降噪证据](../../research/earbud-artwork/evidence/feature-noise-control.md) 开始：有 LHDC + Hi-Res 组合徽标、LHDC Lossless、Dolby、Dynaudio、自适应降噪、深度/轻度降噪、通透、关闭、独立聆听、空间音频及 EQ 资源。资源存在不等于当前耳机支持对应功能，型号能力仍由有效配置与运行时状态决定。

Android vector 原件是二进制 XML，PNG/SVG 仅是研究预览；主题属性、状态选择器、animated-vector 动态行为与 Lottie 播放尚未完全迁移到 Avalonia。Hi-Res 渐变依赖、default/night、密度与语言变体通过 `origins`、`dependencies`、`decoded_path` 保留。`normal` 是降噪关闭（modeType 1），`listen` 是聆听（modeType 6）；不能混为通透，`transparent_appbar` 也不是通透模式图标。LDAC 图标未确认。`assets/melody.bin` 仍是未知二进制，不称为 3D 模型。

## 后续设备美术适配需要的输入与缺口

1. **先由设备操作适配确认精确 productId 与能力来源。** 美术选择还需设备真实 color 协议值、形态/左右耳/盒状态、主题与显示角色，不能从名称或颜色外观猜 ID。当前只有成功查询色号，没有证明设备协议中色号如何读取或与营销配色如何映射。
2. **保留两种 App 来源及配置快照版本。** 已有云配置只负责能力声明；资源 endpoint 是另一层契约。未来下载器需构造正确 metadata 版本、moduleId 与准确请求 body，复用认证研究，处理空资源和失效缓存。此次没有完成所有美术模块、其他地域域名、资源包版本淘汰、离线回退和缓存更新规则的完整运行时复刻。
3. **按响应字段与 config 路径加载对应资源角色。** 先校验来源、摘要和成员，再解析配置，前端根据角色选择 PNG、动画或 GLB；不将 config 中任意相对路径当作可执行内容，不把自带资源下载地址的配置原样放进诊断日志。当前 control 资源的图片角色已能研究，尚未在 App 集成。
4. **美术加载保留在 UI 侧。** 元数据和能力研究不要求后端解析纹理、加载渲染器或上传 GPU。模型体积、纹理尺寸、节点/三角形数量、外部 URI、包路径、XML/JSON 深度及内存预算需要明确边界；现有下载脚本已限制包/成员大小与路径，但不是支持任意第三方模块的完整安全边界。
5. **分别验收各原始模型的透明度、轴向、尺寸、光照与生命周期。** Free4 特定 Opaque 修复、相机按世界 bounds 适配和交互可作为现有实现参考；OEM IBL/skybox 未接入，新资产不能只凭导出成功即判渲染正确。Free4 丹拿版官方模型已于 2026-10-08 取得，但本次未接入 App；X4 模型仍未取得，不能借用普通 Free4 宣称对应型号已适配。

## 离线验收结果、保存范围与复现

| 报告 | 结果 |
|---|---|
| [Free4 正式 run 新验收](../../artifacts/cloud-research-acceptance/artwork-20261007T101352.199515Z.json) | 6 个 case 自洽，2 个来源成功，1 个独立原 ZIP；服务端预期摘要、传输摘要/解码、ZIP CRC/成员、原提取文件与各导出段全部通过 |
| [X3 正式 run 新验收](../../artifacts/cloud-research-acceptance/artwork-20261007T101708.195649Z.json) | 8 个 case 自洽，2 个来源成功，1 个独立原 ZIP；相同完整性检查通过 |
| [完整美术图册新验收](../../artifacts/cloud-research-acceptance/earbud-artwork-validation.json) | 695 资产、730 条 APK 来源、124 条远程成员来源、19 原 ZIP、634 功能选择、76 绘制依赖、538 预览、52 接触表全部通过 |
| [验收汇总](../../artifacts/cloud-research-acceptance/artwork-summary.json) | 两份 APK 身份不变、历史报告不变、没有网络或 ADB 操作 |

输入 APK 身份为欢律 `eacb314e092cfb6c2f075eee834498412255cadf184978f3dc16400f6e1dd5d4`、无线耳机 `c1149df9e24ed85bf84f7c81c712cee794299d2b95057c6e221fb547f2717059`。模型 run 还保存 `results.json`、脱敏 `metadata.redacted.json`、下载传输原文、原 ZIP、inventory、容器解析报告与导出段。元数据 HTTP 原文当时未保留，因此无法离线重算元数据传输摘要；新的验收不能补回这项缺失。对远程包的 SHA256 校验证明保存字节符合当时记录的预期摘要，不构成独立的厂商数字签名验证。

重新离线复核可以调用原脚本的 `verify_artifact` / `verify_exports` / `negative_tests`，以及图册 `merge_and_verify.validate(..., check_gallery=True)`，把输出写入新的验收目录。此次 [reverify_artwork.py](../../artifacts/cloud-research-acceptance/reverify_artwork.py) 复用了原模型 `main()`，仅重定向最终 verification 输出；所有输入路径限制和校验代码不变，调用前后还检查历史报告摘要。此脚本用 `open("x")` 避免覆盖本轮已有报告，再次验收应另选输出目录，不直接重跑覆盖。旧 `online-artwork/README.md` 把 `inspect_models.py` 的 run 参数写成 `--run`，而实际脚本接受位置参数；它还会重写既有导出报告，阅读/验收时优先用统一验收入口而不执行该旧示例。

后续统一验收入口为 [tools/research/accept_cloud_research.py](../../tools/research/accept_cloud_research.py)，使用新的输出目录保存报告，避免覆盖本轮或历史验证；总交接见 [device-adaptation-handoff.md](device-adaptation-handoff.md)。

不要为阅读成果默认执行 online 获取或 `--merge`/重新提取操作。原在线 fetcher 默认离线，只有显式 `--online` 才请求资源；复现需要已经安装的 Python 3.11、cryptography，图册检查还需要 Pillow，脚本不会安装依赖。模型获取器依赖 `online-whitelist/scripts/fetch_whitelist.py`，后者又依赖 `earbud-artwork/scripts/probe_resources.py` 与 `android_resources.py`；保存研究时需要连同辅助脚本及两份输入 APK 保留，不只复制 GLB。

原验收没有发现正式模型、主清单原件、派生预览或报告失效路径；这是对当时本地保存产物的结论，未检查当时 CDN 链接现在是否仍有效。原 `research/` 产物现按专题归档于 `docs/research/`；其中原始输入、完整厂商资源、生成图册与运行报告，以及 `artifacts/` 证据仍仅在本地保留，具体提交边界见 [资料归档入口](README.md)。迁移电脑或清理忽略目录前应同时保全原件、脚本、清单、报告和依赖入口，不能仅凭本文恢复未提交的二进制资源。

**资料归档（2026-10-08）：** 本文访问链接与复现路径已按新目录更新；研究时的原始位置为仓库根 `research/`，原报告与 manifest 内路径保留不变。新旧映射及提交边界见 [资料归档入口](README.md)。
