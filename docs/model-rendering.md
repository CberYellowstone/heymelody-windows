# 三维耳机模型渲染设置

0.9.0 取消三维模型的固定帧率。0.8.x 用 1/30 s 的 `DispatcherTimer` 驱动旋转，并把 Ava3D 的 `MaxFrameRate` 设为空闲 30、拖动 60；Win32 计时器约 15.6 ms 的粒度还会让帧间隔不均匀。现在改用 `TopLevel.RequestAnimationFrame` 的逐帧回调，与显示器的合成节拍对齐，`MaxFrameRate` 为 0（不设上限），`RenderTrigger` 仍为 `OnDemand`：每个显示帧推进一次已按时间积分的 `ModelOrbitMotion`，只有姿态变化时才 `InvalidateCamera`，高回报率鼠标的多次移动在下一帧合并应用。隐藏、最小化或卸载时循环停止，`AnimationActive` 表示循环是否在运行。

应用设置新增“三维模型帧率”：跟随显示器（默认）、60 帧或 30 帧，保存在 `preferences.json` 的 `modelFrameRate`（`display`／`60`／`30`，缺失或未知值按跟随显示器）。GPU 后端按所选间隔均匀跳帧；CPU 后端仍保持 0.8.x 的空闲 30 帧，拖动时至多 60 帧，并在单帧渲染耗时较长时按 1.3 倍渲染时间继续放慢，因为 CPU 渲染发生在整窗的帧内，过快会拖慢其他动画。4×MSAA、阴影贴图与光照参数不变。

重新附加模型（切换设备或布局后）复用最近两份已校验的 GLB 字节，不再重复读取与 SHA-256；每个视图仍解析自己的独立场景。软件材质副本改为第一次实际使用 CPU 后端时才在后台生成，GPU 模式不再解码和采样全部贴图。设 `HEYMELODY_RENDER_DIAGNOSTICS=1` 或按 Ctrl+Shift+F12 可显示帧率／帧时间叠加层，模型左上角另显示 Ava3D 的帧率、P95 帧时间、渲染耗时、后端、MSAA 与迟到帧；只有画面在动时数字才有意义。0.9.0 的这些改动已通过 Linux 无头测试与运动检查，**尚未在 Windows 显示器上实测 120 Hz**，完整依据见[界面与渲染性能](research/ui-performance-0.9.0.md)。

Enco X3 白色模型的半透明外壳问题沿用已验证的 Free4 方案修复：导入后，仅对 SHA256 匹配的已知不透明资产，将材质 `1001` 设为 `Opaque` 并开启深度写入。白色 GLB 原先未命中已有规则；黑色 GLB 已被覆盖，本轮补充其回归。修正规则统一放在内部 `ModelMaterialCorrections`，模型与贴图原件不改写，也不按型号名称全局覆盖透明材质。本轮仍为 0.9.2；静态依据、外部模型复测命令与验收范围见 [X3 白色遮挡修复与黑色回归](research/enco-x3-white-opaque-occlusion.md)。

0.8.6 恢复默认 GPU 优先：没有偏好、只有主题的旧文件、损坏 JSON 或非布尔值时开启“三维模型 GPU 加速”，请求 Ava3D 的 `Automatic` 后端。已保存的明确 `false` 继续使用软件；已保存的 `true` 继续开启。GPU 不可用时的自动软件回退及 0.8.5 材质／照明修正保留。

0.8.5 修正软件后端的模型过亮与曲面层次不足。固定版本的 CPU 渲染器仅逐像素读取基础色贴图，原模型中由其他贴图调节的金属度、粗糙度和发光系数因此被当作均匀参数使用。应用在每个网格的实际 UV 上采样，并按三角形世界表面积加权估算这些贴图的代表值，放入独立的软件材质副本，并降低软件环境光与直接光，保留曲面明暗。

软件副本保留原基础色贴图，模型和贴图文件不改写。GPU 使用原始材质引用与原有光照；`Automatic` 实际退回 CPU 时也应用软件方案。该方案不增加逐像素法线、AO 或完整材质贴图渲染，视觉效果仍与 GPU 和手机端不同，具体依据见 [软件模型材质研究](research/software-model-materials.md)。

代表值采样使用最近邻，不承诺与 `Texture.Filter` 的线性过滤一致；显式 `Srgb` 数据 MR 图的 gamma2.2 转换也只是固定依赖近似，可能不同于标准分段曲线。当前官方模型的 Auto MR 仍按线性数据读取。

当前软件光照为 `EnvironmentLight.Studio(.06)`、方向 `normalize(-4,-2,-1)`、直接光强度 `1.5`、ambient `.02`。GPU 保留 Studio `1.2`、方向 `normalize(-1,-2,-3)`、直接光 `2`、ambient `.15`。较弱的环境光配合方向光保留曲面明暗，避免将照明颜色提前推至饱和。

0.8.4 新增应用设置中的“三维模型 GPU 加速”，在 0.8.4／0.8.5 默认关闭，0.8.6 起默认开启。关闭时，所有耳机模型（包括在线 GLB、普通 Free4 和丹拿版演示）明确使用 Ava3D 的 `RenderBackendKind.Software`，保留三维显示、自动旋转和鼠标拖动。已保存的软件选择仍在控件附加到窗口前应用。

