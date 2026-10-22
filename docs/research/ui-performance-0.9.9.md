# 界面性能与交互（0.9.9 审计轮）

**2026-10-22 在 review/0.9.9 分支完成界面性能方向一轮优化：合成器改用区域脏矩形，界面操作时让装饰动画降到 30 fps，模型帧少一次整帧复制，Lottie 末帧缓存为 GPU 图像，事件驱动回收 GPU 缓存；顺带修复 EQ 竖直滑块被裁，并把动态设置行与 EQ 选择改为与降噪一致的非阻塞排队。** 起点提交 `a13e236`。所有结论来自源码审计、对 Avalonia 12.1.4 与 FluentAvaloniaUI 3.1.0 程序集的反编译核实以及 Linux 无头测试；**本轮没有在 Windows 或骁龙 8cx 设备上测量帧率**。

## 问题与依据

用户反馈：在 GPU 较弱的设备（骁龙 8cx Gen3）上，展开设置卡等基本操作明显掉帧；没有三维模型、只播放 Enco X4 Lottie 时同样如此。

| 发现 | 依据 | 影响 |
| --- | --- | --- |
| Avalonia 默认把一帧中所有脏区并成一个包围矩形 | `CompositionOptions` 各项默认 null，`ServerCompositionTarget` 仅在 `UseRegionDirtyRectClipping == true` 时用多矩形/区域跟踪，否则 `SingleDirtyRectTracker`（`FullUnion`） | 左侧主图动画（模型、Lottie、WebP）与右侧任何变化（展开、悬停画刷过渡）合并成近乎整窗重绘，弱 GPU 上每帧重画全部文本与卡片 |
| 三维模型默认按刷新率空闲自转 | `EncoFree4ModelView.OnFrame`、`ModelOrbitMotion.IdleYawSpeed`、默认帧率“无限制” | 每帧 GPU 渲染（物理像素、4×MSAA）、回读、UI 线程两次整帧复制、Skia 重新上传纹理 |
| `WriteableBitmap` 每次修改生成新 `SKImage` | `WriteableBitmapImpl.Draw` → `SKImage.FromPixels` | 每帧一次新纹理上传；锁定位图即使不写入也会触发 |
| Avalonia.Skia 从不清理 GPU 缓存 | 只在创建上下文时 `SetResourceCacheLimit`，无 purge 调用 | 256 MiB 预算长时间运行后只靠超限逐出（待办 UI-02） |
| Lottie 每个合成帧完整执行 Skottie | `LottieHandler.OnAnimationFrameUpdate`/`OnRender` | 0.9.0 为 120 Hz 平滑有意如此；与整窗重绘叠加时代价最大 |
| FASettingsExpander 展开没有动画 | 模板只把 `ExpanderContent` 设为可见，仅有 83 ms 的标题画刷过渡 | 0.9.0 文档所写“`TranslateTransform.Y` 关键帧 333 ms”不成立；展开是一次布局与新内容的首次光栅化 |
| EQ 竖直滑块右侧被裁 | FA 竖直模板列为 14 + 18 + 14 = 46 px，放进 32 px 槽后拇指在 14..32，悬停放大的椭圆到 33.5 | 拇指偏右 7 px 并越出右缘 1.5 px（无头几何测试复现） |
| 设置行在写入待确认时全部禁用 | Feature 行 `CanEdit` 含 `!_pending.ContainsKey("Feature")`；EQ 在同组写入期间的点击被 `ApplyWithResultAsync` 静默丢弃 | 与降噪的非阻塞体验不一致，EQ 还会丢失点击 |

## 实施

