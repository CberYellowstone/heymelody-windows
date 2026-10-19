# 0.7.1 Enco Free4 外观体验包

> 当前客户端仅使用欢律配置与资源，见[当前单源规则](heymelody-only.md)。下文涉及已退出支持来源的覆盖、接口和操作要求仅用于历史研究复核，已由当前规则取代；原版本、路径、摘要和验证范围保留。

本文为公开整理稿；[整理前完整原文](../../research/migrations/organization-2026-10-09/originals/docs/research/enco-free4-0.7.1.md)保存在本地研究档案，不随普通Git clone提供。历史版本、来源及验证范围保留，完整研究对象与产物由 `research/` 保存。

0.7.1 的 Free4 模型与交互已从独立分支 `experiment/enco-free4-3d-0.7.1` 迁入 `arch/modular-devices`。原独立成果提交为 `10c2c71`，基于此前已验证的 X3 预览提交 `e7db26b`，仍沿用 0.7.0 的设备控制架构。主界面左侧换用 Enco Free4 官方 GLB；App、Client、Contracts、Host 和 DeviceManager 的版本均为 0.7.1。

下文体验包的 259 项原生检查、338 项离线检查、ZIP 摘要与发布窗口来自迁移前的独立工作区；对应本地证据保存在 `research/3d-validation/worktree`。迁移后主仓库另行验证的结果见“主仓库迁移验证”。

## 运行

完整解压 [迁移前 Windows x64 体验包](../../research/3d-validation/worktree/release/HeyMelody_Windows_0.7.1_x64.zip) 后：

- `Start-3D-Demo.cmd`：立即查看 Free4 模型，右侧为 X4 模拟状态，无需连接耳机。
- `Start-HeyMelody.cmd`：正常客户端入口，保留 X4 控制功能、后台托盘及设备管理器。

包自带 .NET 运行时。模型支持左右上下拖动、沿松手方向继续的强阻尼惯性，以及空闲时 20°/秒水平自转（一周约 18 秒）。隐藏、最小化和关闭时暂停。Free4 本次只替换外观，设备协议与模块架构留待后续推进。

迁移后的源码可在仓库根目录运行 `Run-3D-Demo.ps1`，或双击 `Run-3D-Demo.cmd`。需要 .NET 10 SDK，右侧仍使用 X4 模拟数据。

ZIP 大小 **117,726,072 字节（约 112.3 MiB）**，SHA256 为 `830d7787664fbc59766f13829fc3f90685f6be7c7b739be811210f8e3091131f`。

## 原件与适配

使用此前取得的普通 Free4 `productId=068C10`、`color=1`、`moduleId=14` 资源。欢律与无线耳机返回相同 ZIP；本次没有重新请求云端。`color=1` 是成功查询的参数，不据此推断营销配色。

| 项目 | 值 |
|---|---|
| GLB 字节数 | 9,727,500 |
| GLB SHA256 | `c49540d3e5afd7af8bb5aa562d3dc00e35246361e6efc14fa142a749b7d22bcc` |
| 几何 | 28 个 mesh、67 个 node、93,875 个三角形 |
| 材质 | 唯一 `1001`，原件声明 BLEND，RGB 纹理实际完全不透明 |
| SDK | Ava3DControl 12.1.0-preview.16 |

应用在校验原件长度与哈希后加载新的独立场景，通过公开 API 将该已知资产的唯一材质设为 Opaque 并开启 DepthWrite。保持全部纹理、几何和原有正向缩放，相机按 SDK 的世界 bounds 适配。使用原有桌面光照，不声称已完整移植 OEM 的 IBL 或 skybox。

- [模型控件](../../src/HeyMelody.App/EncoFree4ModelView.cs)
- [资源获取记录](online-artwork/README.md)
- [Free4 原件审计](../../research/3d-validation/free4-material-audit/README.md)

## 迁移前独立工作区验证

真实 Windows / OpenGL ES 3.0 / RTX 2060 验证 **259 项通过**，覆盖拖动方向、松手惯性、20°/秒稳定自转、暂停恢复、明暗主题、两种主界面尺寸与四方向遮挡。渲染器提交全部 28 个场景项、93,875 个三角形。

四个固定相机角度反转 sibling 顺序，修复后仅 21 个像素变化，均在原有 0.3% 栅格误差范围内。原始与修复画面共 40,115 个像素不同，目检确认外壳正确遮住耳机柄和背面结构。Free4 网格具有 26 个不同世界原点，透明排序本来就较稳定，不能要求其原始画面像 X3 一样必须受插入顺序明显影响；测试继续严格检查修复后各角度的绝对稳定性。

