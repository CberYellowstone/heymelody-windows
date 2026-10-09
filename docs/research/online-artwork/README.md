# 官方云端模型与动态外观研究摘要

2026-10-07 至 2026-10-09 已取得并复核 X3、普通 Free4、Free4 丹拿版的官方三维模型，以及 X4 的官方动态 WebP。完整请求条件、空响应、原包、格式解析和运行记录保存在 [research/online-artwork/](../../../research/online-artwork/)；资源获取阶段原文见 [归档报告](../../../research/online-artwork/archive-2026-10-09/README.md)。

## 已核实的资源

| 型号 | 精确产品 ID | 成功请求的色号 | 模块 | 已取得内容 | 对应报告 |
| --- | --- | ---: | ---: | --- | --- |
| Enco X3 | `067410` | 2 | 14 | VFXMS 原容器、GLB、IBL 与场景配置 | [X3 模型验证](../enco-x3-3d-validation.md) |
| Enco Free4 | `068C10` | 1 | 14 | VFXMS、GLB、IBL、skybox 与场景配置 | [Free4 模型与实现](../enco-free4-0.7.1.md) |
| Enco Free4 丹拿版 | `06C010` | 3 | 14 | 独立官方模型包与导出模型 | [丹拿版模型研究](../enco-free4-dynaudio-model.md) |
| Enco X4 | `06EC10` | 1 | 14 | 官方 `modelWebp` 动态资源 | [X4 美术复核](../enco-x4-artwork-audit.md) |

每个表中成功条件都分别查询欢律与无线耳机，两来源返回的对应原包摘要相同。色号是请求协议值；此次样本不能将它自动解释成商品颜色名称或官方默认色。普通 Free4 和丹拿版使用不同产品 ID，不能互相替代。

X4 的主展示资源是动态 WebP，control 包中的 Lottie 是操作教程；二者用途不同。`support3DModel` 不是取得 WebP 的必要条件。主展示应依据实际资源角色与可加载格式选择三维模型、WebP 或静态图，不能把教程动画当作型号外观。

## 格式与来源结论

取得的 X3、普通 Free4 `.vfxms` 不是加密模型包，而是头部加场景 JSON、GLB、IBL KTX 和可选 skybox KTX 的容器。导出直接复制各段原字节，没有重编码模型。固定样本中 GLB 都是版本 2，无外部 buffer/image URI，也没有内置 glTF animation；X3 有 31 个 mesh，普通 Free4 有 28 个。

原件复核覆盖元数据摘要、ZIP 成员和提取字节、容器边界、GLB chunk/JSON，以及 KTX 全部面和 mip 边界。获取阶段的结构检查没有运行模型引擎或完整渲染验收；后续原生显示、材质处理和帧验证由表中专题另行记录。当前应用已接入官方模型与动态外观，具体持久化和显示行为见 [外观资源持久化](../../artwork-persistence.md) 与 [模型渲染设置](../../model-rendering.md)。

资源查询版本取自 application meta-data，旧外观探测器使用 manifest 版本的观察保留为历史，不能替代本次有效条件。模块响应没有独立产品 ID 字段，型号归属由精确请求、唯一响应资源、文件名与包摘要建立，不能称为响应产品 ID 的额外校验。

## 未证明范围与完整复核

2026-10-07 X3 只在已查询的色号 2 下取得包，普通 Free4 在色号 1 下取得包；丹拿版当日的 -1/0/1/2 查询没有资源，次日色号 3 已成功。因此 `HTTP 200 / code 0` 的空资源响应只说明该次条件未提供包，不能否定型号资源存在，也不能推广为所有颜色或地区的结论。

原元数据完整 HTTP 字节未保留，无法重新计算其传输摘要。原包摘要与本地结构完整性也不证明其他型号控制能力、官方渲染的完全等效或真实双耳声音效果。

固定原件复核入口：[X3](../../../research/online-artwork/runs/20261007T101708.195649Z/verification.json)、[普通 Free4](../../../research/online-artwork/runs/20261007T101352.199515Z/verification.json)、[丹拿版](../../../research/online-artwork/runs/20261008T051703.741040Z/verification.json)。X4 原包及帧复核入口见其专题报告；容器静态依据见 [model-format-contract.json](../../../research/online-artwork/evidence/model-format-contract.json)。

复现脚本位于 [tools/research/online-artwork/scripts/](../../../tools/research/online-artwork/scripts/)。已有样本可通过 [accept_cloud_research.py](../../../tools/research/accept_cloud_research.py) 统一离线复核，新报告另存 `research/cloud-acceptance/`。重新获取、导出或原生预览的详细命令与环境记录保留在完整研究目录中。

这些 `research/` 链接指向本地保存的完整材料；普通仓库克隆不包含厂商 ZIP、模型、纹理或全部验证记录。
