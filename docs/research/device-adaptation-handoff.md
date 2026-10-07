# 设备适配研究交接与验收入口

本页整理截至 **2026-10-07** 完成的欢律、无线耳机云端配置和美术研究，供新会话依次开展设备操作适配、设备美术适配。**获取、认证材料恢复、内置配置解密、云配置选择和美术完整性已有可复现证据；各型号 Windows 控制能力仍须逐项适配。** 本次只做离线整理验收，没有新网络请求、手机操作或应用功能变更。

从本页开始，依次阅读 [设备配置交接](cloud-device-configuration.md)、[设备美术交接](cloud-device-artwork.md)。两页分别列出原件路径、摘要、DEX 定位、处理规则及已验证范围。路径相对仓库根目录；本地研究原件链接需要保留当前工作区才能打开。

## 当前基线与既有要求

当前分支为 `arch/modular-devices`，应用基线提交为 `96c0804dd2500def558078ba6a43a06aadbf521f`，版本 **0.7.1**。Free4 3D 模型与交互已迁入 App，控制继续沿用 0.7.0 的 X4 架构。后端不加载模型或 GPU 资源。实现与原生验证见 [Free4 0.7.1 记录](enco-free4-0.7.1.md)。

原模块化方案已按用户要求撤销。旧草稿仅归档于 stash 对象 `2e3c31e70ca52b6e330c93a8fbdb74e771454643`，**不要恢复作为新适配工作的前置步骤**。独立实验工作区保留于 `research/3d-validation/worktree`，当前应用不依赖它。

后续仍需满足这些既有要求：

- 理解并复刻两 App 的内置与云配置处理，包括认证与解密；设置允许整体选择欢律或无线耳机来源。分别保留两源，不按字段拼出第三份配置。
- 优先性能和安全性，能力逻辑与美术加载分离。第三方模块导入暂只准备底层边界，不实现导入向导。
- 继续使用 Avalonia 与 FluentAvalonia。操作输入包括开关、Slider、RadioButton、文本框、ComboBox；Expander 可声明默认展开。多段 EQ 的段数、频率、容量由设备定义，预设/自定义采用 RadioButton 并分隔，容量允许时显示新建入口。这些仍是待实现需求。
- 先操作适配，再美术适配。Android 功能声明不能直接授权 Windows 写命令或宣称高规格音频链路已可用。

## 验收结论与固定证据

| 项目 | 结果 | 原件与复核入口 |
|---|---|---|
| 两源设备配置 | 原 52 项检查通过；两 APK 认证材料重新恢复并通过 GCM；分析重算与历史结果一致 | [正式 run](../../research/online-whitelist/runs/20261007T095049.319796Z/results.json)、[分析](../../research/online-whitelist/runs/20261007T095049.319796Z/analysis.json)、[新复核](../../artifacts/research-handoff-whitelist/verification.json) |
| 欢律覆盖 | 85 主条目 / 85 展开产品 ID | [配置交接](cloud-device-configuration.md) |
| 无线耳机覆盖 | 143 主条目 / 144 展开 ID，其中 141 耳机、3 音箱 | [配置交接](cloud-device-configuration.md)；子项展开是分析器策略，仍须核对 Android 识别/继承逻辑 |
| 普通 Free4 `068C10` | 配色请求值 1，两来源成功，共 1 原 ZIP；导出 GLB 与 App 原件相同 | [run 101352](../../research/online-artwork/runs/20261007T101352.199515Z/results.json)、[新复核](../../artifacts/cloud-research-acceptance/artwork-20261007T101352.199515Z.json) |
| X3 `067410` | 配色请求值 2，两来源成功，共 1 原 ZIP，完整模型导出 | [run 101708](../../research/online-artwork/runs/20261007T101708.195649Z/results.json)、[新复核](../../artifacts/cloud-research-acceptance/artwork-20261007T101708.195649Z.json) |
| 既有美术图册 | 695 去重资产、19 已确认型号资源包、538 预览、52 接触表全部通过 | [离线图册](../../research/earbud-artwork/index.html)、[资产清单](../../research/earbud-artwork/assets.json)、[新复核](../../artifacts/cloud-research-acceptance/earbud-artwork-validation.json) |

原获取记录和验证报告保持不变，新验收单独保存。上述数量描述样本范围，不是设备控制支持数量。

本轮统一入口的 [完整验收摘要](../../artifacts/research-acceptance-handoff-20261007-v2/summary.json) 和 [保全 manifest](../../artifacts/research-acceptance-handoff-20261007-v2/retention-manifest.json) 将上述固定输入一并复核，也重新检查容器全部 KTX 面/mip 边界及当前 App 的 Free4 GLB 字节。实际依赖版本写入摘要；工具优先使用已安装环境，缺失时才查本地缓存。

后续必须使用以下已纠正的规则：

1. API 请求 `versionCode` 来自 application meta-data：欢律 `17006004`、无线耳机 `17006002`，不是 manifest 安装版本。欢律内置有效内容版本加一亿，为 `117006004`。
2. 此次云端 `.headset` 是明文 UTF-8 JSON。AES-GCM 用于 APK 认证材料、内置名单和安装私有缓存，三种链不同；VFXMS 模型容器也未加密。
3. 官方先按非 null 规则转换兼容字段，空列表和 0 仍有效；启动时选内容版本更高的一整份配置，相等时内置优先；运行中仅发布严格更新内容。文件版本用于下载变更判断。本次两云配置均满足采纳条件。
4. 本次 85 个共有产品的两源配置全部存在差异，不能取字段并集。OEM 特有条件、固件门槛和系统服务需要保留。选无线耳机来源不等于获得 Windows 无损编码链路。
5. 旧 `earbud-artwork/models.json` 是 2D 阶段库存，X3/Free4 的 `not_obtained` 已被后续 `online-artwork` 成功记录更新。普通 Free4 和丹拿版 `06C010` 不同，后者在记录条件下未取得模型；请求色号尚未映射为营销色名或实机读取命令。

