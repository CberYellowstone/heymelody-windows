# 耳机 3D 界面验证

本页记录的历史体验分支为 `experiment/enco-free4-3d-0.7.1`，沿用已验证的 X3 交互与 0.7.0 控制架构，主界面左侧换用此前云端获取的官方 Enco Free4 模型。完整 Windows x64 包自带运行时，Free4 仅替换外观，正常客户端保留 X4 控制。

- [0.7.1 x64 体验包](../../../research/3d-validation/worktree/release/HeyMelody_Windows_0.7.1_x64.zip)
- [Free4 来源、适配和完整验证记录](../../../research/3d-validation/worktree/docs/research/enco-free4-0.7.1.md)
- [发布包实际窗口](../../../research/3d-validation/worktree/artifacts/free4-0.7.1-packaged-window.png)
- [原件材质审计](../../../research/3d-validation/free4-material-audit/README.md)

完整解压后双击 `Start-3D-Demo.cmd` 即可查看模型；`Start-HeyMelody.cmd` 为正常客户端入口。保留水平拖动修正、强阻尼惯性与 20°/秒空闲自转。259 项原生模型检查和 338 项原有离线检查通过，发布包的 583 个文件、模型哈希与 0.7.1 版本均已核验。

## 此前 X3 验证

独立 worktree 基于应用版本 0.7.0 的 `dd14bbe2531914c3bed295eecc33b7f9e2daa0ca`，分支 `experiment/enco-x3-3d`。验证现有 Avalonia / FluentAvalonia 左侧主示意图加载 X3 GLB，以及拖拽、惯性和闲置水平自转；演示模式不连接 Bluetooth，也不改变模块架构。

- [实验说明、来源摘要、许可和运行命令](../../../research/3d-validation/worktree/docs/research/enco-x3-3d-validation.md)
- [外壳遮挡修复、材质诊断与对比证据](../../../research/3d-validation/worktree/docs/research/enco-x3-opaque-occlusion.md)
- [原始模型获取与离线结构验证](../online-artwork/README.md)
- [OEM 旋转与惯性静态证据](../../../research/online-artwork/evidence/rotation-contract.json)
- [Ava3DControl 包内许可](../../../research/3d-validation/dependencies/LICENSE.md)

在主仓库根目录启动：

```powershell
Set-Location research/3d-validation/worktree
.\Run-3D-Demo.ps1
```

上述开发启动脚本当前显示 Free4。此前 X3 通过 259 项运动/原生窗口检查，包含拖动与惯性方向、20°/秒自转及外壳遮挡回归，以及 47 项界面回归检查。实际后端为 OpenGL ES 3.0 / RTX 2060。交互修正使模型随鼠标水平转动，稳定自转恢复为无线耳机的 20°/秒；松手强阻尼惯性与隐藏/最小化暂停仍按桌面适配实现。

- [修复后亮色主界面截图](../../../research/3d-validation/worktree/artifacts/model-orbit-direction-speed-final/main-light-1280x900-window.png)
- [修复后暗色主界面截图](../../../research/3d-validation/worktree/artifacts/model-orbit-direction-speed-final/main-dark-1280x900-window.png)
- [原生验证报告](../../../research/3d-validation/worktree/artifacts/model-orbit-direction-speed-final/report.json)

当前是独立效果验证分支：Ava3DControl 为闭源 preview，其他 GPU 与软件回退尚未验证，OEM 完整 IBL 光照未移植。


## 2026-10-08 归档说明

研究说明保存在本目录；worktree、缓存、依赖、材质审计原件和截图／发布包位于 `research/3d-validation/`。worktree 路径为 `research/3d-validation/worktree`，Git 注册路径已修复，分支和 HEAD 保持原值。当前分支已采纳的研究结论见 [Free4 记录](../enco-free4-0.7.1.md)、[X3 验证](../enco-x3-3d-validation.md) 和 [归档索引](../README.md)。历史报告中旧 `research/3d-validation/` 路径仍为当时取证位置。
