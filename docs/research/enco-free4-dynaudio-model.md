# Enco Free4 丹拿版官方模型获取与验证

> 当前客户端仅使用欢律配置与资源，见[当前单源规则](heymelody-only.md)。下文涉及已退出支持来源的覆盖、接口和操作要求仅用于历史研究复核，已由当前规则取代；原版本、路径、摘要和验证范围保留。

本文是2026-10-08模型研究的公开整理稿。完整原文保存在[本地研究档案](../../research/migrations/organization-2026-10-09/originals/docs/research/enco-free4-dynaudio-model.md)，研究对象、工具、理解和产物保存在`research/online-artwork/`。下文的`research/`原件、日志和渲染图链接仅供本地档案使用，不随源码分发；克隆仓库包含公开文档和`tools/`工具，不要求拥有这些原件。

**后续接入：0.8.2 的临时 Demo 已改用此模型。** 原始 GLB 独立嵌入 `src/HeyMelody.App/Assets/Devices/EncoFree4Dynaudio/model.glb`，长按主界面“+”两秒或 `Start-3D-Demo.cmd` 可检查布局；精确摘要限定内存不透明/深度写入设置。下文保留获取阶段的研究范围，来源及原件不变。

2026-10-08 已取得 **Enco Free4 丹拿版 `06C010` 的官方 3D 资源**。有效条件为 `color="3"`、`moduleId="14"`；欢律与无线耳机两种来源返回同一 ZIP。此前仅查询 `-1、0、1、2`，遗漏了 3，空响应不能作为模型不存在的依据。本次仅做资源研究与验证，没有修改应用实现，也不需要 root AVD 或手机配合。

## 原件与复现

可直接使用的研究原件整理在 [enco-free4-dynaudio](../../research/online-artwork/enco-free4-dynaudio/manifest.json)：

- [原始官方 ZIP](../../research/online-artwork/enco-free4-dynaudio/android_all_06C010_3_14.zip)：8,820,468 字节，SHA256 `efe7c68b71fe92a3b6c8e5901603cd889ac3e5f8737e612e3e42f3f6054b1a99`。
- [原始 VFXMS](../../research/online-artwork/enco-free4-dynaudio/detail_model.vfxms)：13,533,055 字节，SHA256 `7c71cd7b50d537e4c3ccb6552b728e17ae336d49ed4e018e5c9f1fb049737175`。
- [无损导出的 GLB](../../research/online-artwork/enco-free4-dynaudio/model.glb)：9,863,456 字节，SHA256 `7f85c66d397ee45163246ff745c37e52a70059e739be0af2283a30bfb689c2f6`。
- 外层 `config.json`、场景 `scene-config.json`、`ibl.ktx` 和 `skybox.ktx` 也保留原始字节；逐文件大小、摘要和来源见 manifest。

成功请求为 `POST https://iot-earbuds-cn.heytapmobi.com/v1/earphone/resource/fetch`，欢律请求体：

```json
{"platform":"android","channel":"2","versionCode":"17006004","productId":"06C010","color":"3","moduleId":"14"}
```

无线耳机改用 `channel="1"`、`versionCode="17006002"`。版本仍来自 APK application meta-data，认证与签名仍使用各 APK 已验证的契约。没有改变版本、产品 ID、模块或请求端点；认证材料、签名和完整 CDN URL 未写盘。

本次新增 2 次模型元数据 POST、1 次 CDN GET，全部串行并沿用现有请求间隔。下载前要求唯一资源记录，下载后校验服务端 SHA256，再验证 ZIP 路径、成员大小和 CRC。两来源记录见 [获取 run](../../research/online-artwork/runs/20261008T051703.741040Z/results.json)，独立离线复核见 [verification.json](../../research/online-artwork/runs/20261008T051703.741040Z/verification.json)。

