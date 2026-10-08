# Enco X4 产品展示与操作引导资源复核

2026-10-09（香港时间；在线证据时间为 2026-10-08 22:03 UTC），从保存的两份官方 APK 复核资源调用链，并向官方接口查询精确产品 `06EC10` 的 `color=0/1`。**X4 水平旋转的产品展示资源是 Animated WebP，位于模块 14 的 `modelWebp` 字段；control 包的 Lottie 是按捏、滑动等操作引导。** 不能把同型号任意 Lottie 作为主页面产品展示，也不能将模块 14 一律视为 GLB/3D 模型。

这些研究结果用于 0.8.4 的产品动画与角色选择修正；该版本也新增默认关闭的 [三维模型 GPU 加速开关](../model-rendering.md)。本文记录资源与调用依据，源码检查和发布包的验证范围分别见 [外观资源持久化](../artwork-persistence.md) 与 [构建说明](../../REPOSITORY.md)。

本次研究没有执行 APK、访问手机、修改耳机、扩大型号/模块枚举，也没有覆盖此前研究原件或 `latest-run.json`。本次使用用户提供的 `http://127.0.0.1:59991` 代理；8 次元数据 POST、2 次按摘要去重的 CDN GET 均串行且间隔至少一秒。

## 原件位置与请求范围

证据根目录，以下简称 `RUN`：

```text
research/online-artwork/x4-artwork-audit/20261008T220302.957674Z/
```

`results.json` 保存每次请求字段、时间、HTTP 状态/传输摘要、脱敏响应和下载关联。认证材料从校验过的 APK 证书绑定数据在内存中恢复，实际请求采用已验证的 HMAC-SHA1；没有保存认证材料、请求认证头或完整 CDN 地址。元数据 HTTP 原文没有保存，故记录的传输摘要不能单凭脱敏 JSON 独立重算。

| 来源 | 原始输入 | APK SHA256 | 请求 `channel` / `versionCode` |
| --- | --- | --- | --- |
| 欢律 17.6.5 | `research/apps/欢律.apk` | `eacb314e092cfb6c2f075eee834498412255cadf184978f3dc16400f6e1dd5d4` | `2` / `17006004` |
| 无线耳机 17.6.3 | `research/apps/无线耳机_17.6.3.apk` | `c1149df9e24ed85bf84f7c81c712cee794299d2b95057c6e221fb547f2717059` | `1` / `17006002` |

请求版本仍来自 application meta-data，不能替换为 manifest 安装版本。两来源分别查询 `v1/earphone/resource/control` 与 `v1/earphone/resource/fetch`（后者附 `moduleId="14"`），仅使用 `productId="06EC10"`，全部请求字段为字符串。

| 请求色号 | 两来源 control | 两来源 fetch14 |
| --- | --- | --- |
| `0` | HTTP 200 / code 0，空 data | HTTP 200 / code 0，空 data |
| `1` | 各返回同一个已校验 ZIP | 各返回同一个已校验 ZIP |

`1` 是本次成功的协议色号；此证据没有证明默认颜色、营销颜色名称或其他色号是否提供资源。control 响应明确含 `productId=06EC10`，归属为 `request_and_response_productId`；fetch14 响应文件名为 `android_all_06EC10_1_14.zip`，但无独立 `productId`，归属仍为 `request_only`。

## 产品旋转原件与正确解析

模型资源目录：

```text
RUN/packages/1bb49ddc6d0ffe4f1ab9e6282bc72962d15629bd5ef0dc7480ea4903d51ba6e0/
  resource.zip
  download.transport.bin
  inventory.json
  extracted/config.json
  extracted/res/raw/detail_model.webp
```

原 ZIP 为 **6,511,256 字节**，SHA256 即上述目录名。该包只有两个文件，不含 Lottie JSON、VFXMS 或 GLB。虽然服务端资源描述仍包含“3D模型”，包内契约明确是：

```json
{"modelWebp":{"path":"res/raw/detail_model.webp"}}
```

`config.json` 是 68 字节，SHA256 为 `2808ed084cf1f8f36bad731deb3729ebbeb85440045f158e3d5d025de27c6f2d`。`detail_model.webp` 是 **6,508,910 字节**，SHA256 为 `269c63cccf729ea819a38cc010f6c7f6ed86fefa00bc680e84f1e4863f3d4736`。

