# 蓝牙状态事件与盒盖广播证据

> 当前客户端仅使用欢律配置与资源，见[当前单源规则](heymelody-only.md)。下文涉及已退出支持来源的覆盖、接口和操作要求仅用于历史研究复核，已由当前规则取代；原版本、路径、摘要和验证范围保留。

本文为公开整理稿；[整理前完整原文](../../research/migrations/organization-2026-10-09/originals/docs/research/bluetooth-events.md)保存在本地研究档案，不随普通Git clone提供。历史版本、来源及验证范围保留，完整研究对象与产物由 `research/` 保存。

本记录说明本轮状态推送实现所依赖的协议证据、Windows 接口和验证边界。它不包含耳机固件源码，也不将协议推断当作实机观察。

## 输入与证据定位

- `research/apps/无线耳机_17.6.3.apk`：OPPO 系统组件，包名 `com.oplus.melody`，版本 17.6.3。
- `research/apps/欢律.apk`：欢律，包名 `com.heytap.headset`，版本 17.6.5。
- 产品配置提取方法和 APK 白名单来源见 [oppo-device-support.md](oppo-device-support.md)。

以下位置是 APK 中原始 DEX 的字节偏移，并非仓库源码行号。通过内存中的 DEX 方法表与指令读取核对；无需将整个 APK 反编译结果提交到仓库。

| 证据 | APK / DEX / 符号 | 方法起点 |
|---|---|---:|
| 查询通知能力 | 欢律 `classes3.dex`，`btsdk.protocol.commands.i.a(String)` | `0x23A41C` |
| 通知能力响应与订阅响应 | 欢律 `classes3.dex`，`btsdk.protocol.commands.i.c` | `0x23B23C` |
| 通知事件分派 | 欢律 `classes3.dex`，`btsdk.protocol.commands.i.b` | `0x23A458` |
| 设置同步包分派 | 欢律 `classes3.dex`，`HeadsetCoreService.m0` | `0x23464C` |
| SPP 放置、佩戴、盒盖标志 | 欢律 `classes3.dex`，`StatusInfo` 构造函数 | `0x22BAA0` |
| OEM 当前厂商广播解析 | 无线耳机 `classes2.dex`，`model.scan.k.c` | `0x236160` |
| OEM 广播类型识别与地址解析 | 无线耳机 `classes2.dex`，`model.scan.k.d(ScanResult)` | `0x236910` |
| OEM 广播电量转换 | 无线耳机 `classes2.dex`，`model.scan.k.b` | `0x236E38` |
| OEM 盒盖打开记录 | 无线耳机 `classes2.dex`，`repository.earphone.J.h` | `0x21846C` |
| 较旧 OEM SDK 广播解析 | 无线耳机 `classes.dex`，`I6/f.b` | `0x1C935C` |
| 欢律 SDK 广播解析 | 欢律 `classes3.dex`，`m8/e.b` | `0x406730` |

## SPP 通知订阅与响应

先完成型号与命令能力验证，然后显式开启状态监听。普通只读查询不暗中产生订阅。

1. 发送 `0x0200`，请求载荷为空。响应 `0x8200` 为 `[status, count, eventIds...]`。
2. 只订阅设备明确提供、且当前后端理解的事件：`1` 电量、`2` 放置/盒盖、`3` 降噪、`5` 游戏状态、`6` 多设备连接信息、`15` 公共事件。
3. `0x0100` 命令能力位图的 bit 22 表示支持 `0x0205`。支持时发送 `[count, eventIds...]` 一次注册；否则使用 `0x0201 [eventId]` 分别注册。
4. 不猜测未知事件编号，也不在设备拒绝注册后尝试未核实的替代命令。拒绝结果通过状态警告报告。
5. 连接更换后重新验证身份，并在原来已启用监听的情况下重新订阅。每个连接的请求序号只使用一次，不能循环复用以匹配延迟响应。

`0x0204` 载荷为 `[eventId, data...]`；兼容的 `0x8202` 接收载荷为 `[status, eventId, data...]`。本轮没有发送 `0x0202`：接收分派器存在 `0x8202` 不足以证明需要轮询请求。

