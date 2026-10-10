# 研究资料索引

完整研究对象、理解、结论及产物保存在仓库根 `research/`；本目录保存整理后适宜公布的研究结果。具体规则见 [研究资料规范](organization.md) 和根目录 [AGENTS.md](../../AGENTS.md)。维护中的复现工具见 [tools/research/](../../tools/research/README.md)，其原始版本及运行产物保留在完整研究档案。

下面的 `research/` 链接是本机完整档案入口，不随普通Git clone提供；没有这些原件仍可阅读公开结论并正常构建。当前完整档案目录由本地 [research/README.md](../../research/README.md) 索引。

2026-10-09 黄金听感与贴合度的完整重新研究按本轮要求存放在 [research/calibration-reassessment-20261009/](../../research/calibration-reassessment-20261009/README.md)，这里保存 [受版本管理的纠正摘要](calibration-reassessment.md) 和 [后续组件修复](calibration-repair.md)。旧 [校准实现记录](calibration-codecs.md) 保留为修复前基线，不再作为当前实现或原厂完整协议证明。

## 公开研究结果

先读 [设备适配交接](device-adaptation-handoff.md)、[云配置](cloud-device-configuration.md)、[云美术](cloud-device-artwork.md)。当前实现索引见 [docs/README.md](../README.md)。

| 文档入口 | 内容 |
| --- | --- |
| [上下文读取风险排查](context-read-risk-2026-10-11.md) | 文档、源码、研究产物的字符与长行风险，subagent 预览规则和后续处理顺序；503 成因尚未确认 |
| [黄金听感预览与声音设置](hearing-preview-settings.md) | 0.8.13 结果雷达、原厂音乐试听与频谱、游戏分组、BassWave 和完整固件显示 |
| [游戏音效与 Hi-Res／空间音效重新研究](game-audio-spatial.md) | 两 APK 原 DEX、X4 原始固件、游戏开关／类型命令纠正、包名与互斥方向、Hi-Res旧版／V2手机分支 |
| [智能音量调节（原“自动音量”）](auto-volume.md) | 原厂用途与能力门控、17款配置机型、X4 v116静态/实机验证；0.8.10全机型隐藏的决定与右上提示修复 |
| [黄金听感与贴合度重新研究](calibration-reassessment.md) | 两 APK 独立复核、X4 v116 交叉验证、UI路由/filter等纠正与完整研究入口 |
| [黄金听感与贴合度组件修复](calibration-repair.md) | 现有组件改动、当前协议与界面行为、回归验证及验证边界 |
| [X4 v116追加实测](../../research/calibration-reassessment-20261009/LIVE-TEST.md) | 双耳贴合度良好，12项测听/滤波/保存/读回，旧公开字段恢复；失败、重试与研究UI音量纠错 |
| [earbud-artwork/](earbud-artwork/README.md) | 2D／功能美术的来源、角色与桌面适配结论 |
| [online-whitelist/](online-whitelist/README.md) | 内置／云配置、认证与版本选择结论 |
| [online-artwork/](online-artwork/README.md) | 云模型、格式、材质与验证范围 |
| [Enco X4 美术复核](enco-x4-artwork-audit.md) | 官方动态 WebP、操作 Lottie 角色与帧合成验证 |
| [软件模型材质研究](software-model-materials.md) | 0.8.5 软件材质代表值与光照修正、固定依赖证据和限制 |
| [Free4 身份与色号审计](online-artwork/free4-identity-color-audit.md) | 产品身份、色号与请求链审计结论 |
| [3D 验证](3d-validation/README.md) | 历史实验工作树与验证入口 |
| [OppoPods 对照研究](oppopods-comparison-2026-10-08/README.md) | 两个项目的官方资料使用程度、协议经验与局限 |

## 原件与证据

