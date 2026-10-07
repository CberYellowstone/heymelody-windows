# Enco X3 / Enco Free4 云端 3D 资源获取

本页保留资源获取阶段记录。后续已完成 X3 渲染验证，并将 Free4 模型迁入 0.7.1；当前范围见 [美术交接](../../docs/research/cloud-device-artwork.md) 和 [Free4 实现](../../docs/research/enco-free4-0.7.1.md)。对已保存样本优先运行 `python -B -X utf8 tools/research/accept_cloud_research.py`，它只离线复验并另存报告。

2026-10-07 已实际取得 **Enco X3 与 Enco Free4 的官方 3D 资源包**。每个型号分别通过欢律和无线耳机的请求认证查询，两种来源返回的包 SHA256 相同；每份包只下载一次。研究使用现有 Python 环境和 `http://127.0.0.1:59991` 代理，不需要 AVD、root 或手机辅助。全部产物留在本目录，没有接入桌面应用或实现渲染。

## 已取得资源

| 型号 | 产品 ID | 本次有效配色编号 | ZIP 字节数 | VFXMS 字节数 | 导出 GLB 字节数 |
|---|---|---:|---:|---:|---:|
| Enco X3 | `067410` | 2 | 9581441 | 12932064 | 10835448 |
| Enco Free4 | `068C10` | 1 | 8885630 | 13397063 | 9727500 |

编号是请求中的协议值，两款白名单均未声明 `defaultColor`。本轮没有把这些编号认定为官方默认色或对应的商品颜色。

直接查看导出模型与原包：

- X3：[model.glb](runs/20261007T101708.195649Z/model-inspection/b4763c5f9f790b0859131807324bf81c4a1855489fc087e715f673de848119bf/extracted/res/raw/detail_model/model.glb)、[原 ZIP](runs/20261007T101708.195649Z/packages/b4763c5f9f790b0859131807324bf81c4a1855489fc087e715f673de848119bf/resource.zip)、[结构报告](runs/20261007T101708.195649Z/model-inspection/report.json)。
- Free4：[model.glb](runs/20261007T101352.199515Z/model-inspection/068676a8bb633f35e741f2d0dc7eb919afcb03e89f5c6839b41adbec53287bb1/extracted/res/raw/detail_model/model.glb)、[原 ZIP](runs/20261007T101352.199515Z/packages/068676a8bb633f35e741f2d0dc7eb919afcb03e89f5c6839b41adbec53287bb1/resource.zip)、[结构报告](runs/20261007T101352.199515Z/model-inspection/report.json)。

原 ZIP 的 SHA256 与元数据完全一致：

- X3：`b4763c5f9f790b0859131807324bf81c4a1855489fc087e715f673de848119bf`
- Free4：`068676a8bb633f35e741f2d0dc7eb919afcb03e89f5c6839b41adbec53287bb1`

两包都只有两个文件：`config.json` 和 `res/raw/detail_model.vfxms`。外层配置明确指向该模型文件。原件、ZIP 成员清单、逐文件摘要与导出结果均保留；没有重编码或转换模型。

## 请求契约与查询范围

`POST https://iot-earbuds-cn.heytapmobi.com/v1/earphone/resource/fetch`。

欢律查询 X3 的成功请求体：

```json
{"platform":"android","channel":"2","versionCode":"17006004","productId":"067410","color":"2","moduleId":"14"}
```

无线耳机使用 `channel="1"`、`versionCode="17006002"`；Free4 改为 `productId="068C10"`、`color="1"`。所有值均为字符串。认证沿用已验证的 APK 内认证材料解密与准确 body 字节的 HMAC-SHA1 请求签名。认证材料、签名、nonce、请求认证头及完整下载 URL 不写盘。

版本来源是 **application meta-data.versionCode**。旧 `earbud-artwork` 资源探测器用了 manifest 安装版本号，此处已纠正；旧观察仅作为历史线索，不作为本轮结果。

本轮共 20 次元数据 POST、2 次 CDN GET，全部串行、所有网络请求间隔至少一秒；HTTP 200、业务码 0。具体条件如下：

| 型号 | 两来源查询的配色编号 | 有资源的编号 |
|---|---|---|
| X3 `067410` | -1、0、1、2 | 2 |
| Free4 `068C10` | 0、1 | 1 |
| Free4 丹拿版 `06C010` | -1、0、1、2 | 本次均未返回资源 |

其他条件返回成功但没有 `data`，只能说明本次查询没有提供包，不能推断该型号没有模型。此后没有继续扫描配色、其他产品或模块。

