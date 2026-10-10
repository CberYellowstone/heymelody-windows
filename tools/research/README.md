# 维护中的研究工具

完整研究对象、理解、结论、原始工具版本及运行产物保存在根目录 `research/`；整理后适宜公布的结果见 [docs/research/](../../docs/research/README.md)。本目录维护项目复现、资源提取和离线验收需要的工具。目录约定见 [研究资料规范](../../docs/research/organization.md)。

| 入口 | 用途 |
| --- | --- |
| [earbud-artwork/scripts/](earbud-artwork/scripts/) | APK美术、Android资源、来源探测、图册与验证 |
| [online-whitelist/scripts/](online-whitelist/scripts/) | 配置认证、解密、版本分析及离线验证 |
| [online-artwork/scripts/](online-artwork/scripts/) | 模型获取、容器解析、身份/颜色审计及验证 |
| [accept_cloud_research.py](accept_cloud_research.py) | 对固定本地配置、模型、图册进行离线验收，默认新输出位于research/cloud-acceptance |
| [inspect_oppo_apk.py](inspect_oppo_apk.py) | APK来源与内置配置检查 |
| [event_dex_audit.py](event_dex_audit.py)、[anc_dex_audit.py](anc_dex_audit.py)、[equalizer_dex_audit.py](equalizer_dex_audit.py) | 固定APK中事件、ANC与EQ相关DEX依据 |
| [preset_name_audit.py](preset_name_audit.py) | 官方预设名称的资源依据 |
| [game_audio_spatial_audit.py](game_audio_spatial_audit.py) | 固定两APK与X4固件的游戏／Hi-Res／空间离线审计；保存原件、原DEX、callback原字节和8源配置矩阵，默认另建research输出 |
| [fetch_hearing_audition.ps1](fetch_hearing_audition.ps1) | 获取并核对官方黄金听感试听原件，离线转换 PCM16；原件、转换工具版本和摘要默认归档到 research/hearing-preview-20261010/audio |

21个专题脚本原先位于 `docs/research/<专题>/scripts/`，2026-10-09迁到此处，旧路径解析与专题依赖同步更新。原字节版本位于本地 `research/<专题>/archive-2026-10-09/scripts/`，用于保留研究历史；运行入口使用此处维护版本。

旧脚本路径的运行兼容指向维护版本，不保证修改后的代码仍匹配旧manifest中的脚本摘要。核对旧脚本字节时使用完整档案中的归档版本；历史JSON中的原始path/hash不改写。

常规构建及离线应用测试不需要原始APK或完整研究目录。上述专题复现需要本地输入及已有Python依赖；在线获取仍需显式 `--online`，本轮整理不重新请求厂商资源。已有JSON、manifest和实测记录中的采样路径保持原值。
