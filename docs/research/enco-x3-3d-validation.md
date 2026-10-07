# Enco X3 3D 示意图验证

这是 X3 独立实验的历史记录，结论与截图对应其当时的源码。后续已换成 Free4 模型并迁入 `arch/modular-devices`；当前源码入口与验证范围见 [Free4 记录](enco-free4-0.7.1.md)。本文中的本地历史证据仍位于 `research/3d-validation/worktree`。

本实验基于 `dd14bbe2531914c3bed295eecc33b7f9e2daa0ca`，该提交的应用版本号为 **0.7.0**，没有对应 tag。独立分支为 `experiment/enco-x3-3d`，worktree 位于主仓库的 `research/3d-validation/worktree`。

目标是在现有 Avalonia / FluentAvalonia 界面中，将左侧 X4 主示意图替换为 X3 GLB，验证鼠标拖拽、松手惯性及闲置水平自转。`--3d-demo` 使用演示状态，不连接 Bluetooth。实验只涉及主示意图及其交互，不改变设备控制逻辑或模块架构；X3 实验当时仅保留在独立分支。

## 渲染库

固定使用 **Ava3DControl 12.1.0-preview.16**，通过现成控件加载 GLB 和渲染场景。应用仅编写交互与场景配置，不自行实现渲染底层。