成功元数据含 `fileName/resourceDescription/resourceName/resourceUrl/sha256`，没有独立 `productId` 字段。型号归属记录为 **request-only**：依据精确产品 ID 和配色请求、唯一响应资源、带对应编号的文件名，以及经校验的包 SHA256；没有声称存在响应产品 ID 的独立校验。

## VFXMS 格式及实际验证

APK 的 `ModelScene` 加载器直接读取文件字节，不经过 AES 解密。实际取得的两份 `.vfxms` 是可解析容器，结构为：

```text
40 字节头部（10 个 big-endian int32）
configStart, configLength, glbStart, glbLength,
iblStart, iblLength, skyboxStart, skyboxLength,
animationStart, animationLength

UTF-8 场景 JSON → GLB → IBL KTX → 可选 skybox KTX
```

官方加载器按长度顺序读取前四段，没有用 start 字段 seek。本轮样本的所有非空段 start 都与顺序位置一致，段长度覆盖整个文件且没有剩余尾部；animation 段均为空。导出时复制原始段字节。

- 两个 GLB 均为版本 2、一个 JSON chunk 加一个 BIN chunk，声明总长与实际一致。X3 有 31 个 mesh，Free4 有 28 个；两者均没有外部 buffer/image URI，也没有内置 glTF animation。
- 两个 IBL 均为 KTX1 六面 cubemap，256×256、五级 mipmap；Free4 另有一个 256×256、单级 mipmap 的 skybox KTX1，X3 没有 skybox 段。
- 场景 JSON 包含光照、材质、缩放、抗锯齿及阴影等参数；已保存原容器与场景配置原始段。

检查覆盖容器边界、GLB chunk/JSON、KTX header 和所有面/级别边界；没有运行像素解码器、完整 glTF accessor 验证器、模型加载引擎或渲染器，因此这不是渲染效果验收。

静态依据见 [model-format-contract.json](evidence/model-format-contract.json)；两来源目标能力与资源字段见 [target-profiles.json](evidence/target-profiles.json)。

## 文件及复现

- [配色 1 请求与 Free4 下载记录](runs/20261007T101352.199515Z/results.json)
- [配色 0 空响应记录](runs/20261007T101533.954104Z/results.json)
- [配色 2/-1 请求与 X3 下载记录](runs/20261007T101708.195649Z/results.json)
- 独立原件复核：[Free4](runs/20261007T101352.199515Z/verification.json)、[X3](runs/20261007T101708.195649Z/verification.json)。

两份独立原件复核均通过，包括服务端摘要、ZIP 实际成员字节、提取原件、请求与资源文件名映射及模型容器结构。空响应的检查只证明记录自洽，不表示该查询取得了模型。

获取器正常验证 TLS、限制为厂商 HTTPS CDN、禁止重定向，校验元数据 SHA256；同时保留传输原文与 HTTP 解码后 ZIP 的摘要。ZIP 全成员先检查 Windows 路径、大小、类型及 CRC，再提取；不执行包内内容。元数据响应保留脱敏版本，没有保留完整 HTTP 原文，无法离线重算元数据传输摘要。

在仓库根目录复现成功条件（会发起新请求）：

```powershell
$artworkPython = 'python'
& $artworkPython -B -X utf8 research/online-artwork/scripts/fetch_artwork.py --online --targets x3 --colors 2 --proxy http://127.0.0.1:59991
& $artworkPython -B -X utf8 research/online-artwork/scripts/fetch_artwork.py --online --targets free4 --colors 1 --proxy http://127.0.0.1:59991
```

默认查询两种 App 来源；`--packages` 可只选一种。获取器默认离线，`--offline` 仅复核 APK 与认证材料，不访问网络。在线退出码 0 代表流程完成，应查看各 case 的 `status` 和 `unique_verified_packages`；空资源响应会明确记录，不等于已下载模型。

仅解析、导出与复核已有样本，不联网：

```powershell
$artworkRun = 'research/online-artwork/runs/20261007T101708.195649Z'
& $artworkPython -B -X utf8 research/online-artwork/scripts/inspect_models.py $artworkRun
& $artworkPython -B -X utf8 research/online-artwork/scripts/verify_artwork.py --run $artworkRun --output artifacts/artwork-review-new/verification.json
```

两个模型目录中的 `model.glb`、`ibl.ktx`、可选 `skybox.ktx` 和场景配置可供下一阶段研究使用；本阶段没有调整 Avalonia 界面或添加 3D 渲染能力。