独立 RIFF 检查和 Pillow 解码一致确认：画布 **780 × 780**、**180 帧**，每帧 **33 ms**，整轮 **5,940 ms**；`ANIM` 中循环次数为 **0（无限循环）**。第 0、45、90、135、179 帧的 RGBA 摘要全部不同。查看独立预览，第 0 帧是开盖充电盒和双耳的正面，第 90 帧是背面，确实是围绕 Y 轴水平旋转的产品展示。原件有透明区域；不能用单张普通 Bitmap 解码后期望它自行播放。

`ANMF` 不是每帧整画布：第 0 帧矩形为 `(114,152,552,484)`、flags=2；第 1 帧为 `(112,152,554,484)`、flags=3。解码器必须按容器处理帧矩形、混合与前一帧释放，再按每帧时长播放。循环回首帧也需要恢复画布状态，不能把前一轮的像素继续叠加。

完整 chunk 边界、时长、帧矩形、透明 bounds、RGBA 摘要和独立 PNG 位于 `RUN/inspection/report.json` 与 `RUN/inspection/model-webp/frame-*.png`。

## control 包及 Lottie 角色

control 原件目录：

```text
RUN/packages/c3f1b327c3ad31d57e47250dad3ccf9e1fc25f3cba0dd170df8388e03f3f5411/
  resource.zip
  inventory.json
  extracted/config.json
  extracted/res/image/img_detail.png
  extracted/res/raw/control_guide_*_left.json
  extracted/res/raw/control_guide_*_right.json
```

原 ZIP 为 **2,181,004 字节**，SHA256 即目录名，98 个文件。`config.json` 为 3,672 字节，SHA256 为 `d7c1f2ed7fce9f206e30c5d12792c5c5df538fba1cd64625a146f83373f52d8b`。

该配置的 `leftLottieResList`、`rightLottieResList` 分别列出单捏、双捏、三捏、上滑、长捏，并带 `type=16/17/18/5/20` 与 `left="0%"`、`right="100%"`。`controlPages[].animation` 引用相同引导及下滑，用于暂停/恢复音乐、切歌、音量和降噪教程。配置没有顶层主 `lottieResList`；包内实际有 **12 个捏/滑教程 Lottie 和 1 个按压提示 Lottie**，13 个文件的 `markers` 都是空数组。

`detailImageRes` 指向静态 `res/image/img_detail.png`，780 × 780、363,587 字节，SHA256 为 `a7d2542fc246bca562b7c392ce2aabdcc7057dde0d9ba41a9697b523022c7ee4`。`capsuleVideoRes` 指向另一个 `res/video/capsule.webp`，属于胶囊/发现页面角色；不能因扩展名都是 WebP 就替代 `modelWebp` 产品展示。

现有预装索引中的 X4 也没有旋转 Lottie。`src/HeyMelody.Resources/Bundles/artwork.json` 中两来源均只有 `detail` 和 `capsule` PNG；预装 `detail` SHA256 为 `4fcaa983fc6f02586b59a5febf5b45809cd6ebf6e00e51e04d299b66ad38a905`（644,604 字节），与本次远程 detail PNG 分开保存。由静态预装 PNG 无法恢复模块 14 的旋转帧。

## 官方调用链与资格边界

保留的 DEX 证据为 `RUN/apk-appearance-callchains.json`、`apk-model-view.json`、`apk-model-view-listeners.json`、`apk-artwork-selection.json`。每份包含 APK/DEX SHA256、方法名、绝对 DEX 字节偏移和原指令单元；原 APK 只做静态读取。

| 契约 | 无线耳机（classes2.dex） | 欢律（classes3.dex） |
| --- | --- | --- |
| `modelWebp` → 展示路径 | `LF8/d;.invoke`，getModelWebp `0x13790a`，setWebpFilePath `0x13793e` | `Loa/c;.invoke`，getModelWebp `0x4184e8`，setWebpFilePath `0x41851c` |
| 主页面展示选择 | `MelodyDetailModelView.d`，code `0x29cfdc` | 同类 `.d`，code `0x3575dc` |
| WebP 使用 Glide 加载 File 到 ImageView | `setWebpSource`，code `0x29d32c` | `setWebpSource`，code `0x357928` |
| control 主 Lottie 的消费者 | `LA9/f;.onChanged`，getLottieRes `0xed8bc` | `LAb/w;.c`，getLottieRes `0x118a04` |