- [官方 GitHub](https://github.com/pavel-zheltiakov/Ava3DControl)
- [固定版本 NuGet](https://www.nuget.org/packages/Ava3DControl/12.1.0-preview.16)
- [本次包内 LICENSE.md](../licenses/Ava3DControl/LICENSE.md)、[第三方声明](../licenses/Ava3DControl/THIRD-PARTY-NOTICES.md)

包内许可为 freeware：允许免费商用，以及作为自有应用组件分发未修改的二进制；分发包和文档时须保留许可与版权声明。编译库为闭源，不能据此将其当作开源库；许可限制修改或反编译编译库、分发其衍生版本和单独转售。示例源码及第三方组件各有许可范围。本次保留原 NuGet 包，SHA256 为 `8a876c52573a10ade210181eedaa0db83436cc5bb41b8c15570c79afdcec3ab1`。

这是 preview 版本，API、渲染后端和驱动兼容性仍需验证。包内 README 提及较早的 preview.14，本实验实际版本依据 nuspec 和项目固定引用，确为 preview.16。

## 模型来源

使用此前研究中取得的 OPPO Enco X3 资源：产品 ID `067410`、请求配色值 `2`、资源模块 `14`。欢律与无线耳机两种认证来源返回相同包摘要；配色值仅是本次成功的协议值，不认定为默认色或商品颜色名称。请求详情和完整归属边界见 [资源研究记录](../../research/online-artwork/README.md)。

| 原件或导出 | 字节数 | SHA256 |
|---|---:|---|
| 原 ZIP | 9,581,441 | `b4763c5f9f790b0859131807324bf81c4a1855489fc087e715f673de848119bf` |
| `detail_model.vfxms` | 12,932,064 | `5b57fbff64ecc4e2521e693847fc2dc49f5e6d3598a6f76cdacb053a990795aa` |
| 导出 `model.glb` | 10,835,448 | `6f6ab09e3b5067d3f1d4fac732243bb5fe723e3eb982d9b84e629560e5813363` |

GLB 是按容器边界直接复制的原始段，没有重编码。它为 GLB 2.0，含 31 个 mesh、39 个 node、1 个 material，没有外部 URI 或内置 glTF animation。容器内原场景 JSON 与 IBL KTX 仍保留；本实验并不声称复刻官方完整光照和材质效果。结构验证见 [报告](../../research/online-artwork/runs/20261007T101708.195649Z/model-inspection/report.json)。

原始导出路径（相对于主仓库）：

```text
research/online-artwork/runs/20261007T101708.195649Z/model-inspection/
  b4763c5f9f790b0859131807324bf81c4a1855489fc087e715f673de848119bf/
  extracted/res/raw/detail_model/model.glb
```

X3 验证提交 `e7db26b` 中的应用副本位于 `src/HeyMelody.App/Assets/Devices/EncoX3/model.glb`，SHA256 与上述导出一致。当前树已用 Free4 资源替换该副本；原始研究导出与历史提交仍保留 X3 原件。资源获取与结构检查本身不等于渲染效果验收。

本次补充修复了原件不透明材质声明为 `BLEND` 所导致的内部零件覆盖外壳问题：在 SDK 加载后针对材质 `1001` 使用不透明绘制并开启深度写入。源 GLB 保持原样，四个方向的真实渲染均已验证。原因、前后对比和绘制顺序压力检查见 [遮挡修复记录](enco-x3-opaque-occlusion.md)。

## 交互参数与官方差异

参考无线耳机 17.6.3 的 `CameraController`，具体 DEX 方法及偏移见 [rotation-contract.json](../../research/online-artwork/evidence/rotation-contract.json)。

| 行为 | OEM APK | 本实验桌面适配 |
|---|---|---|
| 稳定水平自转 | 默认 20°/秒，一周 18 秒 | 20°/秒，一周 18 秒 |
| 松手阻尼 | 拖拽残量逐帧乘 `1 - 7.2 × dt` | 角速度乘 `exp(-7.2 × dt)`，位移采用精确积分 |
| 俯仰范围 | ±85° | ±85° |
| 恢复自转 | 惯性结束即自转，并逐帧恢复视角；常规松手不经过 Idle | 惯性结束后等待 0.65 秒，再恢复慢速水平自转 |

桌面惯性公式为 `v(t+dt)=v(t)×exp(-7.2×dt)`、`Δangle=v(t)×(1-exp(-7.2×dt))/7.2`。沿用阻尼数值 7.2，但改用指数衰减与精确积分，以减少帧率对运动的影响；这不是 APK 算法的逐行移植。鼠标事件使用桌面坐标，拖拽位移按 `0.008 rad/坐标单位` 转换，也没有沿用 Android 的整数像素增量乘 21 算法。水平拖动使用负相机 yaw 增量，让模型随鼠标左右转动，松手惯性沿同一方向继续；垂直拖动保持原方向。隐藏或卸载视图时暂停动画，并取消当前拖拽与惯性。

2026-10-07 按用户验证反馈，将此前适配的 5°/秒恢复为无线耳机的稳定自转默认值 20°/秒。欢律样本同样为 20°/秒：其 `classes3.dex` 构造方法在 `0x39874a` 加载该常量，在 `0x39874e` 写入 `mRotateSpeed`；无线耳机对应位置为 `classes2.dex:0x2edb0e / 0x2edb12`。OEM 普通松手后另有短暂 40→20°/秒的回正过渡，本次只恢复稳定自转速度，桌面惯性及恢复等待仍如上表所述。

## 运行与验证状态

以下为当时 X3 源码的运行命令，对应提交 `e7db26b`。当前分支与独立工作区最新源码均已改为 Free4，直接运行最新入口会显示 Free4：

```powershell
Set-Location research/3d-validation/worktree
.\Run-3D-Demo.ps1
```

当时也可双击 worktree 根目录的 `Run-3D-Demo.cmd`。启动器运行已构建的 Windows x64 程序；`-Rebuild` 会重新构建，需要 .NET 10 SDK。该实验使用 worktree 旁的 `nuget-cache`，可读取已有全局缓存；只复制当前 Windows x64 需要的 native 文件，不复制 NuGet native 调试符号，避免验证产物占用过大。许可证随应用产物一并复制。当前主仓库的启动方式见 [Free4 记录](enco-free4-0.7.1.md)。

2026-10-07 已完成真实 Windows 窗口验证，而非仅依靠 headless 截图：

| 验证 | 结果 |
|---|---|
| App 与测试构建 | 0 警告、0 错误 |
| 运动和原生检查 | 最新 259 项通过：115 项纯运动检查、144 项原生检查，包含拖动与惯性方向、20°/秒自转及四个方向的遮挡回归 |
| 原有界面回归 | 前次 47 项通过；这些 headless 图只用于界面布局与状态检查，本次另捕获修复后的四组真实主界面 |
| 本机实际渲染后端 | OpenGL / OpenGL ES 3.0，NVIDIA GeForce RTX 2060 |
| 实际加载几何 | 31 个场景项、31 次绘制、98,367 个三角形 |
| 主界面大窗口 | 600×520 视口，模型前景约 322×287 像素 |
| 主界面紧凑窗口 | 600×138 视口，模型前景约 86×77 像素 |
| 用户输入 | 真实 HWND 鼠标消息可拖动，松手继续旋转，350 ms 内惯性明显下降 |
| 空闲旋转 | 原生计时容差内为 20°/秒；纯状态检查为精确 20°/秒 |
| 隐藏/最小化/关闭 | 停止动画计时器，隐藏后真实渲染帧数不增长，恢复无时间跳变 |

明暗主题、1280×900 与 900×640 四组主界面原生截图已目检，模型没有边缘裁切。图像来自真实窗口像素，同时保留 Ava3D 的独立场景截图，避免将 offscreen capture 当作界面呈现的证明。模型 SHA256 校验、独立场景加载、30/60/144 Hz 运动一致性、俯仰限位、陈旧输入不产生惯性也已检查。验证没有调用 Bluetooth 服务。

本机历史证据保存在未提交的 `research/3d-validation/worktree/artifacts` 目录：

- [原生与运动报告，含方向、速度及遮挡回归](../../research/3d-validation/worktree/artifacts/model-orbit-direction-speed-final/report.json)
- [原有界面回归报告](../../research/3d-validation/worktree/artifacts/avalonia-regression-final/validation.json)
- [亮色主界面](../../research/3d-validation/worktree/artifacts/model-orbit-direction-speed-final/main-light-1280x900-window.png)、[暗色主界面](../../research/3d-validation/worktree/artifacts/model-orbit-direction-speed-final/main-dark-1280x900-window.png)
- [紧凑亮色](../../research/3d-validation/worktree/artifacts/model-orbit-direction-speed-final/main-light-900x640-window.png)、[紧凑暗色](../../research/3d-validation/worktree/artifacts/model-orbit-direction-speed-final/main-dark-900x640-window.png)

历史 X3 源码的复测命令（原生测试会短暂打开并关闭测试窗口；当前源码检查的是 Free4）：

```powershell
dotnet build tests/HeyMelody.ModelPreview.Tests -r win-x64 -p:SelfContained=false -p:CopyDebugSymbolFilesFromPackages=false
dotnet run --project tests/HeyMelody.ModelPreview.Tests --no-build -r win-x64 -- --native-smoke
```

实际渲染诊断的短采样约 0.8–0.9 ms/帧，仅代表本机 GPU 渲染阶段，不是整个应用性能保证。空闲请求 30 fps，拖动和惯性时请求 60 fps，按需绘制；实际帧率受 Avalonia、驱动与调度影响。尚未验证其他 GPU、软件回退后端，光照也未完整移植 OEM Filament 的 IBL KTX。这是效果验证分支，库为闭源 preview，不能视为正式集成与跨设备兼容性验收。

接入时还验证了两个具体 API 边界：Ava3D 的 class handler 会释放指针捕获，应用必须在 tunnel 阶段先结束拖动才能保留惯性；`Camera.Fit(bounds, margin)` 第二参数是安全边距，默认 1.25，并不是宽高比。验证版使用具名 `margin` 参数，并按真实 viewport 布局适配，避免紧凑窗口将模型意外缩小。