| 本地目录 | 内容 |
| --- | --- |
| [research/hearing-preview-20261010/](../../research/hearing-preview-20261010/README.md) | 结果截图、完整源码与 APK 保全、计算推导、音频原件及转换、实现与验证证据 |
| [research/apps/](../../research/apps/) | 欢律 APK、OPPO 第一方无线耳机组件 APK |
| [research/game-audio-spatial-20261010/](../../research/game-audio-spatial-20261010/README.md) | 两包与X4固件原件、字节保全、完整过程／失败判断、原DEX指令和8源配置矩阵 |
| [research/calibration-reassessment-20261009/](../../research/calibration-reassessment-20261009/README.md) | 重新研究全过程、专题报告、原DEX/fallback、反编译、固件指令与配置矩阵 |
| [research/OPPO_Enco_X4_v116_reconstruction/](../../research/OPPO_Enco_X4_v116_reconstruction/README_zh.md) | 用户提供的可信X4 v116固件重建包；本轮保留不修改 |
| [research/installers/](../../research/installers/) | Surface MSIXbundle 等研究参考安装包 |
| [research/screenshots/](../../research/screenshots/) | 界面参考截图、用户提供的 X4 PNG 原件 |
| [research/calibration-wizard-design-20261009/](../../research/calibration-wizard-design-20261009/README.md) | 用户提供的 ColorOS 黄金听感向导截图原件、摘要及 0.8.9 校准窗口设计取舍 |
| [research/upstreams/](../../research/upstreams/) | OppoPods1、OppoPods2、SteamTools 完整源码副本及 Git 元数据 |
| [research/earbud-artwork/](../../research/earbud-artwork/) | 2D 原图、功能素材、图册、预览、库存和完整证据 |
| [research/online-whitelist/](../../research/online-whitelist/) | 厂商配置、下载记录、静态契约、完整 run |
| [research/online-artwork/](../../research/online-artwork/) | ZIP、VFXMS、GLB、KTX、模型预览、完整 run 与审计证据 |
| [research/3d-validation/](../../research/3d-validation/) | 实验 worktree、依赖、NuGet cache、模型材质审计和验证输出 |
| [research/history/](../../research/history/) | 公开历史文档整理前的完整记录 |
| [research/oppopods-comparison-2026-10-08/](../../research/oppopods-comparison-2026-10-08/README.md) | 完整对照报告、逐项勘证、机器快照；源码在upstreams |
| [research/evidence-archive-2026-10-09/](../../research/evidence-archive-2026-10-09/) | 早期APK、白名单、模型及归档验收证据的原字节副本 |
| [research/migrations/](../../research/migrations/) | 历史盘点、原稿归档及本轮整理清单 |

`docs/` 的公开整理稿与 `tools/research/` 的维护工具随源码管理；`research/` 继续忽略新加入的完整档案，七份原先已跟踪的静态契约保留原位置与跟踪状态。完整报告、证据和研究对象均保留原文；公开稿从完整资料整理而来，不把个人数据、本机操作流水或完整机器快照直接列为公开结果。

## 路径与使用

此前原件已按类型回到 `research/apps/`、`installers/`、`screenshots/` 和 `upstreams/`。本轮进一步将完整报告、勘证、脚本原版与研究证据归档到 `research/`，公开专题和历史文档完成整理，21个项目复现脚本迁到 `tools/research/<专题>/scripts/`。位置及保全记录见 [本轮整理](organization.md)。

现行命令与 Markdown 链接已更新。历史 JSON、manifest、第三方源码及实验记录里的原路径保留，研究工具读取时兼容旧路径。实验 worktree 注册位置恢复为 `research/3d-validation/worktree`，原分支与工作树内容保留。

从仓库根运行已有离线验收：

```powershell
python -B -X utf8 tools/research/accept_cloud_research.py
```

新研究验收输出另存 `research/cloud-acceptance/`，不覆盖历史记录。常规构建和应用运行使用 `src/` 中的资源包；继续研究或重新提取资源才需要本地完整研究对象。
