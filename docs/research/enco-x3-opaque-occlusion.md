# Enco X3 外壳遮挡修复

本文记录 X3 独立验证提交 `ff99141` 的修复与结果。当前分支已换成 Free4，相关处理沿用在 [Free4 模型控件](../../src/HeyMelody.App/EncoFree4ModelView.cs)；当前资产与验证范围见 [Free4 记录](enco-free4-0.7.1.md)。以下截图、统计和 DLL 摘要仍只证明历史 X3 构建。

2026-10-07，在 `experiment/enco-x3-3d` 验证分支修复外壳可见内部结构的问题。真实窗口和同相机对比确认，充电盒外壳现在能正确遮住耳机柄及背面按钮。

## 原因与修复

原始 GLB 唯一材质 `1001` 声明 `alphaMode: BLEND`，31 个 mesh 全部共用它。但 base-color 是没有 alpha 通道的 RGB PNG，SDK 解码后的 4,194,304 个像素均为 alpha 255，base-color factor 也为 `(1,1,1,1)`。没有顶点颜色或透射扩展。Ava3D 原样导入后为 `BlendMode.Alpha`、`DepthWrite=false`、`DepthTest=true`：内部零件可能在外壳之后绘制并覆盖外壳，形成穿透外观。

应用在校验原始 GLB 的长度及 SHA256、通过现成 SDK 导入后，只调整这个已知模型的材质：

```csharp
var material = model.Materials["1001"];
material.Blend = BlendMode.Opaque;
material.DepthWrite = true;
```

改动位于历史提交 `ff99141` 的 `src/HeyMelody.App/EncoX3ModelView.cs`，该文件后续随模型替换而更名。保留原有深度测试、背面剔除、贴图及全部几何；源 GLB SHA256 仍为 `6f6ab09e3b5067d3f1d4fac732243bb5fe723e3eb982d9b84e629560e5813363`。这个修正只适用于本次已验证的不透明资产，不是所有后续模型的全局策略。

原件检查见 [材质与 APK 审计](../../research/3d-validation/material-audit/README.md)、[审计 JSON](../../research/3d-validation/material-audit/audit.json)；SDK 实际导入属性和纹理覆盖率见 [公开 API 探针](../../research/3d-validation/material-api-probe/README.md)、[探针 JSON](../../research/3d-validation/material-api-probe/loaded-materials.json)。[glTF 规范](https://registry.khronos.org/glTF/specs/2.0/glTF-2.0.html#alpha-coverage) 说明不透明材质的常规光栅实现会写入深度，混合材质则依赖渲染器的深度与排序策略；这里的 SDK 行为已由公开 API 和真实渲染确认。

## 真实渲染验证

历史 `NativeModelOcclusionChecks.cs` 对原始导入场景与应用修正后的场景使用相同灯光和固定相机；[当前测试文件](../../tests/HeyMelody.ModelPreview.Tests/NativeModelOcclusionChecks.cs) 已改为 Free4。逐层反转 sibling 插入顺序，保持每个节点的父子关系、变换、默认 RenderOrder 和几何不变，检查遮挡是否依赖绘制顺序。每张图为真实 Ava3D 渲染的 700×500 像素；另保存真实 HWND 和完整主界面的截图。

| 相机方向 | 原始 BLEND 反转顺序后变化像素 | 修复后变化像素 |
|---|---:|---:|
| 正面，yaw 0° / pitch 15° | 18,403 | 0 |
| 斜角，yaw 45° / pitch −10° | 20,415 | 0 |
| 侧面，yaw 90° / pitch 25° | 18,268 | 8 |
| 背面，yaw 180° / pitch 40° | 23,428 | 0 |
| 合计 | 80,514 | 8 |

变化像素按 RGB 绝对差之和大于 12 计算。修复后侧面残余 8 个像素属于边缘或共面接缝差异，在允许的栅格化误差内；另外三面完全一致。目检同角度修复前后图，外壳正确遮住内部零件。全部 31 个场景项、98,367 个三角形仍参与渲染。

| 修复前 | 修复后 |
|---|---|
| ![原始混合材质](../../research/3d-validation/worktree/artifacts/model-occlusion-fix-final/occlusion-front-original-alpha-3d.png) | ![不透明材质与深度写入](../../research/3d-validation/worktree/artifacts/model-occlusion-fix-final/occlusion-front-corrected-3d.png) |

完整回归 **228/228 项通过**，涵盖运动、真实鼠标拖动、松手惯性、闲置自转、隐藏与最小化暂停、明暗主题及两种窗口尺寸、四方向遮挡。独立遮挡运行另有 69 项通过。实际后端为 OpenGL ES 3.0 / NVIDIA GeForce RTX 2060。构建 0 警告、0 错误。

- [完整回归报告](../../research/3d-validation/worktree/artifacts/model-occlusion-fix-final/report.json)
- [修复后暗色主界面](../../research/3d-validation/worktree/artifacts/model-occlusion-fix-final/main-dark-1280x900-window.png)
- [独立遮挡报告](../../research/3d-validation/worktree/artifacts/model-occlusion-fix-first/report.json)

报告所验证的应用 DLL SHA256 为 `f8234fcf3f2f253c49e12fc666785ec1892f74c243ff0c62ffdf196f3303e6bb`。测试窗口的 Opened 事件加了一次性启动保护，避免隐藏再显示时重复启动测试；该改动只作用于测试程序。

两份 OEM APK 的 Java 材质调用层只发现参数应用，没有发现深度、剔除或混合策略覆写。未检查 Filament 原生材质管线，因此不能声称本修复复制了 OEM 的内部不透明处理。其他 GPU 尚未验证。

以下复测命令对应历史 X3 源码，工作目录为当时的独立 worktree；当前源码会检查 Free4：

```powershell
dotnet build tests/HeyMelody.ModelPreview.Tests -r win-x64 -p:SelfContained=false -p:CopyDebugSymbolFilesFromPackages=false
dotnet run --project tests/HeyMelody.ModelPreview.Tests --no-build -r win-x64 -- --native-smoke --output-dir artifacts/model-occlusion-rerun
# 仅检查遮挡时将 --native-smoke 换为 --native-occlusion。
```
