# HeyMelody for Windows

独立源码仓库、克隆后的构建步骤和本地研究资料依赖见 [REPOSITORY.md](REPOSITORY.md)。

阶段五客户端 **0.5.0** 已完成，支持 OPPO Enco X4 的电量、降噪、五种声音预设、十段 EQ、双连、游戏和空间模式，以及 Hi-Res 互斥控制。发布包包含 x86、x64、ARM64 自包含版本和自动选择架构的启动器，无须安装 .NET SDK。

完整解压 `release/HeyMelody_Windows_0.5.0_AllArchitectures.zip`，双击 `Start-HeyMelody.cmd`；或直接运行 `release/HeyMelody_Windows_0.5.0/x64/HeyMelody.exe`。先在 Windows 中配对并连接耳机，再在客户端点击“连接耳机”。只读刷新不自动发送设置。

x64 和 x86 发布程序已在本机执行蓝牙查询与真实界面预览。ARM64 已交叉构建并核对 PE 架构和图标，尚未在 ARM 电脑执行。设置路径已通过离线 golden、响应匹配和模拟回读测试，本轮没有在真实耳机发送 SET。记录见 `phase5-notes.md` 和 `artifacts/phase5-release-validation.json`。

源码入口为 `src/HeyMelody.App`，蓝牙后台为 `src/HeyMelody.Host`，日常功能为 `src/HeyMelody.Daily`。使用 .NET SDK10 执行 `Build-Client.ps1` 可以构建三架构发布包。OTA、录音、翻译和健康功能留待后续阶段。

以下为保留的协议研究工具。只读 CLI 为 0.2.0/schema2，受控实验 CLI 为 0.3.0；运行这些源码工具需要 .NET SDK10，以及已配对、连接的 OPPO Enco X4。

```powershell
cd D:\Code\LHDC_Driver\HeyMelodyWindows
powershell.exe -NoProfile -File .\Run-ReadOnly.ps1 -List
powershell.exe -NoProfile -File .\Run-ReadOnly.ps1
```

查询结果保存为 `artifacts/x4-readonly-<UTC时间>.json`，同时输出至控制台。写入采用 CreateNew，不覆盖已有报告。默认报告不含蓝牙地址。可用 `-OutputPath` 指定一个新的文件路径。

直接调用：

```powershell
dotnet run --project .\src\HeyMelody.Probe\HeyMelody.Probe.csproj -c Release -- --probe --output .\artifacts\my-query.json
```

CLI 通过 Windows 设备列表找到唯一的名称匹配项，再从指定 Melody UUID 的 SDP 查询获得真实通道，不扫描裸通道。连接后首先查询 Product ID；必须精确匹配 `06EC10` 才继续。存在多个同名设备时，可用 CLI 的 `--list --show-addresses` 查看地址，并通过 `--address HEX12` 显式选择。这个选项仅在控制台主动要求时显示地址，查询报告仍不写地址。

## 本阶段发送内容

| 命令 | 查询 | 载荷 |
|---|---|---|
| 0x0103 | Product ID | 空 |
| 0x0100 | 命令能力位图 | 空 |
| 0x0101 | 远端控制 MTU | 空 |
| 0x0102 | Vendor ID | 空 |
| 0x0104 | 协议版本 | 空 |
| 0x0105 | 固件版本列表 | 空；设备能力允许时查询 |
| 0x0114 | 当前 codec 原始编号 | 空；设备能力允许时查询 |
| 0x010D | Hi-Res 功能状态 | 01 18 |

发送门只允许上表的命令和对应载荷。没有 SET、通知注册、主机能力宣告、时间应答、OTA、配对或取消配对命令，也不操作驱动、注册表、服务和音频播放。连接私有控制服务本身仍会建立并随后关闭一条 RFCOMM 连接。

序号与响应命令必须同时匹配，业务 status 必须为 0，数据通过各查询的布局校验才算成功。超时、断连或非法完整帧后停止在该会话上发送，不自动重发。控制服务查询使用同步 Windows SDP API，因此运行于独立进程中：工作进程操作预算 55 秒，父进程 70 秒后终止未返回的工作进程。这是进程监督，不是对所有内核调用的绝对耗时保证。

报告的 `success=true` 表示八项 GET 均收到匹配的成功响应并通过对应布局校验，且身份为 X4、feature 0x18 状态明确。单项失败、未知状态或能力不支持会保留在 `queries` 内，整体标记 partial。原始 codec 编号和固件版本三元组保留，避免跨固件或跨接口套用枚举。协议版本的三字节二进制形式保留原始 hex，并明确标记未确认其版本语义。功能状态只有 0/1 可以映射成关闭/开启，其他值保留为未知。