电量和放置通知数据均为 `[count, (component, raw)...]`，component `1/2/3` 分别对应左耳、右耳、盒。重复 component 或截断数据不提交状态。部分通知只更新出现的 component，不清空另两项。

SPP 状态字节中 bit 0 为反向的“在盒内”标志，bit 1 为佩戴标志，bit 2 为反向的“盒盖关闭”标志。因此 component 3 的 bit 2 为 1 表示开盖。电量状态不提供盒盖证据，零电量和未知电量均不能推导开合或连接状态。

已核对的普通状态分支 `1/2/3/5/6` 在分派给 Handler 后直接返回，不发送 host ACK。APK 中另有诊断等事件的选择性 ACK 分支；不能将这些分支扩展成“所有 0204 都要 ACK”。当前后端对状态通知、050x 同步包均不生成 ACK。

## 设置同步包与状态归并

| 接收命令 | 已核实载荷 | 后端处理 |
|---|---|---|
| `0x0503` | `[count, (featureId, state)...]` | 按 feature 合并原始状态；未知值保留，不授权设置 |
| `0x0504` | `[presetId]` | 更新内置 EQ 编号；独立的自定义选中状态按需回读 |
| `0x0506` | `[count, completeEqRecords...]` | 严格校验完整自定义 EQ 列表；没有 GET 的 status 字节 |
| `0x051D` | 手机空间模式协商请求 | 仅使相关空间状态需要回读，不把它解释成 `0x012A` 当前状态 |

050x 包不会与 SET 的响应匹配。例如 `0x0406` 只接受相同序号的 `0x8406`，`0x0504` 即使序号相同也不是 ACK。收到目标状态推送、或之后回读恰好符合目标，都不能将未知/空 ACK 改为成功。

新空间模式由 `0x012A` 回读；旧空间模式只在实际报告的 `0x1B` 为 0/1 且选定旧协议路由时使用。未知的现代游戏状态不能回退成旧 `0x06` 游戏开关。未知旧空间 feature 必须同时使空间状态和设置能力变为未知。

游戏、多设备、公共事件或未完整覆盖当前 UI 状态的通知，可以触发所需范围的 GET。多个事件合并为一次排队读取，空闲期间没有定时 GET。公共事件 15 的载荷不是 `0x0100` 命令能力位图，不能直接替换命令能力。

通信连接只有一个持续读循环。它对一批数据中的全部报文做分派，不能在遇到匹配响应后丢弃同批后续通知。每个查询记录开始时的通知版本，最终缓存合并时保留该字段在查询期间收到的新通知，避免旧 GET 回答覆盖新状态。对查询、通知、设置回读的发布使用独立快照和递增 Revision。

左右耳、盒电量以及盒盖、左右佩戴分别保存观察时间。收到单个左耳通知只更新左耳时间；降噪、EQ 等无关查询不能刷新盒盖或电量时间。后端融合 BLE 与 SPP 时应比较相应字段的观察时间，不能仅比较整个快照的采样时间。

## OEM 厂商广播布局

OEM 当前解析器 `model.scan.k.c` 接受 `fastPairId` 的 `0x98xx` 系列。公司 ID 为 `0x079A`（OPPO），另有 `0x072F`（OnePlus）共用布局。realme 的其他分支不在本轮 OPPO 布局范围内。

Windows `ManufacturerData.Data` 已不包含公司 ID。下表的偏移从厂商数据本身开始：

| 字段 | 标志 | 长度 | 解释 |
|---|---:|---:|---|
| productId | 固定，偏移 0 | 3 | 小端 24 位 |
| flags | 固定，偏移 3 | 2 | 小端 16 位，要求 bit 0 / `0x0001` 存在 |
| fastPairId | 固定，偏移 5 | 2 | 小端，要求 `(id & 0xFF00) == 0x9800` |
| pairingState | `0x0002` | 1 | 配对状态 |
| anotherAddress | `0x0004` | 6 | 反序组成 MAC |
| earbudsStatus | `0x0008` | 1 | 盒盖与佩戴信息 |
| batteryStatus | `0x0010` | 3 | 左、右、盒电量原始字节 |
| color | `0x0020` | 1 | 颜色 ID |
| accountKeyFilter | `0x0040` | `1 + n` | 长度字节 n，随后 n 个字节 |
| phoneCrc16[0] | `0x0080` | 2 | 第一项手机 CRC16 |
| supportsLeAudio | `0x0100` | 0 | 能力标志，不消耗载荷字节 |
| phoneCrc16[1..2] | `0x0200` | 4 | 后两项 CRC16 |