## 新会话的离线复验

在仓库根目录运行 [统一验收器](../../tools/research/accept_cloud_research.py)。需要 Python 3.11+、已安装的 `cryptography` 与 `Pillow`；不安装依赖，不启动 Android 或连接耳机。本机缓存只是依赖回退，跨电脑应显式准备 Python 环境。

```powershell
python -B -X utf8 tools/research/accept_cloud_research.py
```

默认创建 `artifacts/research-acceptance-<UTC>/`；也可指定尚不存在的目录：

```powershell
python -B -X utf8 tools/research/accept_cloud_research.py --output-dir artifacts/research-acceptance-next-session
```

读取输出 `summary.json` 的总体/分项 `passed` 与退出码。`retention-manifest.json` 记录保全文件的仓库相对路径、大小和 SHA256；配置/模型/图册报告复核原件及历史证据。输出必须在 `artifacts` 内，已存在时拒绝执行。

单独复验时，两验证器均支持 `--output <新的 artifacts 报告路径>`；不传则仍原位写 `RUN/verification.json`。`analyze_whitelist.py` 和 `inspect_models.py` 旧 CLI 也会写回研究目录，阅读成果不要默认重跑。在线入口及成功条件见两个专题交接，本次证据不用 `latest-run.json` 定位。

## 保全范围与跨会话使用

| 必须保留 | 原因 |
|---|---|
| `research/欢律.apk`、`research/无线耳机_17.6.3.apk` | 证书、认证材料入口、内置密文、静态代码和资源出处；指纹见专题文档 |
| `research/online-whitelist/runs/20261007T095049.319796Z/` | 两源内置/云配置、下载原字节、元数据、缓存样本、分析和历史验收 |
| `research/online-artwork/runs/` 与 `evidence/` | 原 ZIP、传输字节、VFXMS、场景 JSON、GLB/KTX 及成功/空响应记录 |
| `research/earbud-artwork/` | 原 ZIP、APK 美术原件、型号/功能/变体关联、绘制依赖、图册、预览与历史工具 |
| 研究脚本、脱敏静态契约、本页及专题交接 | 可复现规则；DEX 小工具迁至 `tools/research/event_dex_audit.py` |
| 本轮新 `artifacts` 验收报告及 manifest | 本次验收、历史报告未变的证据和备份核对索引 |
| `artifacts/apk-research/` | 早期白名单、反汇编及事件链索引；本页的新结论优先于旧报告中的已纠正解释 |

同工作区切换会话可直接使用。**Git 保存文档、指定脚本和脱敏契约，不包含 APK、完整厂商配置、原 ZIP、运行报告或整套图册。克隆仓库不能恢复这些原件；清理忽略目录或迁移电脑前须按 manifest 备份。** 本轮不复制大体积原件；manifest 是索引，不是备份包。

保存的最小脚本闭包保证现有成果读取、验收及两云接口复现：选定 `online-whitelist/scripts`、`online-artwork/scripts` 工具，以及 `earbud-artwork/scripts/{probe_resources,android_resources,research_runtime,merge_and_verify}.py`。旧图册 README 的完整重建流程还需本地提取/图册生成等历史脚本；克隆后的最小闭包不保证从 APK 重建全部图册。仅复制 fetcher 或 GLB 也会丢失复验出处。本轮依赖版本见 [cloud-research-requirements.txt](../../tools/research/cloud-research-requirements.txt)，验收摘要记录实际加载版本；旧 [requirements.txt](../../tools/research/requirements.txt) 是另一套 APK 研究环境要求。

认证材料、真实签名/nonce/认证头、完整临时下载 URL 和缓存测试 key 均不纳入交接。测试 key 未保留，不能在新进程再次解密旧测试密文；验收检查旧字节和记录，并用新内存 key 重做加解密、错误 key 与篡改拒绝测试。部分旧远程配置是脱敏派生文件，原 ZIP 才是原字节权威。

## 接下来如何使用成果

操作适配从选定来源的完整有效配置建立精确 product ID、SPP 条件、功能版本、固件过滤，再追踪 APK 的 GET/SET/通知翻译及现有 X4 transport。补齐命令号、payload、能力位图交集、事件解码、读回确认、EQ 增益/步长/槽位和嵌套模式。`equalizer`、`customEqualizer` 是功能版本，不能当段数/保存上限；X3 默认六段与 X4 显式十段出处已保留。

美术适配按精确 product ID、色号、模块/角色、左右耳/盒状态选择资源，保留通用回退与来源。`moduleId=14` 模型包与旧 `control` PNG 包契约不同。按资产分别验收透明度、尺度、轴向、光照和释放生命周期。Free4 材质修复只针对固定摘要的已知模型，OEM IBL/skybox 尚未接入。

待证明的边界包括 Android provisional/子配置完整运行逻辑、其他型号控制与实机行为、设备色号读取、OEM 音频系统条件、完整缓存刷新及 Android TLS pinning。配置没有独立服务端内容签名；本地 SHA256 及资源元数据预期摘要不能称为厂商数字签名。第三方模块信任边界须在新设计中另行建立。

新会话可直接使用以下说明：

> 先读 docs/research/device-adaptation-handoff.md 及设备配置/美术交接，在当前 arch/modular-devices 的 0.7.1 基线上继续。本轮先做设备操作适配，保留欢律/无线耳机整体来源切换要求。旧模块方案已撤销，不恢复旧 stash。优先固定样本和离线验收，无需重复批量云端查询；按已核实协议与固件条件逐项实现，随后再做美术适配。
