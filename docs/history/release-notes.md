# 版本记录

## 当前开发分支：欢律单源（2026-10-18，未发布）

当前源码仍为 0.9.2。仅保留欢律配置、资源获取和界面设置，删除另一来源的配置、认证项、独占美术与复制图标；共有模型、欢律扫描图和官方听感试听保留。旧首选项保留有效欢律设置，展示缓存迁移至 schema 2；公共 IPC 升至 schema 4，更新须使用完整客户端并退出旧后台。能力门控、固件仅提醒及连接代次规则继续保留。

当前规则与验证边界见[欢律单源说明](../research/heymelody-only.md)。旧研究和许可按原始范围保全，下列历史数量不作为当前覆盖保证。

2026-10-19：顶部电量区域补回左耳、右耳和耳机仓标识，使用已核实来自欢律 17.6.5 电量控件的矢量形状，沿用当前界面前景与透明度。三种界面几何不计入设备美术的 365 条／635 文件闭包，来源与适配边界见[电量图标来源](../research/battery-device-icons.md)。产品版本不变，尚未发布。

## 历史版本

以下原段整理自 2026-10-11 文档重整前的根目录 `README.md` 与 `REPOSITORY.md`，保留原版本、日期、设备、平台及验证范围，仅调整链接位置。原段中的 “current”、默认行为与检查数量属于对应历史版本，不作为后续版本的保证。两个来源有重叠时分别列出；原文未给验证日期的条目不补写日期。

历史 `research/`、`artifacts/`、`release/` 链接指本地研究证据或发布产物，通常不随普通 Git clone 提供。完整原稿和摘要清单见本地[本轮整理档案](../../research/documentation-entrypoints-2026-10-11/README.md)；缺少该档案不影响阅读下列公开原段。现行构建与验证入口见[开发说明](../development.md)。

本页收录 0.8.11–0.9.2。更早记录分为[0.8.8–0.8.10](release-notes-0.8.8-0.8.10.md)、[0.8.4–0.8.7](release-notes-0.8.4-0.8.7.md)和[0.8.3 及更早版本与校准实测背景](release-notes-early.md)。项目早期阶段的独立记录仍见[历史索引](README.md)。

## 0.9.2（Windows 本地构建）

2026-10-11，针对 Enco Air4 Pro 的控制连接不可用反馈核查并修复 Windows SDP 缓存缺失、歧义及过期通道可能阻止建连的问题。实际 SDP 查询仍只使用所选来源允许的 UUID，要求唯一有效通道，并保持身份、能力与回执校验。连接失败保留具体步骤、命令号或 Windows 错误码，Daily、Host 和主界面显示相同诊断，成功恢复后清除。没有增加 UUID 猜测、固定通道或设置重放；配置与原 App 路由依据见[Air4 Pro 控制连接核查](../research/enco-air4-pro-control.md)。

本轮按用户授权在 Windows 本地构建 0.9.2，未发布到 GitHub。第三次完整构建退出 0，十三个默认 runner 全部通过，共 719 项数值用例及 Windows x64 SDP/ABI 离线检查五组 PASS，包括 App 106 项、Daily 139 项、Client 163 项、Host 84 项；完成 x64、x86、ARM64 共十五个 publish。完整流程零编译错误，保留一个既有 App 测试 CS0067 未使用事件警告。合并 ZIP `release/HeyMelody_Windows_0.9.2_AllArchitectures.zip` 为 748,491,996 字节；静态校验通过，核对 ZIP、程序集版本、运行时、许可及九项内嵌美术，收据为 `release/HeyMelody_Windows_0.9.2_STATIC-VALIDATION.json`。另独立核对十五个入口的 ReadyToRun 头与架构全部通过。构建日志保存在本机 `artifacts/release-0.9.2-20261011/`，摘要、检查收据和修改前文档保存在 `research/release-0.9.2-20261011/`。

前两次完整构建停在既有 Client 重连测试的计时联合断言，失败日志均保全。第二次明确观测到 400 ms 延时的计时结果为 399.946 ms，而模拟服务的同设备恢复与验证快照均正常；为计时断言增加 20 ms 容差，原暂停检查、同设备恢复及不发送 SET 的检查保留。首次失败未单独确定具体子断言原因。未执行 Air4 Pro 真机恢复、实设备 SET 或 ARM64 硬件验证。

## 0.9.1

2026-10-11 发布于 `arch/ui-perf-0.9.0` 分支，修正 0.9.0 在 Windows 实测中发现的四处主窗口问题，不改协议、依赖、字体或打包内容。

页头去掉“断开客户端”和刷新按钮，只保留应用设置按钮，并在标题与副标题两行间垂直居中。双击底栏中的当前设备卡片刷新状态；三击断开控制连接，1 秒后重新连接，不重发设置。键盘可用 F5／Shift+F5。BassWave 改为均衡器卡片的直接子行，不再露出内层卡片底色，开关与滑块回到右侧。所有下拉框文字垂直居中（FluentAvalonia 的 ComboBox 主题没有设置垂直对齐，0.9.0 实测偏上约 3 px）。操作记录恢复为只读文本框。

