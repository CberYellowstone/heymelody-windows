# 文档索引

当前版本为 0.8.5。除 Markdown 链接外，文档中的 `src/`、`tests/`、`docs/research/`、`reference/`、`artifacts/` 和 `release/` 路径均相对于仓库根目录。研究结论和工具位于 `docs/research/`，原始材料位于根目录 `research/`，具体位置见 [研究资料归档](research/README.md)。构建、发布和验证入口见 [REPOSITORY.md](../REPOSITORY.md)。

## 当前实现

- [外观资源持久化与重新下载](artwork-persistence.md)：0.8.4 的外观提示持久化、SPP 颜色读取、缓存优先解析、X4 动态 WebP、教程与产品主图分离、设置卡片中的“重新下载在线资源”，以及 Windows x64/ARM64 CI。
- [X4 美术复核](research/enco-x4-artwork-audit.md)：两来源官方旋转 WebP、控制 Lottie 教程的角色区分、模块获取与实际帧验证。
- [三维模型渲染设置](model-rendering.md)：0.8.5 软件材质与光照修正，保留 0.8.4 默认关闭 GPU 加速、设置持久化与即时切换。
- [软件模型材质研究](research/software-model-materials.md)：固定 Ava3D 软件后端与官方材质的差异、世界表面积加权的 UV 样本代表值、色彩空间及视觉验证限制。
- [ANC 主模式与提示音差异](research/anc-mode-prompt.md)：0.8.3 主模式父编号修复、自适应子档独立控制；检查包提示音效果待用户真机 A/B 确认。
- [Free4 丹拿版模型与验证](research/enco-free4-dynaudio-model.md)：官方原件、色号 3 请求与渲染证据；0.8.2 的会话内 Demo 使用该模型，长按主界面“+”两秒添加。
- [Enco X4 控制连接回归与 0.8.1 实机验证](research/enco-x4-0.8.1.md)：控制服务校验修复、型号提示及两套预装／缓存资源的只读验证。
- [Free4 三维模型与 0.7.1 体验包](research/enco-free4-0.7.1.md)：已迁入当前分支的模型控件、拖动与惯性、自转，以及迁移前独立工作区验证。
- [后台架构](backend-architecture.md)：托盘、共享 IPC、多设备监听、BLE 观测、设置与退出生命周期。
- [界面说明](gui-notes.md)：现有 Avalonia/FluentAvalonia 布局、编辑保护和界面验证。
- [日常控制层](daily-notes.md)：GET/SET、通知订阅、字段合并与结果证据。
- [CLI 工具](cli-tools.md)：保留的只读查询和受控 Hi-Res 实验，其边界独立于当前 GUI。
- [发布说明来源](../packaging/README.md) 与 [来源及许可](../packaging/SOURCE-NOTES.md)：构建时复制到发布包。

## 研究与历史

- [研究资料归档与提交边界](research/README.md)：专题目录、原始输入与上游仓库、新旧路径映射及本地保全规则。
- [两个 OppoPods 项目的适配调查](research/oppopods-comparison-2026-10-08/README.md)：官方型号表、协议与系统集成对照，证据位置及值得继续验证的经验。
- [设备适配研究交接与验收入口](research/device-adaptation-handoff.md)：下一会话的阅读入口、离线复现与原件保全清单。
- [云端设备配置](research/cloud-device-configuration.md)、[云端设备美术](research/cloud-device-artwork.md)：认证、解密、配置选择、模型/2D 原件与未证明范围。
- [X3 三维模型验证](research/enco-x3-3d-validation.md)、[X3 外壳遮挡修复](research/enco-x3-opaque-occlusion.md)：Free4 之前的独立实验记录，保留对应版本的证据。
- [蓝牙事件研究](research/bluetooth-events.md)：本轮通知、广播布局及 Windows API 依据；测试广播为合成 fixture。
- [FluentAvalonia 研究](research/fluent-avalonia.md)、[其他型号](research/oppo-device-support.md)、[跨平台](research/cross-platform.md)：调研结论与暂缓范围，不作支持承诺。
- [阶段 1](history/phase1-notes.md)、[阶段 2](history/phase2-notes.md)、[阶段 3/4](history/phase34-notes.md)：旧 CLI 和本机实验记录。
- [阶段 5](history/phase5-notes.md)、[协议证据](history/phase5-protocol-evidence.md)、[集成审阅](history/integration-review.md)：WPF 0.5.0 证据与 0.6.x 后续说明。

历史证据保留原始时间、版本、范围和源码定位；新的复核另存报告，不覆盖原件。资料迁移仅更新访问链接及工具入口，历史报告和 manifest 的旧路径保留。当前离线/模拟测试和静态包校验不证明真实耳机广播、手机并发、休眠恢复或 ARM64 实机行为。研究结论与复现脚本保存在 `docs/research/`；原始输入、上游仓库、厂商配置、图册和采样记录保存在根目录 `research/`。发布包与构建输出仍使用原有目录。