能力结果的 `supportedCommands` 保留阶段 1 八个 GET 的可用子集。`allSupportedCommands` 使用 APK 的完整 69 位映射和 11 条基础命令，未知尾位记录为 `unknownCapabilitySetBits`，不据此猜测额外命令；完整原始位图保留为 `capabilityBitmapHex`。这两个集合都不是发送授权。`readOnly.sentQueries` 是应用提交记录，可检查命令与帧；它不是独立 HCI 抓包。

## 阶段 2 的配置与状态

`deviceModel` 仅在本次 Product ID 精确验证为 06EC10 后生成；蓝牙名称和别名不能替代身份验证。嵌入 X4 资产与证据包成员逐字节一致，加载时验证 SHA256。`profile.rawSource` 和子项原始 JSON 保留未投影字段，配置的数字标志不会作为当前开启状态。

EQ 预设保持协议编号顺序 **0、7、2、1、3**，与显示顺序或 modeType 分开；ANC 父子编号和未知子类型原值保留。十段 EQ 频点、空间列表 0/1/2、游戏音效类型 3/0、互斥列表 1/3、多设备条件和源版本门槛均保留。

能力模型分别记录静态声明、查询/设置命令支持、原始状态及 0/1/unknown 状态。当前实际只请求 feature 0x18；其他状态在模型中保持未知，额外回包值可在查询证据中查看。新空间模式与旧 feature 0x1B 分开；游戏模式 0x06/0x28 和专用游戏音效 0x012B/0x0423 分开，不与驱动低延迟设置混同。

来源中的 `minVersion` 属于 Android 应用版本门槛。版本条件按来源字段和 App/Firmware 域保留为 Pass/Fail/Conditional，不拿 Windows 程序的 0.2.0 与耳机固件 116 比较。只读工具未提供额外版本判断上下文，因此正门槛可保持 Conditional；它是条件资料，不等于某项设置已被实际验证。

同一会话最多分配256个互不重复的请求序号；耗尽后停止发送，需要新会话。响应必须带正确响应位、命令与序号。EOF遇到半帧或未完成分片时拒绝并清理状态。只读模型没有传输接口，settingsTransmissionEnabled=false；设置通过独立的Control workflow明确执行，不由模型投影触发。

## Hi-Res与高采样率验证

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Run-HiRes.ps1 -Action Enable
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Run-HiRes.ps1 -Action DriverStatus
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Run-HiRes.ps1 -Action Rate -SampleRate 192000
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Run-HiRes.ps1 -Action Play -SampleRate 192000
```

Enable处理耳机空间互斥、单次设置、匹配ACK及新控制会话回读；可能短暂中断播放。Rate只发送一次临时96/192请求；Play做短静音测试。设置结果和线上协商分别记录，不能把驱动目标当成已验证的wire配置。

本机完整结果为Hi-Res开启，实际96/192 kHz、24bit LHDC5配置均被耳机接受，Start及媒体流有证据，最终保留192k/24bit。使用说明和测量边界见 `phase34-notes.md`；汇总 `artifacts/phase34-validation.json`。这不构成声学时钟、无损比例、逐样本bit-perfect或实际码率测量。

耳机的 codec/Hi-Res 回读不等于 Windows 驱动的采样率、实际传输码率、无损状态或声音延迟。只读 CLI 不执行 Hi-Res 设置和音频协商验证。

## 验证

```powershell
dotnet run --project .\tests\HeyMelody.Protocol.Tests\HeyMelody.Protocol.Tests.csproj
dotnet run --project .\tests\HeyMelody.Devices.Tests\HeyMelody.Devices.Tests.csproj
dotnet run --project .\tests\HeyMelody.Probe.Tests\HeyMelody.Probe.Tests.csproj
```

协议测试覆盖可变长长度、分片、增量读取、序号、畸形数据和查询解析；会话测试覆盖只读允许列表、身份不符停止、错误序号、空回应、超时/断连后停止与不自动重发。ARM64 交叉构建仅证明编译通过，真实 ARM64 蓝牙执行另需验证。

资料与定位见reference，未修改参考资料。阶段1实测见phase1-notes.md，阶段2见phase2-notes.md，阶段3/4见phase34-notes.md。历史schemaVersion1的阶段1证据保持原样。