验证（Linux x64 无头，2026-10-11）：App 98 项（新增 6 项手势、按键、下拉框与操作记录检查），Client 162 项（新增 4 项重连检查），设备管理器 8 例，校准界面 13 项，全部通过；Host、FitTest、HearingSetup 构建通过。未验证：Windows 上雅黑字体下的实际居中效果、真实耳机的断开与重连、系统双击间隔下的三击手感。

## 0.9.0

2026-10-11 发布于 `arch/ui-perf-0.9.0` 分支。依据与验证边界见[界面与渲染性能](../research/ui-performance-0.9.0.md)。

0.9.0 完成一轮界面审计与渲染性能优化。主界面不再在窄窗口改为上下堆叠：小于 1080 px 时边距和右侧面板收窄（480 px），1600×1000 以上面板与主图放大并居中；设置按“声音”“听感与佩戴”“设备”分组，贴合度测试改为原生卡片，操作记录改为不滚动的最近 20 条加“复制全部”，切换设备时主图淡入，设置对话框的主题即时预览、取消即恢复。设备管理器的活动条不再挤动页头，错误提示固定在列表上方，列表只占一个 Tab 停靠点并支持方向键，刷新时就地更新行。两个校准窗口把提示固定在页面上方，忙碌时不再整页置灰，进度显示在标题下方，图例在深色主题下使用正确颜色，Enter／Esc 对应主按钮和取消。托盘单击打开主界面，提示显示当前已连接耳机。

渲染方面，三维模型改为按显示器刷新率逐帧旋转并新增帧率设置；动态 WebP 改为后台解码，大图异步解码；动态设置区、均衡器的 BassWave 行与设备管理器列表按 ID 复用控件；隐藏的进度条不再持续计时；升级 Avalonia 12.1.4，固定 Win32 合成与 Skia 资源参数，发布启用 ReadyToRun。界面固定使用系统微软雅黑 UI，并为没有雅黑的系统随附 Noto Sans SC（OFL-1.1），避免英文系统把汉字回退成日文字形。

本轮在 Linux（x64，无头）完成构建与测试：App 92 项、设备管理器 8 例、校准界面 13 项、Client 158 项、Calibration 55 项、Protocol 65 项、Resources 25 项、Devices 19 项、Control 19 项、Daily 131 项通过，ModelPreview 运动检查 180 项通过；Windows 发布交给 CI。没有在 Windows 显示器上实测帧率，没有原生窗口检查、实机耳机或 ARM64 执行。

## 0.8.13

原 `REPOSITORY.md`（行 49, 51）：

Version 0.8.13 adds the Golden Hearing result radar, bundled official audition music and spectrum animation. Game switches are adjacent; game type is hidden globally, and BassWave belongs to the equalizer panel. Device information displays complete running component firmware while capability checks retain their original version value. Sources, behavior and validation limits are described in the [preview and settings research](../research/hearing-preview-settings.md).

Final Windows x64 verification passed all thirteen default runners: 683 checks across the twelve application/protocol harnesses plus Windows offline checks. Separate native runs passed 59 main-window, 33 calibration-window and 32 demo checks. The local self-contained x64 package passed the Python static verifier; all 2,843 ZIP paths, sizes and SHA256 match the published directory, including the complete embedded audition WAV and source notices. Logs are under `artifacts/release-0.8.13-final-20261011`, with preserved copies in the local research archive. No headset audition, Free4 firmware capture or native ARM64 execution was performed.

原 `README.md`（行 3, 5）：

**0.8.13** 使用 Avalonia、FluentAvalonia 和共享托盘后台，在 Windows 上提供资源驱动的耳机控制、状态监听及图片、三维模型和动画展示。

0.8.13 完善黄金听感结果页：六轴雷达图显示左右耳校准曲线与增强区间，内置官方试听音乐，支持增强前后播放比较和频谱动画。两套来源的游戏开关相邻排列，全部型号隐藏游戏音效类型；BassWave™动态低音开关及强度滑块移入均衡器。设备信息按耳机组件显示完整固件版本，固件功能门槛继续使用原始版本值。依据与验证边界见 [黄金听感预览及声音设置](../research/hearing-preview-settings.md)。

## 0.8.12

原 `README.md`（行 7）：

0.8.12 区分日常状态与校准通知订阅：校准事件的明确拒绝不再引发日常状态告警，真实订阅和读取故障仍会提示。耳机状态提示移到顶部连接状态右侧，以圆点分隔，长提示可悬停查看完整内容。降噪保持默认展开的 Expander。说明见 [通知提示修复](../research/notification-header.md)。

## 0.8.11

原 `README.md`（行 9）：

0.8.11 修复游戏音效开关与类型的独立写入、失败后的状态回读及控件恢复，选项同时受所选来源配置、固件条件和耳机能力约束；Hi-Res 与空间音效按各自开关提交，不再显示互斥限制或自动关闭另一项。主界面右侧降噪恢复为默认展开的 Expander，手动折叠状态在刷新时保留。原厂依据、跨型号范围及 X4 验证见 [游戏音效与空间音效研究](../research/game-audio-spatial.md)。
