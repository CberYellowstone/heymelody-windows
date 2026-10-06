# 阶段 1 完成记录

2026-10-06 完成 Enco X4 的 Windows 只读控制会话 CLI、协议库和 RFCOMM 传输库。源码位于 `src/`；运行入口 `Run-ReadOnly.ps1`。本阶段不实现 GUI、耳机设置或驱动配置。

## 本机查询

成功样本为 `artifacts/phase1-x4-complete.json`，开始时间 **2026-10-05 15:58:18 UTC / 北京时间 2026-10-05 23:58:18**。8 项 GET 均按响应命令和本次序号匹配，业务 status=0，并通过各自布局校验。

| 项目 | 本次设备回报 |
|---|---|
| 蓝牙设备名 | OPPO Enco X4 |
| Product ID | 06EC10，精确确认 |
| Melody RFCOMM 服务 | 0000079A-D102-11E1-9B23-00025B00A5A5 |
| SDP 实际通道 | 15，未扫描猜测裸通道 |
| 控制 MTU | 4600 字节；不是 A2DP 音频 MTU |
| Vendor ID | 1946 |
| 能力位图 | FF775AEEEE8FA65F1700000000000000，共 16 字节 |
| 协议版本 | raw 01 00 00；未命名二进制版本语义 |
| 固件 deviceType=1/versionType=2 | 116 |
| 固件 deviceType=2/versionType=2 | 116 |
| 固件 deviceType=3/versionType=2 | 100 |
| 当前 codec | raw 8；未跨层套用 LHDC5 枚举 |
| Hi-Res feature 0x18 | 0，当前 Windows 控制会话回读为关闭 |

版本按原始 deviceType/versionType 三元组保留，没有未经核实合成为单个版本号。Hi-Res 状态不替代内部 lossless 标志、音频配置或逐帧音质证明。本机没有重新读取 Windows 驱动/AVDTP 参数，没有播放、断开重连或开启 Hi-Res。

三次开发期查询均为同一只读策略：首次确认身份但能力位图被过窄长度拒绝；第二次确认能力/MTU/vendor但协议版本二进制被当成文本拒绝；第三次上述解析边界修复后八项全部通过。失败样本分别保留为 `phase1-first-query.json`、`phase1-x4-query.json`，不是最终验收结果。

## 实现边界

- 发现仅枚举 Windows 已记忆的蓝牙设备，不做 inquiry 或配对。目标必须已连接；Product ID 不符即停止。
- SDP 只查精确 Melody 服务，通过 WSAAddressToStringW 提供远端上下文。没有普通 SPP 或裸通道 1/15 回退。
- 普通控制采用 AA + ULEB128 长度 + control/保留字节 + 可选分片序号。内层保留 command、sequence、LE16 payload 长度；不添加不存在的普通 CRC。
- 完整帧、消息和分片数量均有界，FSN 必须顺序；payload 声明长度不符时不截断接受。错误序号及主动推送被忽略，不应答，不注册通知。
- 发送门限定 0100/0101/0102/0103/0104/0105/010D/0114。除 010D 固定载荷 01 18 外均为空。沒有 SET、OTA、时间应答和主机能力宣告。
- timeout、非法完整帧、成功状态的畸形载荷、空 ACK 或断连后，当前会话不再发送，也不自动重发。不同设备拒绝状态按失败结果保留。
- 只读报告默认不含 MAC，`sentQueries` 为应用提交记录，不能声称独立被动 HCI 抓包已核对全部空口行为。
- `supportedCommands` 是阶段 1 GET 子集，完整位图另行保留，未推断未知扩展能力。codec raw 8 不映射为 Windows 驱动或另一固件接口的编号。
- 原生 SDP 不保证线程可取消，使用 55 秒操作预算与父进程 70 秒监督。进程终止可能另需系统调度时间，不承诺故障蓝牙栈的绝对耗时。

## 验证结果

- Debug 和 Release 编译：0 warnings / 0 errors。
- 最终 Release 离线测试：24 项协议测试 + 17 项会话测试全部通过。
- ARM64 Release 交叉构建：0 warnings / 0 errors；实际 ARM64 蓝牙运行未验证。
- Windows ABI：SOCKADDR_BTH 大小 30、地址/UUID/端口偏移 2/10/26；x64 fd_set 大小 520；WSAQUERYSETW 大小 120。
- Windows PowerShell 5.1 的 `Run-ReadOnly.ps1 -List` 实际执行通过，X4 connected=true。
- 本机真实 RFCOMM 查询八项成功，见上述 UTC 时间及 JSON。最终新增的报告范围 metadata、二进制版本 fixture 和空 ACK 禁用边界经离线回归；没有为这些变化重复实机查询。

查验源码和 reference 时参考了本地 Android 17.6.5 的 Packet、PacketFactory、TLVDataProcessor、OPOv1Wrapper、PollCommandManager 及 SDK 26100 的 Windows 蓝牙结构定义。没有改动 reference，也没有读取或写入设备许可证。

