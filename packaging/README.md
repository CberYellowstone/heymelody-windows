# HeyMelody for Windows 0.8.2

0.8.2 新增布局演示设备，使用 Enco Free4 丹拿版官方三维模型；保留 0.8.1 的耳机控制连接初始化及错误提示修复。

这是采用 Avalonia 和 FluentAvalonia 的社区 Windows 耳机客户端。资源目录覆盖欢律的 85 个、无线耳机的 141 个耳机产品 ID；配置、协议与实际设备能力共同决定可用功能，未知控制不会开放。OPPO、OnePlus、realme 耳机目录已纳入，音箱排除。

完整解压后双击 **`Start-HeyMelody.cmd`**，启动器选择 x64、x86 或 ARM64 目录中的程序。发布包自带 .NET 运行时，无须安装 SDK；单架构包仅包含所选架构。先在 Windows 中配对并连接耳机，随后通过设备管理器保存名单。管理器、贴合度测试和黄金听感向导分别为 `HeyMelody.DeviceManager.exe`、`HeyMelody.FitTest.exe`、`HeyMelody.HearingSetup.exe`，从主界面打开，共享托盘后台。

检查 Demo：在主界面长按底部设备添加键“+”满 2 秒，添加并选中 `Demo · Enco Free4 丹拿版`。重复长按仍为同一台，短击仍打开 Windows 蓝牙设置。Demo 加载全部已实现主界面控件，可在本地调整开关、选项、Slider、文本和 EQ；测试按钮只显示演示反馈。关闭主窗口后 Demo 自动移除，不影响真实设备。没有已配置耳机时，双击 **`Start-3D-Demo.cmd`** 可直接进入同一离线 Demo，不需要白名单或网络。模型支持拖动旋转、惯性及空闲自转。

设置默认使用欢律、开启在线资源。切换“无线耳机”后整体使用该来源的参数和美术，不混合两套配置。自定义密钥留空时，本版使用对应预置密钥。关闭在线资源后不发起网络请求，保留已下载的有效新版。更新先校验新包、切换引用，再删除旧在线版本；软件预装资源不清理。设置和缓存加密密钥以用户本地 JSON 明文保存。

降噪模式由圆形按钮选择，下方显示细分选项；均衡器频段、预设和可保存方案数由设备决定。修改自定义 EQ 后选择“保存并启用”。耳塞贴合度测试需要双耳正确佩戴。黄金听感下拉框选择本机向导保存及耳机当前完整读回的方案；没有方案时先打开设置向导。校准中可以取消；断连、摘下耳机、关闭窗口等情况会停止测试。

关闭主界面后后台继续运行，右键托盘可打开主界面或退出。直接启动 `backend/HeyMelody.Host.exe` 只启动后台。本版本不添加登录自启或开盖弹窗。Demo 的合成控件集合只供布局检查，不代表丹拿版实机能力。

`ResourceBundles`、各程序 DLL、原生渲染库、`backend` 和 `licenses` 都是运行所需文件，请保留完整目录。资源来自用户提供的 APK 和已验证的官方资源包，版权及源摘要见 `SOURCE-NOTES.md`、`PACKAGE-NOTICES.json`、`ResourceBundles/provenance.json` 和 `licenses/upstream/Extracted-Earphone-Resources-NOTICE.txt`。

模拟协议、界面测试及三架构静态检查不证明所有型号实机、手机并发或 ARM64 硬件行为。具体构建信息见 `manifest.json`；只有与版本、文件摘要匹配的执行记录才能证明对应包的实机验证。