| 项目 | 改动 | 说明 |
| --- | --- | --- |
| 区域脏矩形 | `RenderingSetup` 加 `CompositionOptions { UseRegionDirtyRectClipping = true, MaxDirtyRects = 8 }` | 三个 GUI 入口共享；Skia 报告支持区域，重绘按区域裁剪 |
| 动画让路 | 新增 `HeyMelody.Artwork.AnimationYield` 与 `App.AnimationYieldTriggers`：任一设置卡展开/收起脉冲 600 ms、设置区滚动 300 ms、主窗口失活期间保持 | 按用户选择的“仅让路”方案：模型只在空闲自转时改用 30 fps 节拍，拖动与惯性不变；Lottie 时间连续推进、只在 30 fps 步进变化时重绘；无空闲定时器 |
| 模型帧复制 | 渲染器直接写入锁定的 `WriteableBitmap`，托管缓冲逐行复制仅作回退；只在请求新帧后轮询，最后一次请求后保留 0.25 s 宽限 | 省一次整帧复制，并避免无新帧时锁定位图引发的重新上传；宽限保证重叠请求时最后姿态仍呈现 |
| Lottie 末帧缓存 | 停在末帧且有 GPU 上下文时渲染一次到 `SKSurface`，之后绘制 `SKImage` | 尺寸、缩放、播放状态变化即失效；循环动画不缓存；软件后端照旧 |
| GPU 缓存回收 | 新增 1×1 的 `GpuJanitor` 组合视觉：让路结束或窗口失活后，最多每 10 s 在渲染线程调用 `GRContext.PurgeUnusedResources(30 s)` | SkiaSharp 3.119.4 无 `PerformDeferredCleanup`；上限仍为 256 MiB |
| EQ 滑块 | 频段滑块局部把 `SliderPreContentMargin`/`SliderPostContentMargin` 设为 7 | 7 + 18 + 7 = 32，拇指 7..25、放大椭圆 5.5..26.5，均在滑块与各级祖先内 |
| 非阻塞设置 | 新增 `MainViewModel.WriteQueue.cs`：Feature 行按行、EQ 预设与自定义条目共用一个槽位，只保留最后目标 | 规则与降噪相同：一次一项；发送前校验设备、会话、后台实例、连接代次与资源版本；回读已满足则不发 SET；前一项未确认即丢弃全部排队目标并恢复观察值，不重试不重放；生命周期变化清空队列 |
| UI-01 | `ReadOnlyPresentation` 改用 `UiSnapshot.Clone()` | 既有 Clone 与 JSON 往返的逐字节等价检查覆盖 |

UI-03（FluentAvalonia 带入 DataGrid/ColorPicker）经反编译确认由主题资源字典无条件以类型为键注册，没有受支持的排除方式，本轮不实施，见[优化待办](../optimization-backlog.md)。

## 行为变化

- 设置行与 EQ 选择在写入待确认时保持可用。排队目标在显示上立即生效；被丢弃时恢复耳机观察值。
- 第一次点击若与当前回读一致（例如重选正在使用的预设），不再发送 SET；与降噪一致。
- 窗口失活、展开设置卡或滚动时，模型空闲自转和 Lottie 短时以 30 fps 绘制。

## 验证范围

Linux x64 无头环境（2026-10-22，.NET SDK 10.0.401）：App 129→136（新增 EQ 滑块几何 1 项、帧复制与让路 6 项）、Client 181→190（新增非阻塞排队 9 项）全部通过；DeviceManager 9、Calibration.UI 13、Calibration 55、Protocol 77、Resources 40、Devices 21、Control 20、Daily 177 数量不变、全部通过；Host.Tests 以 `-p:EnableWindowsTargeting=true` 运行 110 个 PASS 行；ModelPreview 默认运动检查 180 项通过；`HeyMelody.App` Release 编译 0 警告 0 错误。既有测试的断言未放宽。EQ 滑块裁切由无头几何检查在改动前复现、改动后通过；30 fps 让路在无头环境中节拍慢于 30 fps，只验证了门控逻辑而非实际节奏。

未验证：Windows 显示器（含 8cx Gen3）上的实际帧率与帧时间、区域脏矩形与 Win32 交换链的实际提交范围（待办 UI-06）、30 fps 让路的观感、Lottie GPU 末帧缓存与 `GpuJanitor` 在真实 GPU 上的效果、显存占用、`--native*` 原生窗口检查、Windows 离线检查，以及真实耳机上的排队写入。验收时设置 `HEYMELODY_RENDER_DIAGNOSTICS=1` 或按 Ctrl+Shift+F12，在播放 Lottie 或模型自转时展开/收起设置卡、滚动设置区，比较叠加层的帧时间曲线。

反编译证据与方法存放在本地[研究档案](../../research/ui-perf-0.9.9-20261022/README.md)，通常不随 Git clone 提供。