可选字段从偏移 7 按表中顺序出现。因此状态字节的位置取决于前面两个可选字段，不能固定取某一偏移。实现需在提交记录前验证所有声明字段的完整长度，未知尾部可保留作诊断，不能推测出额外能力或状态。

`0x0040` 分支还读取 flags 的 `0x0800/0x1000/0x2000/0x4000/0x8000` 位作为账户/多设备相关状态；这些位不在上述顺序中额外消耗字节。本轮不据这些信息授权设备写入，也不使用 Android 手机 CRC 作为 Windows 设备身份。

### BLE 状态字节

| 位 | 为 1 | 为 0 |
|---|---|---|
| bit 0 | 盒盖打开 | 盒盖关闭 |
| bit 1 | 左耳不在盒内 | 左耳在盒内 |
| bit 2 | 左耳佩戴中 | 左耳未佩戴 |
| bit 3 | 右耳不在盒内 | 右耳在盒内 |
| bit 4 | 右耳佩戴中 | 右耳未佩戴 |
| bit 6 | 非 Freeman 模式 | Freeman 模式 |

佩戴中优先于在盒内；否则为盒外未佩戴。没有 `0x0008` 时，盒盖和佩戴必须为 Unknown。BLE 盒盖是 bit 0，SPP 盒盖是 component 3 的 bit 2，两者不能共用相同掩码。

### BLE 电量

每项 `raw & 1` 表示有效；有效时数值为 `(raw >> 1) & 0x7F`。无效值不是 0% 电量。与此不同，现有 SPP 电量使用高位表示充电，低 7 位为数值；两套格式不能混用。

OEM helper 默认 `batteryRadix=10`：除数值 1 保留为 1 外，显示值为 `min((decoded / 10 + 1) * 10, 100)`，其中除法为整数除法。它不是一般的向上取整：90 会进入 100 档。非 10 的 radix 不做该分档。后端保留原始有效性和解码值，不从电量字节猜测充电、盒盖或断线。

### 地址关联与 X4 边界

OEM 当前 OPPO/OnePlus 分支仅在 `fastPairId == 0x9803` 且嵌入 MAC 非全零时，把 `anotherAddress` 提升为目标地址。其他 `0x98xx` 的 `anotherAddress` 不能一概当作已配对 Classic 地址。

本轮关联规则为：优先使用上述 9803 稳定地址；否则仅允许明确 Public 且非零的广播发送地址与已配对 Classic 地址精确相等。Random 或 Unspecified 发送地址不能通过姓名、RSSI 或“目前只有一个同型号”来猜测归属。产品 ID 还必须属于支持列表，并与已有已确认产品身份相容。未匹配广播不改变已配对设备状态、不触发设置或自动配对，可保留有限诊断信息。

X4 配置 ID 是 `06EC10`，对应广播开头 `[10 EC 06]`。OEM 白名单声明 `fastDiscovery=1`、`brLeCoexist=1`、`swiftPair=1`，但没有固定 fastPairId 或真实广播样本。不能据此保证 X4 固件使用 9803。

较旧 OEM SDK `I6/f.b` 与欢律 SDK `m8/e.b` 只接受 9801，并跳过地址字段，不能代替 OEM 当前 98xx 解析器。旧服务 UUID `00009801-0000-1000-8000-00805F9B34FB` 的发现分支仍存在，但没有 X4 使用它的证据；该分支属于 OEM 历史实现；本轮 Windows 后台不解析旧服务 UUID 格式，也不从它猜测盒盖。

`OppoAdvertisementTests` 中的广播 fixture 是从上述布局构造的 **synthetic fixture**，不是 X4 实机捕获。它验证解析、截断拒绝、标志语义、电量和地址规则。实机验收需捕获至少一次尚未建立 SPP 时的 X4 开盖广播，确认公司 ID、fastPairId、地址类型与嵌入地址；同时检查广播与 ScanResponse 的实际分布。

## 开合去重与广播消失

