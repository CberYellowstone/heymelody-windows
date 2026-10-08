# 三维耳机模型渲染设置

0.8.4 新增应用设置中的“三维模型 GPU 加速”，默认关闭。关闭时，所有耳机模型（包括在线 GLB、普通 Free4 和丹拿版演示）明确使用 Ava3D 的 `RenderBackendKind.Software`，保留三维显示、自动旋转和鼠标拖动。该值在控件附加到窗口之前设置，避免默认模式初始化三维模型的 GPU 后端。

开启后使用 Ava3D 的 `Automatic` 选择，优先使用当前平台提供的 GPU 后端；平台不提供 GPU 时仍可退回软件渲染。设置只在点击“保存”后应用，并在当前模型的下一帧生效；取消不会启动 GPU 后端。新建、切换设备和重新附加的模型均使用已保存选择，无需重启。该开关控制耳机三维模型，与 Avalonia 的普通界面合成独立。

主题与此偏好共用 `%LOCALAPPDATA%/HeyMelody/preferences.json`，键为 `modelGpuAcceleration`。旧版只有主题的文件、缺失文件、损坏 JSON 或非布尔值均按关闭处理。保存通过同目录临时文件替换，保留已有主题与其他字段；主题单独更新也不会清除 GPU 选择。

软件渲染使用同一官方 GLB 和场景，无须借用其他设备图片。Ava3D 12.1.0-preview.16 的随包文档说明，软件后端按顶点照明，仅采样基础色贴图且没有边缘抗锯齿，因此外观和性能可能与 GPU 模式不同。接口依据为该固定版本的 `Ava3D.xml` 中 `Ava3DView.PreferredBackend` / `RenderBackendKind.Software`，以及随包 `README.md`；上游入口为 [Ava3DControl](https://github.com/pavel-zheltiakov/Ava3DControl)。

针对性验证：

```powershell
dotnet run --project tests/HeyMelody.App.Tests -- --output-dir artifacts/gpu-ui-validation
dotnet run --project tests/HeyMelody.ModelPreview.Tests -- --native-software-smoke --output-dir artifacts/gpu-software-validation
```

前者检查旧偏好升级、安全默认、保存/取消、当前与后续模型、重新附加以及无关偏好保留。后者在独立 Win32 测试窗口中核对实际 `Software` 后端、丹拿版的 20 个 mesh / 94,927 个三角形、完整模型像素、旋转与卸载；产出渲染 PNG 和 JSON 收据。测试不调用蓝牙、不下载资源、不写用户偏好。ARM64 / 骁龙 8cx Gen 3 的实机兼容性仍需在该硬件复测。

2026-10-09 本机 Windows x64 软件渲染验证通过 125 项（115 项运动检查和 10 项原生软件检查）。实际后端为 `Software`、renderer 为 `Skia`，源模型保留 20 个 mesh 和 94,927 个三角形；CPU 渲染诊断计数为当帧背面裁切后约 48,145 个三角形，不应直接套用 GPU 提交全部三角形的计数。640×580 渲染图已目检，有完整可见耳机模型；旋转、停止、卸载和窗口关闭正常。Avalonia 界面合成仍可以使用 GPU，这与模型选择软件渲染并不冲突。
