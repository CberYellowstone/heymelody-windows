# 软件三维模型材质与光照复核

0.8.5 修正默认软件三维展示的过亮与曲面层次不足：按每个网格的 UV 贴图样本估算金属度、粗糙度和发光参数，以三角形世界表面积加权；使用独立的软件材质副本与较低光照。原始 GLB、基础色贴图和 GPU 材质引用保留。设置行为见 [三维模型渲染设置](../model-rendering.md)，构建及发布验证见 [REPOSITORY.md](../../REPOSITORY.md)。

## 固定依赖与原件证据

本轮使用 **Ava3DControl 12.1.0-preview.16**，没有替换渲染库。其随包 `lib/net10.0/Ava3D.xml` SHA256 为 `aa7ec71d932c26a1d55cda4c592f31f2044f3d34b76ece536b8ceb8d9e255aeb`。相关成员原文与摘要保存在 [pinned-api-evidence.json](../../research/software-material-audit-20261009/pinned-api-evidence.json)；完整本地包位于 `%USERPROFILE%/.nuget/packages/ava3dcontrol/12.1.0-preview.16/`。

`SkiaBackend`、`OfflineQuality.Preview` 和 `Texture.Average` 的文档明确说明：实时 CPU 后端沿用 PBR、环境光、方向光与 tone map，但按顶点计算照明，逐像素仅读取基础色贴图。金属度／粗糙度、法线、发光和遮蔽贴图没有相同的逐像素采样；金属度／粗糙度仅在纹理已提供 `Average` 时有均值补偿。GLB 导入的这些纹理没有预填该均值。CPU 还先将照明折入顶点颜色，再乘基础色贴图，因此不能直接复用 GPU 的所有视觉参数。

[独立原件审计](../../research/software-material-audit-20261009/material-audit.json)检查了三份 GLB 的 JSON、内嵌 PNG 和实际 UV 区域。三份都只有材质 `1001`，金属度与粗糙度标量缺省为 1，显式 `emissiveFactor=[1,1,1]` 搭配发光贴图：

| 模型 | 原始 GLB SHA256 | 特征 |
| --- | --- | --- |
| 普通 Free4 | `c49540d3e5afd7af8bb5aa562d3dc00e35246361e6efc14fa142a749b7d22bcc` | 基础色、金属度／粗糙度、法线、发光贴图各自有效 |
| Free4 丹拿版 | `7f85c66d397ee45163246ff745c37e52a70059e739be0af2283a30bfb689c2f6` | 原基础色贴图有 DYNAUDIO 标记，模型与普通版独立 |
| Enco X3，`067410 / 2` | `6f6ab09e3b5067d3f1d4fac732243bb5fe723e3eb982d9b84e629560e5813363` | 原发光贴图全部为黑色；白色 factor 本身不代表应当发光 |

## 为何发光与光照变平

