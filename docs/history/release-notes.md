# 版本记录

以下原段整理自 2026-10-11 文档重整前的根目录 `README.md` 与 `REPOSITORY.md`，保留原版本、日期、设备、平台及验证范围，仅调整链接位置。原段中的 “current”、默认行为与检查数量属于对应历史版本，不作为后续版本的保证。两个来源有重叠时分别列出；原文未给验证日期的条目不补写日期。

历史 `research/`、`artifacts/`、`release/` 链接指本地研究证据或发布产物，通常不随普通 Git clone 提供。完整原稿和摘要清单见本地[本轮整理档案](../../research/documentation-entrypoints-2026-10-11/README.md)；缺少该档案不影响阅读下列公开原段。现行构建与验证入口见[开发说明](../development.md)。

本页收录 0.8.11–0.8.13。更早记录分为[0.8.8–0.8.10](release-notes-0.8.8-0.8.10.md)、[0.8.4–0.8.7](release-notes-0.8.4-0.8.7.md)和[0.8.3 及更早版本与校准实测背景](release-notes-early.md)。项目早期阶段的独立记录仍见[历史索引](README.md)。

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