主页面先检查 GPU/3D 支持及模型文件，再检查 WebP，最后检查静态图片。3D 不支持时，无线耳机 `if-eqz@0x29d064` 跳到 `getWebpFilePath@0x29d0ae`，欢律 `if-eqz@0x357664` 跳到 `getWebpFilePath@0x3576ae`。WebP 分支使用 Glide；加载成功后取消并隐藏同视图里的 `LottieAnimationView`。不能因这个组合视图持有 Lottie 控件而把 WebP 当成 Lottie。

control 的主列表 getter 实名是 `getLottieRes`；其消费者继续读取 `Function.getControl`、`Control.getButton/getAction`，进入 `EarControlViewModel`。无线耳机同方法在 `0xeda90` 明确标记 `EarControlFragment` 并按左右列表选择。因此，**顶层主列表和左右列表均不是主页面产品展示的通用优先候选**。只有明确的资源角色/播放契约才可决定 hero；目录顺序、文件名字相似或“包含动画”均不足以决定。

本次 X4 原件没有可用于切换产品展示的 Lottie marker。没有证据支持“所有 Lottie 按 markers 任意切成 intro/loop/outro”；需要播放某个操作引导时，应先按动作角色选择对应文件和配置，不能用 marker 推断文件用途。

两来源预装配置及截至 10/8 保存的有效云配置都声明 `customDress=1`，但未声明 `support3DModel`。官方 `MelodyDetailModelView.c()` 在 3D 支持判断中读取该字段、ABI/OS/GPU 能力；其失败仍落入 WebP 分支。本次 fetch14 实包也证明该字段不能单独作为“模块 14 不应查询”的依据。桌面采用 `support3DModel` 或 `customDress` 为有限在线查询资格，是结合已取得原件的实现选择，**不声称完整复刻了 OEM 的全部下载资格规则**；已验证缓存的可用性不应由单一当前配置 flag 抹掉。

## 离线回归样本与复现

真实小样本 `RUN/inspection/model-webp/fixtures/x4-first-two-frames.webp` 为 **82,530 字节**，SHA256 为 `73d654f444dea3be6f13f96b5477652f7707b3e8fea36ab0da695d5f9b1e02ae`。它保留原件 `VP8X`、`ANIM`、前两个 `ANMF` chunk 全部原字节，仅重新计算外层 RIFF 长度，不做帧/纹理重编码；780 × 780、两帧各 33 ms。旁边的 `frame-000.png`、`frame-001.png` 是 Pillow 独立解码参考，供另一套解码器验证透明和部分画布合成。该截取片段是测试派生物，不是官方完整产品动画。

`RUN/inspection/synthetic-fixtures/two-frame.webp` 为独立生成的透明 32 × 32 Lossless WebP，140 字节，SHA256 为 `859cb79880a2e401fc275c91c669814ba68c49c34e33f1abb0fccd70d4f453f3`；两帧 100/300 ms、loop=0。第二帧仅改动第一帧红方块内的一小块青色，适合检查不同时长、保留像素和循环。它不含厂商外观，不能作为取得官方资产的证据。

复现脚本：[audit_x4_artwork.py](online-artwork/scripts/audit_x4_artwork.py) 默认离线，只在显式 `--online` 下执行上述固定 8 次请求；[inspect_x4_artwork.py](online-artwork/scripts/inspect_x4_artwork.py) 仅离线校验/解码，拒绝覆盖已有输出。

```powershell
python -B -X utf8 docs/research/online-artwork/scripts/audit_x4_artwork.py --online --proxy http://127.0.0.1:59991
python -B -X utf8 docs/research/online-artwork/scripts/inspect_x4_artwork.py research/online-artwork/x4-artwork-audit/20261008T220302.957674Z --output research/online-artwork/x4-artwork-reverification-new
```

第二条路径需不存在，重复验证应换新目录。脚本使用已安装的 Python 3.11、cryptography、Pillow，不安装依赖。此机 Python 入口可用 `uv python find --offline` 查询。原始 ZIP、完整厂商 WebP/PNG/Lottie、DEX 报告和解码图都保存在被忽略的 `research/` 中；提交时保留本文、复现脚本与必要小样本，不能仅凭本文恢复未提交的完整厂商原件。