开启后使用 Ava3D 的 `Automatic` 选择，优先使用当前平台提供的 GPU 后端；平台不提供 GPU 时仍可退回软件渲染。设置只在点击“保存”后应用，并在当前模型的下一帧生效；取消不会启动 GPU 后端。新建、切换设备和重新附加的模型均使用已保存选择，无需重启。该开关控制耳机三维模型，与 Avalonia 的普通界面合成独立。

主题与此偏好共用 `%LOCALAPPDATA%/HeyMelody/preferences.json`，键为 `modelGpuAcceleration`。0.8.6 只有明确布尔值 `false` 按关闭处理；缺失或无效值使用开启的默认值。保存通过同目录临时文件替换，保留已有主题与其他字段；主题单独更新也不会清除 GPU 选择。

软件渲染使用同一官方 GLB，无须借用其他设备图片。Ava3D 12.1.0-preview.16 的随包文档说明，软件后端按顶点照明，仅采样基础色贴图且没有边缘抗锯齿，因此外观和性能可能与 GPU 模式不同。接口依据为该固定版本的 `Ava3D.xml` 中 `Ava3DView.PreferredBackend` / `RenderBackendKind.Software`，以及随包 `README.md`；上游入口为 [Ava3DControl](https://github.com/pavel-zheltiakov/Ava3DControl)。

针对性验证：

```powershell
dotnet run --project tests/HeyMelody.App.Tests -- --output-dir artifacts/gpu-ui-validation
dotnet run --project tests/HeyMelody.ModelPreview.Tests -- --native-software-smoke --output-dir artifacts/gpu-software-validation
```

前者检查旧偏好升级、安全默认、保存/取消、当前与后续模型、重新附加以及无关偏好保留。后者在独立 Win32 测试窗口中核对实际 `Software` 后端、丹拿版的 20 个 mesh / 94,927 个三角形、完整模型像素、旋转与卸载；产出渲染 PNG 和 JSON 收据。测试不调用蓝牙、不下载资源、不写用户偏好。ARM64 / 骁龙 8cx Gen 3 的实机兼容性仍需在该硬件复测。

0.8.4 的历史验证：2026-10-09 本机 Windows x64 软件渲染验证通过 125 项（115 项运动检查和 10 项原生软件检查）。实际后端为 `Software`、renderer 为 `Skia`，源模型保留 20 个 mesh 和 94,927 个三角形；CPU 渲染诊断计数为当帧背面裁切后约 48,145 个三角形，不应直接套用 GPU 提交全部三角形的计数。640×580 渲染图有完整可见耳机模型；旋转、停止、卸载和窗口关闭正常。这些检查没有覆盖材质明暗质量，不能作为 0.8.5 修正的验证。Avalonia 界面合成仍可以使用 GPU，这与模型选择软件渲染并不冲突。

0.8.5 历史 Windows x64 原生软件检查通过 151 项，收据与五张截图保存在 [software-lighting-085-20261009b](../artifacts/software-lighting-085-20261009b/report.json)。实际后端为 `Software`，丹拿版保留 20 个 mesh、94,927 个源三角形，四张原 PNG 摘要及软件／GPU 材质引用往返恢复均通过；材质往返没有初始化 GPU 设备。固定前盒区域的亮度 P90−P10 为 `18.2126`，旧平面图为 `5`；左右改光使约 `38.429%` 的模型内部像素变化，移除基础色贴图使约 `97.398%` 变化，证明光照与基础色实际影响图像。浅色／深色截图均已目检，曲面与耳机内托可辨。

这些原生截图检查对象是丹拿版；普通 Free4 与黑色 X3 另以离线真实 Preview 目检，不能称作三款都完成原生验证。X3 仍偏暗，受 CPU 先计算照明再乘基础色的近似限制。PNG 摘要证明法线图被加载，不证明 CPU 逐像素使用法线图。

发布后将 x64 `HeyMelody.dll` 与 `Ava3D.dll` 加载到独立 model harness，再次通过 [151 项原生检查](../artifacts/software-lighting-085-package-20261009/report.json)，程序集摘要与发布文件匹配，得到相同的亮度和贴图／光照对照结果。这验证发布的 GUI／renderer 程序集在模型测试窗口中的行为，没有完整启动客户端 exe、Host 或蓝牙。

0.8.5 的 13 个默认 runner、三架构发布及两套静态校验均完成，见 [构建记录](../REPOSITORY.md)、[PowerShell 收据](../release/HeyMelody_Windows_0.8.5_STATIC-VALIDATION.json) 与 [Python 收据](../artifacts/release-085-20261009/static-python.json)。ARM64 与骁龙 8cx Gen 3 仍未实机验证。

0.8.6 的 App 回归继续覆盖 GPU 默认开启、明确 `false` 的保留、保存／取消及当前／新建／重新附加模型。独立 [原生演示检查](../artifacts/native-demo-086-agent-20261009a/native-demo-receipts.json) 通过 40 项，以未写入的临时偏好隔离用户设置：请求 `Automatic`，本机实际使用 OpenGL GPU，丹拿版保留 20 个 mesh、94,927 个三角形，并捕获浅色／深色与宽窄窗口的四张真实模型帧。另行 [原生软件检查](../artifacts/software-lighting-086-20261009/report.json) 显式关闭 GPU，通过 151 项，前盒亮度跨度、改光和移除基础色的对照值与 0.8.5 相同。这两项是 Windows x64 源码测试窗口验证，不是 ARM64、真实耳机或本轮发布客户端完整执行记录。
