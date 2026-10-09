# 研究资料索引

研究报告与复现脚本放在 `docs/research/`；研究时使用的安装包、截图、模型、原始资源和完整证据放在仓库根 [research/](../../research/README.md)，按用途与研究主题分类。文档直接链接本地证据，原始内容保留，不新增脱敏处理。

2026-10-09 黄金听感与贴合度的完整重新研究按本轮要求存放在 [research/calibration-reassessment-20261009/](../../research/calibration-reassessment-20261009/README.md)，这里保存 [受版本管理的纠正摘要](calibration-reassessment.md) 和 [后续组件修复](calibration-repair.md)。旧 [校准实现记录](calibration-codecs.md) 保留为修复前基线，不再作为当前实现或原厂完整协议证明。

## 报告与工具

先读 [设备适配交接](device-adaptation-handoff.md)、[云配置](cloud-device-configuration.md)、[云美术](cloud-device-artwork.md)。当前实现索引见 [docs/README.md](../README.md)。

| 文档入口 | 内容 |
| --- | --- |
| [黄金听感与贴合度重新研究](calibration-reassessment.md) | 两 APK 独立复核、X4 v116 交叉验证、UI路由/filter等纠正与完整研究入口 |
| [黄金听感与贴合度组件修复](calibration-repair.md) | 现有组件改动、当前协议与界面行为、回归验证及验证边界 |
| [X4 v116追加实测](../../research/calibration-reassessment-20261009/LIVE-TEST.md) | 双耳贴合度良好，12项测听/滤波/保存/读回，旧公开字段恢复；失败、重试与研究UI音量纠错 |
| [earbud-artwork/](earbud-artwork/README.md) | 2D／功能美术研究与提取、合并、验证脚本 |
| [online-whitelist/](online-whitelist/README.md) | 内置／云配置、认证与版本规则及验证脚本 |
| [online-artwork/](online-artwork/README.md) | 云模型、格式、材质与验证脚本 |
| [Enco X4 美术复核](enco-x4-artwork-audit.md) | 官方动态 WebP、操作 Lottie 角色与帧合成验证 |
| [软件模型材质研究](software-model-materials.md) | 0.8.5 软件材质代表值与光照修正、固定依赖证据和限制 |
| [Free4 身份与色号审计](online-artwork/free4-identity-color-audit.md) | 产品身份、色号与请求链审计结论 |
| [3D 验证](3d-validation/README.md) | 历史实验工作树与验证入口 |
| [OppoPods 对照研究](oppopods-comparison-2026-10-08/README.md) | 两个项目的官方资料使用程度、协议经验与局限 |

## 原件与证据

| 本地目录 | 内容 |
| --- | --- |
| [research/apps/](../../research/apps/) | 欢律 APK、OPPO 第一方无线耳机组件 APK |
| [research/calibration-reassessment-20261009/](../../research/calibration-reassessment-20261009/README.md) | 重新研究全过程、专题报告、原DEX/fallback、反编译、固件指令与配置矩阵 |
| [research/OPPO_Enco_X4_v116_reconstruction/](../../research/OPPO_Enco_X4_v116_reconstruction/README_zh.md) | 用户提供的可信X4 v116固件重建包；本轮保留不修改 |
| [research/installers/](../../research/installers/) | Surface MSIXbundle 等研究参考安装包 |
| [research/screenshots/](../../research/screenshots/) | 界面参考截图、用户提供的 X4 PNG 原件 |
| [research/upstreams/](../../research/upstreams/) | OppoPods1、OppoPods2、SteamTools 完整源码副本及 Git 元数据 |
| [research/earbud-artwork/](../../research/earbud-artwork/) | 2D 原图、功能素材、图册、预览、库存和完整证据 |
| [research/online-whitelist/](../../research/online-whitelist/) | 厂商配置、下载记录、静态契约、完整 run |
| [research/online-artwork/](../../research/online-artwork/) | ZIP、VFXMS、GLB、KTX、模型预览、完整 run 与审计证据 |
| [research/3d-validation/](../../research/3d-validation/) | 实验 worktree、依赖、NuGet cache、模型材质审计和验证输出 |
| [research/migrations/](../../research/migrations/) | 上一轮迁移的历史盘点清单 |

`docs/research/` 中的报告与脚本随源码管理；`research/` 继续忽略新加入的本地原件，原先已跟踪的静态契约保留原路径和版本管理。文档可以直接指向 `research/` 中的完整 JSON、图片、模型或第三方源码，无需另做脱敏副本。

## 路径与使用

上一轮临时放在 `docs/research/local/inputs/` 的文件已按类型回到 `research/apps/`、`installers/`、`screenshots/`；`local/upstreams/` 回到 `research/upstreams/`。各研究包的原件与证据回到 `research/<专题>/`，报告和脚本保留在 `docs/research/<专题>/`。

现行命令与 Markdown 链接已更新。历史 JSON、manifest、第三方源码及实验记录里的原路径保留，研究工具读取时兼容旧路径。实验 worktree 注册位置恢复为 `research/3d-validation/worktree`，原分支与工作树内容保留。

从仓库根运行已有离线验收：

```powershell
python -B -X utf8 tools/research/accept_cloud_research.py
```

验证输出另存 `artifacts/`。常规构建和应用运行使用 `src/` 中的资源包，研究目录用于继续研究与重新提取资源。
