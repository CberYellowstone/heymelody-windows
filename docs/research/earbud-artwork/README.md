# 耳机外观与功能美术研究摘要

2026-10-06 至 2026-10-07 对欢律 17.6.5、无线耳机 17.6.3 的本地资源及已取得的厂商资源包进行了整理。研究保留原件、来源、型号和变体关系，公开摘要只说明已核实的范围。完整提取过程、ADB 可访问性调查、请求记录及原始报告位于 [research/earbud-artwork/](../../../research/earbud-artwork/) 和 [归档报告](../../../research/earbud-artwork/archive-2026-10-09/README.md)。

## 已取得的结果

| 2026-10-07 固定样本 | 数量与口径 |
| --- | --- |
| 资产 | 695 项，按原始字节 SHA256 去重：590 项 APK 本地资产、105 项远程独立资产 |
| 功能标记 | 512 项；标签允许重叠，不能按功能数量相加计算资产总数 |
| 图像预览 | 538 张，另有 52 页接触表，其中 19 页为功能美术 |
| 已确认的远程型号包 | 19 个：OPPO 8 个、OnePlus 1 个、realme 10 个 |
| 远程包成员 | 各型号关联共 105 张 PNG 和 19 份配置；跨型号去重后为 100 张 PNG 和 5 份配置 |

远程型号归属由产品 ID 请求、响应、资源配置及原包摘要建立；没有按外观相似度绑定型号。内置通用耳机、颈挂和开放式外观可作回退，但不能据此认定某一型号专用美术。型号、原件与预览的逐项关联见本地图册 [index.html](../../../research/earbud-artwork/index.html) 和 [assets.json](../../../research/earbud-artwork/assets.json)。

功能资源覆盖音质、降噪、自适应、通透、聆听、空间音频、游戏、电量、查找、听力、健康、翻译及操作引导。已核实 LHDC + Hi-Res 与 LHDC Lossless 徽标；其他 codec 的名称或文案检索不能替代图标证据。`normal` 是降噪关闭，`listen` 是独立聆听模式；教程、背景、颜色配置和状态图标分别保留语义，不能互相代用。通用功能图标也不表示某个型号具备该功能。

## 资源使用规则与限制

同一 Android 资源名可能对应密度、日夜主题、语言或尺寸变体，选择时需保留来源和绘制依赖。二进制 Android XML、Lottie JSON、原始位图与配置均保留原件；PNG/SVG 是派生预览。未解析的主题属性使用标注的替代色，动画矢量仅显示基础静帧；图册中的 Lottie 不播放，预览不能视为 Android 渲染的完整复刻。

本阶段旧库存按主产品 ID 计数，包含 140 个耳机 ID 和 3 个音箱 ID；后续云配置研究展开显式 child 后得到 141 个耳机识别 ID。19 个取得外观的型号、配置登记数量与 Windows 已验证控制型号是不同指标。请求成功但无资源地址只说明该次条件没有提供包；未知默认色、未查询或访问受限均不能证明没有资源。

早期库存中 X3、Free4 的模型未取得状态已被后续研究更新。X3、普通 Free4 和 Free4 丹拿版已取得官方模型，X4 取得官方动态 WebP，见 [云端美术摘要](../online-artwork/README.md)。当前应用已使用整理后的官方资源；资源持久化与模型显示分别见 [外观资源持久化](../../artwork-persistence.md) 和 [模型渲染设置](../../model-rendering.md)，本页历史数量不代表当前发布包包含的资产数量。

## 完整材料与复核入口

[最终原件及图册验证](../../../research/earbud-artwork/evidence/validation.json) 覆盖原件摘要、APK 来源、功能选择、绘制依赖、预览解码与图册数据一致性；远程原包和型号关联另见 [remote-verification.json](../../../research/earbud-artwork/evidence/remote-verification.json)。详细资源名、APK 条目、逐型号表、提取顺序和本机环境保留在完整归档中。

复现工具位于 [tools/research/earbud-artwork/scripts/](../../../tools/research/earbud-artwork/scripts/)。已有本地材料的统一离线复核入口是 [accept_cloud_research.py](../../../tools/research/accept_cloud_research.py)，从仓库根运行 `python -B -X utf8 tools/research/accept_cloud_research.py`，新报告写入 `research/cloud-acceptance/`；提取和图册重建工具可能改写派生产物，应按归档流程另行使用。

上述 `research/` 链接指向本地完整研究材料。普通仓库克隆不包含 APK、厂商原包、图册和完整运行记录；公开摘要与复现工具不能替代这些原件。
