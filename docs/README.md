# 文档索引

当前版本为 0.7.1。除 Markdown 链接外，文档中的 `src/`、`tests/`、`research/`、`reference/`、`artifacts/` 和 `release/` 路径均相对于仓库根目录。构建、发布和验证入口见 [REPOSITORY.md](../REPOSITORY.md)。

## 当前实现

- [Free4 三维模型与 0.7.1 体验包](research/enco-free4-0.7.1.md)：已迁入当前分支的模型控件、拖动与惯性、自转，以及迁移前独立工作区验证。
- [后台架构](backend-architecture.md)：托盘、共享 IPC、多设备监听、BLE 观测、设置与退出生命周期。
- [界面说明](gui-notes.md)：现有 Avalonia/FluentAvalonia 布局、编辑保护和界面验证。
- [日常控制层](daily-notes.md)：GET/SET、通知订阅、字段合并与结果证据。
- [CLI 工具](cli-tools.md)：保留的只读查询和受控 Hi-Res 实验，其边界独立于当前 GUI。
- [发布说明来源](../packaging/README.md) 与 [来源及许可](../packaging/SOURCE-NOTES.md)：构建时复制到发布包。

## 研究与历史

- [X3 三维模型验证](research/enco-x3-3d-validation.md)、[X3 外壳遮挡修复](research/enco-x3-opaque-occlusion.md)：Free4 之前的独立实验记录，保留对应版本的证据。
- [蓝牙事件研究](research/bluetooth-events.md)：本轮通知、广播布局及 Windows API 依据；测试广播为合成 fixture。
- [FluentAvalonia 研究](research/fluent-avalonia.md)、[其他型号](research/oppo-device-support.md)、[跨平台](research/cross-platform.md)：调研结论与暂缓范围，不作支持承诺。
- [阶段 1](history/phase1-notes.md)、[阶段 2](history/phase2-notes.md)、[阶段 3/4](history/phase34-notes.md)：旧 CLI 和本机实验记录。
- [阶段 5](history/phase5-notes.md)、[协议证据](history/phase5-protocol-evidence.md)、[集成审阅](history/integration-review.md)：WPF 0.5.0 证据与 0.6.x 后续说明。

历史证据保留原始时间、版本、范围和源码定位；未重新执行或扩大其结论。当前离线/模拟测试和静态包校验不证明真实耳机广播、手机并发、休眠恢复或 ARM64 实机行为。研究原件、发布包、日志、抓包和构建输出由 `.gitignore` 保留在本地。
