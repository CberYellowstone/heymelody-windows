# 共享后台架构

本文说明当前客户端沿用的共享后台设计，保留 0.7.0 起的架构演进信息。用户操作见[使用说明](usage.md)，开发与验证入口见[开发指南](development.md)，协作规则见[AGENTS.md](../AGENTS.md)。

`HeyMelody.Host.exe` 默认运行 Avalonia 托盘应用，不创建主窗口。当前用户、当前 Windows 登录会话只保留一份后台和主界面。右键菜单使用 Avalonia 的 NativeMenu，只有“打开主界面”和“退出”。正常启动 `HeyMelody.exe` 会确保后台可用并显示界面；重复启动请求激活已有界面。直接启动 Host 只显示托盘。本版本不注册登录自启。

## 进程与通信

- App 与 DeviceManager 使用 schema-4 双向命名管道（历史版本为 schema 2／3）。端点按用户和 Windows Session ID 区分，服务端限制当前用户并检查客户端进程所在会话。公共契约只携带内存中的 opaque Identity，不携带蓝牙地址。
- Host supervisor 管理白名单、设备目录、每台设备的控制状态和观察缓存。设备的产品 ID 与协议颜色按白名单键持久化在 `device-appearance.json`，只作为外观提示随设备目录发布，不授予控制（见 [外观资源持久化](artwork-persistence.md)）。独立的 `--catalog-worker` 执行同步原生枚举；每台耳机使用独立的 `--worker` 执行 SDP/RFCOMM。原生调用卡住时可以结束对应 worker，托盘和其他耳机保持响应。
- worker 地址通过私有标准输入发送，不放在进程命令行。worker 与 Host 的 stdio 通信只在内部使用。历史 schema-1 RPC 适配器仅位于 Client.Tests，生产 ClientBridge 只使用共享管道。
- subscribe 在后台锁内加入订阅并排入目录和缓存基线。后续事件与响应共用一个接收通道，以 ID 匹配请求。每个客户端拥有独立、有界的输出队列；普通状态更新可以合并，退出、激活和响应保留。慢客户端不会阻塞蓝牙接收。
- Snapshot 包含 BackendInstanceId、DeviceGeneration 和单调 Revision。前端只采纳当前后台、当前连接代次的新快照；迟到的手动刷新或 Apply.After 不会覆盖较新手机通知。后台重启后的旧设置不会迁移或重放。schema 版本不匹配时停止自动重连并提示退出旧后台、重新打开完整客户端，不擅自结束仍在运行的共享后台。

## 蓝牙状态

Windows DeviceWatcher 提供已配对设备的连接和解除配对事件，Radio 事件提供蓝牙开关变化。后台自动控制白名单中由 Windows 连接且已核实支持的耳机。选择设备仅改变界面显示和设置目标，不关闭其他设备的控制会话。

每条 RFCOMM 连接只有一个持续接收通道，同时分发 ACK、GET 响应和耳机通知；同一接收批次中的其他包仍会被处理。每台设备的命令串行执行，通知接收不等待命令锁。WSAEventSelect 的原生就绪事件代替 100 毫秒 select 轮询。初始化和连接轮换会重新核验型号、能力与通知订阅。未支持或未证实的能力保持未知。

建立控制连接时，SDP 先查欢律配置的服务 UUID 缓存；缓存缺失、不唯一或缓存通道建连失败时，实际查询远端 SDP，仍要求唯一服务与有效 RFCOMM 通道。每次打开会话至多两次建连，各限五秒，用户取消时停止；同步 SDP 由 worker 总期限兜底。普通设备列表扫描不进行远端 SDP。失败提示保留服务发现类别、查询/通知订阅命令号或 Windows 数字错误码，不包含蓝牙地址或原始异常消息；依据与验证边界见 [Air4 Pro 控制核查](research/enco-air4-pro-control.md)。

普通界面状态不再每八秒读取。初始化、重新连接、手动刷新、设置前后确认仍执行 GET；只包含部分信息的通知可以合并为对应 scope 的补充读取。设置推送不会触发绑定控件提交 SET，未提交选项和 EQ 草稿继续按编辑版本保护。

BLE 使用单个 Active AdvertisementWatcher，接收 OPPO/OnePlus 的 manufacturer record 和 scan response。广播须完整通过字段长度、公司 ID、产品 ID 和可证实的地址关联检查。仅随机地址且无已证实关联的广播不归到某副同名耳机。广播不会授予 ControlReady、能力或设置权限。

SPP 与 BLE 各自保留观测时间，按耳机部件融合电量、充电盒盖及佩戴状态；同一时间优先采用已核验 SPP。其他设置读取不会刷新旧盒盖时间。BLE 字段在 30 秒后失去新鲜性，未观测到的状态保持 Unknown。开盖去重独立于显示过期：首次观测到盒盖状态时即为打开，标为 FirstObservation；重复开盖不重复触发，只有较新的明确合盖能重新允许下一次开盖。暂停、无广播和休眠不等同于合盖。

系统挂起停止 watcher 和控制会话；恢复重建 watcher，并丢弃旧 watcher 代次或早于本次扫描的回调。Modern Standby 使用注册式 suspend/resume 通知。广播或监听 API 不可用时会发布诊断，现有可用 RFCOMM 仍可工作；确证蓝牙关闭时才停控制会话。

详细 APK 依据和广播编码见 [bluetooth-events.md](research/bluetooth-events.md)。当前按欢律配置、App 条件、已实现的协议和设备能力开放设置；固件条件只产生提醒，适配范围见[设备支持说明](research/oppo-device-support.md)。客户端没有改变 Windows 音频驱动或 LE Audio 管线；后台提供 caseOpen 事件，尚未实现独立开盖弹窗。

## 暂停、写入与退出

现有连接/断开按钮恢复或暂停所选耳机的 RFCOMM 控制监听。暂停跨主界面退出和 Windows 重新连接保留，手动恢复或 Host 重启才解除。OS 和 BLE 观测继续。

每次 apply 携带明确设备 Identity、后台实例和连接代次，并重新核对 ControlReady 及能力。用户界面断开时，尚未开始的该客户端写入取消；已经开始的 setter 在其后台设备生命周期仍有效时继续 ACK 和回读；显式暂停、OS 断连或休眠会结束该生命周期。客户端 EOF、超时或后台重启后不自动重发 SET，缺失回执显示设置可能已生效。

关闭主界面先完成既有 EQ 草稿确认，再结束主界面进程；后台继续监听。托盘退出执行 requestExit → exitVote → stopping，两阶段询问现有主界面；否决或投票超时发送 exitCancelled，恢复界面操作。批准后停止新设置，等待已经开始的设置收尾（共享等待上限 125 秒），再清理 worker、watcher 和托盘。

## 验证边界

离线测试覆盖真实命名管道和子进程隔离，协议测试使用合成通知/广播。Avalonia headless 与 Win32 模拟窗口验证推送、设置绑定和退出确认。三架构发布校验 Host 的 WinExe 子系统、新 Windows TFM、托盘图标、原生图形依赖与独立许可目录。以上不证明实际耳机广播、手机并发操作、休眠恢复或 ARM64 真机蓝牙行为。