- [原生渲染与运动报告](../../research/3d-validation/worktree/artifacts/free4-0.7.1-native-final/report.json)
- [暗色主界面](../../research/3d-validation/worktree/artifacts/free4-0.7.1-native-final/main-dark-1280x900-window.png)
- [修复前](../../research/3d-validation/worktree/artifacts/free4-0.7.1-native-final/occlusion-front-original-alpha-3d.png) / [修复后](../../research/3d-validation/worktree/artifacts/free4-0.7.1-native-final/occlusion-front-corrected-3d.png)

原有 9 个离线测试套件共 **338 项通过**：Protocol 46、Devices 13、Control 19、Daily 62、Probe 23、Host 24、Client 96、App 47、DeviceManager 8。未调用真实 Bluetooth，也未修改设备白名单。日志保留构建输出空间限制及恢复过程，最终各套件均通过。

- [前五套件日志](../../research/3d-validation/worktree/artifacts/free4-0.7.1-offline.log)
- [Host、Client 与 App 日志](../../research/3d-validation/worktree/artifacts/free4-0.7.1-offline-completion.log)
- [DeviceManager 日志](../../research/3d-validation/worktree/artifacts/free4-0.7.1-manager-tests.log)

最终 x64 包通过 GUI、DeviceManager、Host 的 PE 架构、自带运行时、入口哈希、图标与依赖许可检查，五个组件的 FileVersion 均为 `0.7.1.0`。逐文件读取 ZIP 并验证 CRC 与 SHA256，全部 **583 个文件**与发布目录一致；包内没有 PDB。发布的 `HeyMelody.dll` 包含恰好一份经哈希验证的 Free4 原始 GLB。

另从发布目录启动 `Start-HeyMelody.ps1 -ModelDemo`，确认标题为 `HeyMelody 0.7.1 · Enco Free4 三维模型验证`，实际窗口正常显示 Free4 模型。原生 259 项验证使用同源 Debug 构建；最终 Release 包另做启动、画面和完整性验证。本次未对真实耳机执行发布包协议回归。

- [静态验包报告](../../research/3d-validation/worktree/artifacts/free4-release-static.json)
- [压缩包、嵌入模型与版本完整性记录](../../research/3d-validation/worktree/artifacts/free4-0.7.1-package-verification.json)
- [发布包实际窗口](../../research/3d-validation/worktree/artifacts/free4-0.7.1-packaged-window.png)

## 在当前仓库构建

从仓库根目录执行：

```powershell
.\Build-Client.ps1 -Version 0.7.1 -Architectures x64 -SkipTests
python tools/verify_release.py --package release/HeyMelody_Windows_0.7.1 --output artifacts/free4-release-static.json --architectures x64
```

`-SkipTests` 用于已单独完成源代码检查的打包步骤；不代表未运行验证。打包脚本按各项目实际 NuGet 缓存解析许可，自带 Ava3D 与其他依赖声明，并移除运行不需要的 PDB。构建时可以使用 SDK 支持的硬链接减少重复文件占用，ZIP 始终保存完整独立文件内容。

## 主仓库迁移验证

迁移前已撤销原先未提交的模块化草稿，恢复原 X4 资源与控制架构。草稿保存在 Git stash `2e3c31e70ca52b6e330c93a8fbdb74e771454643`，没有纳入本次提交。

在 `arch/modular-devices` 的主仓库运行 Release / win-x64 构建，结果为零警告、零错误；重新运行完整原生模型与运动检查，**259 项通过**。原有九个离线套件重新运行，**338 项通过**，全部退出码为 0；没有调用真实 Bluetooth。此次未重新生成发布 ZIP，前述体验包仍是独立工作区的产物。

根目录 `Run-3D-Demo.ps1` 也已实际执行，通过 Debug 构建并打开标题为 `HeyMelody 0.7.1 · Enco Free4 三维模型验证` 的窗口。脚本每次先进行增量构建，避免启动迁移前的旧可执行文件；`-Rebuild` 可禁用增量构建。

主仓库构建与启动脚本使用 `artifacts/nuget-cache`，并可复用已有用户 NuGet 缓存。已去掉独立工作区的本地包源与仓库外缓存路径。运行时模型来自已提交的 App 资源，加载与渲染仅位于展示层。

- [Release 构建日志](../../artifacts/3d-migration-071/model-build.log)
- [主仓库原生模型报告](../../artifacts/3d-migration-071/native/report.json)
- [主仓库离线套件汇总](../../artifacts/3d-migration-071/source-tests.json)
- [主仓库界面回归](../../artifacts/3d-migration-071/app-ui/validation.json)
- [根目录启动验证](../../artifacts/3d-migration-071/root-launcher-window.json)

**资料归档（2026-10-08）：** 本文访问链接与复现路径已按新目录更新；研究时的原始位置为仓库根 `research/`，原报告与 manifest 内路径保留不变。新旧映射及提交边界见 [资料归档入口](README.md)。