```powershell
python -B -X utf8 tools/research/online-artwork/scripts/fetch_artwork.py --online --targets free4-dyna --colors 3
python -B -X utf8 tools/research/online-artwork/scripts/inspect_models.py research/online-artwork/runs/20261008T051703.741040Z
python -B -X utf8 tools/research/online-artwork/scripts/verify_artwork.py --run research/online-artwork/runs/20261008T051703.741040Z --output research/cloud-acceptance/free4-dynaudio-review/verification.json
```

复现需要已安装依赖的Python环境和相应研究输入。第一条明确访问网络并创建新run；后两条离线读取已保存原件并生成分析结果。验证报告指定尚不存在的输出文件，原始下载字节保留不变；普通构建和应用运行不依赖这份本地研究档案。

## 身份与结构证据

请求使用云端白名单中的精确丹拿版 ID `06C010`，响应文件名为 `android_all_06C010_3_14.zip`，资源名称和描述同样包含该 ID、色号和模块。两来源返回相同元数据及包摘要。但当前资源响应没有独立 `productId` 字段，归属仍标记为 `request_only`，没有虚构第二个身份校验字段。

包内仅含 `config.json` 与 `res/raw/detail_model.vfxms`。外层配置明确指向 VFXMS；容器使用 40 字节 big-endian 头，场景 JSON、GLB、IBL 与 skybox 的声明长度/位置与 APK 顺序加载契约一致，没有动画段或剩余尾部。GLB 为 2.0、一个 JSON chunk 与一个 BIN chunk，无外部 buffer/image URI。[结构报告](../../research/online-artwork/runs/20261008T051703.741040Z/model-inspection/report.json) 保存全部段及 KTX 面/mip 边界检查。

| 项目 | 普通 Free4，`068C10 / 1` | 丹拿版，`06C010 / 3` |
|---|---:|---:|
| ZIP 字节数 | 8,885,630 | 8,820,468 |
| VFXMS 字节数 | 13,397,063 | 13,533,055 |
| GLB 字节数 | 9,727,500 | 9,863,456 |
| mesh 数 | 28 | 20 |
| node 数 | 67 | 22 |

两款 ZIP、VFXMS 和 GLB 摘要不同，丹拿版原件不是此前普通版文件的重命名。IBL、skybox 和外层通用配置字节相同，只能证明这些具体文件共用；不能推导整个模型或设备操作共用。

独立审计还直接解码了原 GLB 内嵌纹理：[原始 base-color PNG](../../research/online-artwork/dynaudio-independent-audit/dynaudio-image-0.png) 可见 **DYNAUDIO** 标识，普通版同位置没有此标识。这为精确请求/资源文件名之外提供了包内可见的丹拿外观证据。[独立审计报告](../../research/online-artwork/dynaudio-independent-audit/report.json) 复核全部 100 个 accessor 的边界和有限数值、primitive 属性数量与索引范围，得到 **94,927 个三角形**，完整层级变换后的 bounds 也有效。

原 GLB 的唯一材质 `1001` 声明 `BLEND`，4 张 2048×2048 PNG 均为 RGB、解码后有效 alpha 全为 255，base-color factor alpha 缺省为 1；未发现负世界变换行列式。后续若用当前 Ava3D 展示，可在独立验证后对该精确 GLB SHA256 使用内存 `Opaque / DepthWrite=true` 设置，避免透明绘制关闭深度写入导致内部零件覆盖外壳；本次原模型字节始终保留不变。