OEM `J.h` 为首次打开的目标地址记录 `elapsedRealtime`；已记录的持续打开不刷新这项记录；显式关闭移除记录。未看到将广播停止直接解释为盒盖关闭的依据。另一个 5 秒定时任务位于状态交付路径，不能将它当作盒盖过期规则。

同一设备的 BLE/SPP 应进入同一盒盖状态归并。首次 Unknown → Open 可以产生开盖事件并标注首次观察；持续 Open 不重复；只有可靠的新 Closed 才允许下一次 Open 重新产生开盖。本轮实现的 30 秒有效期只用于各组件显示新鲜性，并非从 OEM 推导出的盒盖 TTL。广播过期不产生虚构的 Closed，也不能仅凭过期重置开盖锁存。

## Windows 接口与低开销等待

- 使用配对设备选择器及 DeviceWatcher 接收初始枚举与动态 Added/Updated/Removed。缓存中存在设备不等于现在已连接；AEP 的 IsConnected、IsPaired、IsPresent 应分别处理。[配对状态选择器](https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.bluetoothdevice.getdeviceselectorfrompairingstate?view=winrt-26100)、[DeviceWatcher](https://learn.microsoft.com/en-us/uwp/api/windows.devices.enumeration.devicewatcher?view=winrt-26100)、[AEP 属性](https://learn.microsoft.com/en-us/windows/apps/develop/devices-sensors/device-information-properties)。
- `BluetoothDevice.ConnectionStatusChanged` 描述 Windows 设备连接，不提供降噪、EQ 或盒盖状态。[连接状态事件](https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.bluetoothdevice.connectionstatuschanged?view=winrt-26100)。
- BLE AdvertisementWatcher 提供 ManufacturerData；接收事件的地址可能 Public、Random、Unspecified，所以关联必须遵循上述稳定地址规则。[ManufacturerData](https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.advertisement.bluetoothleadvertisement.manufacturerdata?view=winrt-26100)、[接收事件](https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.advertisement.bluetoothleadvertisementreceivedeventargs?view=winrt-26100)、[地址类型](https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.bluetoothaddresstype?view=winrt-26100)。
- Active 扫描会发送请求以取得 ScanResponse，Passive 是默认值。完整有效的厂商数据可独立解析，不能无条件拼接不同时刻的半包。[扫描模式](https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.advertisement.bluetoothlescanningmode?view=winrt-26100)、[广播类型](https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.advertisement.bluetoothleadvertisementtype?view=winrt-26100)。
- `Radio.StateChanged` 后读取当前 Radio.State，并把连接重建工作放到任务队列。休眠/恢复也需停止和重建有关监听，不能让旧连接继续更新新代设备状态。[Radio.StateChanged](https://learn.microsoft.com/en-us/uwp/api/windows.devices.radios.radio.statechanged?view=winrt-26100)、[RegisterSuspendResumeNotification](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registersuspendresumenotification)。
- RFCOMM 使用 `WSAEventSelect` 的 FD_READ/WRITE/CONNECT/CLOSE 与单次 managed wait 注册。`WSAEnumNetworkEvents` 原子读取并复位事件；不能分别注册两个 WSAEventSelect 来监听读写，因为后者会替换前者。收到就绪后仍需处理 WouldBlock，关闭时唤醒并结束待处理操作。[WSAEventSelect](https://learn.microsoft.com/en-us/windows/win32/api/winsock2/nf-winsock2-wsaeventselect)、[WSAEnumNetworkEvents](https://learn.microsoft.com/en-us/windows/win32/api/winsock2/nf-winsock2-wsaenumnetworkevents)。

LE Audio 和 LC3 与厂商 BLE 盒盖广播监听独立。flags `0x0100` 只声明支持 LE Audio，不能证明当前在使用 LC3，也不表示必须启用 LE Audio 才能读取广播。[Bluetooth SIG LE Audio](https://www.bluetooth.com/learn-about-bluetooth/feature-enhancements/le-audio/)。

**资料归档（2026-10-08）：** 本文访问链接与复现路径已按新目录更新；研究时的原始位置为仓库根 `research/`，原报告与 manifest 内路径保留不变。新旧映射及提交边界见 [资料归档入口](README.md)。