glTF 的材质值由贴图与 factor 共同决定。金属度／粗糙度图的 B/G 通道分别承载线性数值，发光纹理是 sRGB 颜色，参与计算前需解码后再乘发光 factor。依据是 [Khronos glTF 2.0 材质规范](https://registry.khronos.org/glTF/specs/2.0/glTF-2.0.html#materials)；这些语义不能从某个标量单独判断。

CPU 后端不读取原发光图时，直接采用白色发光 factor，会把本应受到暗色甚至全黑贴图抑制的表面渲染成均匀发光。金属度和粗糙度也被误作整块材质都是 1。此前同时采用较强的 Studio 环境光、方向光和 ambient，照明先于基础色贴图饱和，削弱曲面明暗。0.8.4 的 [软件截图](../../artifacts/gpu-software-validation-20261009f/dynaudio-software.png)证明模型几何可见，但外壳近似统一灰面；旧 [GPU 截图](../../artifacts/demo-dynaudio-native-20261008-gpu/demo-dynaudio-dark-1280x900-3d.png)也不等于手机端官方光照效果。

官方丹拿场景使用独立 IBL 与 OEM 材质覆盖，[scene-config.json](../../research/online-artwork/enco-free4-dynaudio/scene-config.json)记录直接光强度 0、IBL 强度 31500、材质 metallic=.72、roughness=1、reflectance=.9。App 没有导入该 OEM 环境，不能把这些数值直接套入另一引擎并声称复刻。

## 0.8.5 的软件近似

[SoftwareModelMaterials.cs](../../src/HeyMelody.App/SoftwareModelMaterials.cs)在新模型仍离开 UI／渲染线程时解码原贴图，为每个 mesh 克隆材质。每个网格均匀选取至多 2048 个三角形，每个三角形读取重心和三个内部点；样本通过真实 UV、贴图变换与 wrap 规则取得，以 `node.WorldTransform` 后的三角形表面积加权。这样避免平均整个图集把未使用区域或其他零件混入当前网格，也避免细分更密的区域仅因顶点多而占更大权重。

MR 的 G/B 均值分别乘原 roughness／metallic factor；发光 RGB 样本先线性化，再求均值并乘原 factor。颜色转换遵循固定渲染库的 `SkiaBackend.SrgbToLinear(Vector3)` 文档：采用各通道 `pow(value, 2.2)`，与其 GPU shaders 一致。这是该依赖的 gamma 近似，**不是标准分段 sRGB 曲线**；原件审计中的 `srgbLinearMeans` 同样采用此近似。

采样支持 UV0／UV1、各贴图的 KHR texture transform、材质／节点 UV 仿射变换，以及 Repeat／Clamp／Mirror。代表值读取采用最近邻 texel，不复刻 `Texture.Filter` 的线性过滤。当前 OEM 的 Auto MR 按线性数据读取；如果某个数据图显式标为 `Srgb`，helper 仍采用 pow2.2，与 SDK 部分数据转换路径的标准分段曲线存在小偏差，不能宣称任意色彩空间或 sampler 都与 SDK 精确一致。最多为 256 个网格采样；原尺寸解码不下采样，每张图限制 4,194,304 像素、单边 8192、编码数据 32 MiB，新分配的解码缓存总量限制 32 MiB。不支持的程序投影、缺失或非法 UV、超预算和无法解码的贴图保留原标量，诊断给出对应状态。

软件后端使用这些副本，并以 Studio `.06`、方向 `normalize(-4,-2,-1)`、直接光强度 `1.5`、ambient `.02` 保留明暗；`Automatic` 实际选择 CPU 时采用相同方案。GPU 恢复原始材质对象与原有 Studio `1.2`、方向 `normalize(-1,-2,-3)`、直接光 `2`、ambient `.15`。原模型与基础色字节不变，全套几何、拖动、惯性与空闲自转继续使用同一模型。

## 视觉验证与限制

材质代表值仍是每个网格的常量，不能替代逐像素 MR／发光变化，也不能新增法线和 AO 贴图、恢复细小高光或边缘抗锯齿。它修正缺失贴图导致的参数失真，并改善基础轮廓，不保证与 GPU 或手机一致。

丹拿标记确实在 [原基础色纹理](../../research/online-artwork/dynaudio-independent-audit/dynaudio-image-0.png)。其 UV 约为 `u=.37、v=.53–.62`，对应 mesh13、世界位置约 `y=1.72、z=-11.4`，表面法线接近 `(0,-1,-.028)`。正面盒体会遮挡该面，默认正面图没有可读字样不能证明贴图丢失；单独核对标记需使用能看见该面的后方倾斜角度。

复现当前源码检查时使用新的输出目录：

```powershell
dotnet run --project tests/HeyMelody.App.Tests -c Release -- --output-dir artifacts/software-material-ui-new
dotnet run --project tests/HeyMelody.ModelPreview.Tests -c Release -- --native-software-smoke --output-dir artifacts/software-material-native-new
```

App 检查材质代表值、贴图保留与软件／GPU 引用恢复；原生测试检查实际 CPU 后端、软件光照与可见模型。截图需额外核对浅色／深色主题下的曲面层次、轮廓和标记，而不能只统计 mesh 或前景像素。0.8.4 的 125 项运动／原生检查保留历史范围，不作为本轮视觉修正的结论。

2026-10-09 本轮 Windows x64 原生软件检查 **151 项通过**，见 [report.json](../../artifacts/software-lighting-085-20261009b/report.json) 与 [software-lighting-receipts.json](../../artifacts/software-lighting-085-20261009b/software-lighting-receipts.json)。丹拿版实际后端为 `Software`，20 个 mesh、94,927 个源三角形保留；四张原 PNG 摘要一致。软件／GPU 材质引用往返恢复通过：检查独立调用 `SoftwareModelMaterials.Apply(false)/true`，没有初始化 GPU 设备或验证 GPU 后端画面。浅色 [默认图](../../artifacts/software-lighting-085-20261009b/dynaudio-software.png) 和 [深色图](../../artifacts/software-lighting-085-20261009b/dynaudio-software-dark.png)均目检了耳机、内托与盒体明暗。

固定前盒 ROI `(x=.34–.66, y=.62–.70)` 的亮度 P90−P10 为 `18.2126`，旧平面图为 `5`，测试最低要求 `12`。左右改光有 `38.429%` 的模型内部像素变化；移除基础色贴图有 `97.398%` 变化。这分别证明方向光与基础色参与实际输出，不把法线图摘要校验冒充逐像素法线采样。

本轮原生对象仅为丹拿版。普通 [Free4](../../artifacts/software-material-probe-085-free4/dir-4_-2_-1-intensity1.5.png) 和黑色 [X3](../../artifacts/software-material-probe-085-x3/dir-4_-2_-1-intensity1.5.png) 另做离线真实 Preview 目检；X3 仍较暗，符合 CPU 先照明再乘基础色的局限，不能把本轮原生结果扩展到全部模型。

发布后将 x64 `HeyMelody.dll` 和 `Ava3D.dll` 复制到独立 model harness，再次通过 [151 项原生检查](../../artifacts/software-lighting-085-package-20261009/report.json)，程序集摘要分别为 `ee931c4ae24e1e6170de49dc588388a8a4aff33ca921114c09f2d8079067552b`、`f4f88fefb1f634d16fe441aba26a93a5bee1bfcc2e54f90bd033fec13e16a50c`，与发布文件匹配。该次检查复现前盒亮度 span `18.2126`、改光像素变化 `38.429%` 和移除基础色变化 `97.398%`，证明本次发布的 GUI／renderer 程序集在模型测试窗口中的行为；没有完整启动客户端 exe、Host 或蓝牙。

0.8.5 完整构建成功：13 个默认 runner 通过，12 个应用／协议 harness 共 516 项编号检查，另有 Windows 离线 ABI／transport 检查；x64、x86、ARM64 共 15 个入口程序集发布成功。两套静态校验通过，PowerShell 检查 8,507 条 ZIP 文件路径／大小、7,232 个关键摘要及 9 个嵌入美术字节比对，Python 检查三架构。收据见 [汇总](../../artifacts/release-085-20261009/validation-summary.json)、[PowerShell](../../release/HeyMelody_Windows_0.8.5_STATIC-VALIDATION.json) 和 [Python](../../artifacts/release-085-20261009/static-python.json)。这些结果不代替 ARM64 或骁龙 8cx Gen 3 的实机运行。