`color=3` 已证明是服务器提供丹拿版模型的请求值，尚未由丹拿实机回包证明默认色或协议到营销色名的映射。场景内部名称是 `Kepler_Silver_01a`；[OPPO 官方商城](https://store.oppo.com/cn/app/product/index?skuId=33453)展示丹拿版“星瀚银”，两者提供外观核对线索，不能仅凭名称相似断言实机颜色编号。

同日还重新获取了两来源云端白名单，摘要与 2026-10-07 保存值完全相同：[新配置 run](../../research/online-whitelist/runs/20261008T051557.800877Z/results.json)。因此本次成功不需要白名单更新；直接补查此前遗漏的色号即可取得包。

两 APK 请求代码仍使用模块 14，颜色整数直接转为请求字符串，没有限定只能使用 0–2。`-1` 才进入默认色解析；非 `-1` 值（包括 3）透传。请求及模型路径调用证据见 [DEX 引用](../../research/online-artwork/evidence/dyna-request-refs-20261008.json)、[调用链](../../research/online-artwork/evidence/dyna-request-chain-20261008.json)。[新目标汇总](../../research/online-artwork/evidence/target-profiles-20261008.json) 从实际已验证请求动态得到丹拿成功色号 `[3]`；旧 `target-profiles.json` 保留为历史原件，研究脚本原先固定空丹拿结论也已改为读取记录。

颜色读取的独立静态审计见 [身份/颜色说明](online-artwork/free4-identity-color-audit.md) 与 [DEX 证据](../../research/online-artwork/evidence/free4-identity-color-audit.json)。SPP GET `0x010B` 的 ACK `0x810B` 将 payload 的颜色字节以 `& 0xFF` 读为 unsigned 值；OEM BLE 广告的 `flags & 0x0020` 颜色字段也原样读取。两路径没有 0–2 限制或丹拿到普通版的重映射。OEM 同时硬编码两款 PID 的名单实际用于诊断序列号裁剪，不是模型共享映射。

## Windows 原生渲染验证

隔离 [研究预览工具](../../research/online-artwork/native-preview/README.md) 只引用 Avalonia 和 Ava3D，不引用或修改 App。工具在真实 Win32 HWND / NVIDIA RTX 2060 OpenGL 下加载本次 GLB，分别拍摄正面、侧面、背面，原始 BLEND 与研究用内存 Opaque 各一组，共 6 张窗口图及 6 张 840×600 的渲染输出。

[原生收据](../../research/online-artwork/dynaudio-native-preview-20261008/native-receipts.json) 记录 `success=true`、`loaderError=null`、无渲染错误；每次均为 **20 SceneItems、20 DrawCalls、94,927 Triangles**，窗口自动正常关闭，前后模型 SHA256 相同。研究工具构建为零警告、零错误。

| 角度 | 原始 BLEND | 研究用内存 Opaque |
|---|---|---|
| 正面 | [渲染图](../../research/online-artwork/dynaudio-native-preview-20261008/front-original-blend-3d.png) | [渲染图](../../research/online-artwork/dynaudio-native-preview-20261008/front-research-opaque-3d.png) |
| 侧面 | [渲染图](../../research/online-artwork/dynaudio-native-preview-20261008/side-original-blend-3d.png) | [渲染图](../../research/online-artwork/dynaudio-native-preview-20261008/side-research-opaque-3d.png) |
| 背面 | [渲染图](../../research/online-artwork/dynaudio-native-preview-20261008/rear-original-blend-3d.png) | [渲染图](../../research/online-artwork/dynaudio-native-preview-20261008/rear-research-opaque-3d.png) |

目检确认内存 Opaque 版本外壳遮挡正常、三个角度模型完整且未裁切。原始 BLEND 正面可见耳机柄和内部零件覆盖外壳，和该材质关闭深度写入一致；alpha 证据支持对这个精确模型采用 Opaque。研究场景使用 Ava3D studio 光照，未加载 OEM KTX，因此验证的是当前引擎可加载和绘制原件，不是复刻手机端光照效果或完成 App 接入。

**资料整理（2026-10-09）：** 完整原稿已按字节归档，原报告与manifest中的历史路径保留。本页保留型号、厂商资源摘要、结构及渲染结论，复现工具入口更新为`tools/research/online-artwork/scripts/`；完整研究材料与公开整理稿的范围见[资料索引](README.md)。
