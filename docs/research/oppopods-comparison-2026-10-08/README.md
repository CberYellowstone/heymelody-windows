# 两个 OppoPods 项目的设备适配调查

调查日期：2026-10-08。对象为用户提供的 OppoPods1、OppoPods2 本地 Git 快照（采样时位于 `research/`，现位于 `research/upstreams/`），以及当前 HeyMelody Windows 0.8.3 的源码。仅做静态研究和文档整理，没有修改应用代码、构建 APK、运行模块或连接耳机。

**两个 OppoPods 都使用了第一方资料，且都会解析内嵌设备白名单；不能将它们概括为“只发多数 OPPO 耳机通用指令”。但它们也没有完整复现本项目目前的欢律／无线耳机双源配置处理。** 更准确的分类是：官方型号表的部分字段 + 手写协议实现 + 设备实测经验。其中 OppoPods1 对 ANC 索引和配置档的处理更完整；OppoPods2 的突出增量是 HyperOS 系统集成与 LE Audio 连接协调。

第一方逆向与通用命令并不互斥：三者都需要自行实现协议编码，区别在于是否依据精确型号、官方功能版本、固件条件、设备实际回报来决定发送哪些命令，以及是否保存可复核的来源。

本文中的源码定位见 [EVIDENCE.md](EVIDENCE.md)。仓库提交、白名单摘要和计数保存在 [SNAPSHOT.json](SNAPSHOT.json)。所有“支持”均区分配置登记、代码路径和真机验证，不能用目录数量代替硬件兼容性。

## 1. 调查对象与来源边界

| 本地目录 | 上游仓库 | 本地 HEAD | 提交时间与标识 |
| --- | --- | --- | --- |
| OppoPods1 | [Leaf-lsgtky/OppoPods](https://github.com/Leaf-lsgtky/OppoPods) | `feb7b70ccf6757852a0a58197d0d4839c10a296a` | 2026-10-04 09:42 +08:00，合并 1.4.0 所在分支 |
| OppoPods2 | [1812z/OppoPods](https://github.com/1812z/OppoPods) | `c201e251e68b59d46b6bc2758146b2c7f13ca2a1` | 2026-10-04 20:28 +08:00，Release 2.2.0 |
| 当前主项目 | 本工作区 | `5ad23874487aaaac3efa12971cc05ad21bf601b7` | 2026-10-08，0.8.3 ANC 主模式修正 |

调查开始时三个工作树的 `git status --short` 均为空。两个 OppoPods 保留相同包名，README 均追溯到 HyperPods；OppoPods2 另致谢 Leaf-lsgtky/OppoPods 和 OPPOLeaConnect。相同包名和相同资产不能证明所有实现相同；下文逐条按各自源码判定。

公网 GitHub 固定提交页面未能通过浏览工具取回；另分别尝试了用户提供的 `127.0.0.1:59991` HTTP、SOCKS5 代理，raw.githubusercontent.com 请求均在连接阶段中止。研究所需源码已完整在本地，因此继续基于固定快照完成调查。本文不声称已核对上游最新提交、最新 release 或 issues，也没有下载其他 APK／抓包。

## 2. 两者确实用了官方白名单，但来源复现程度有限

两仓库 `app/src/main/assets/device_models.json` 的 **SHA-256 完全相同**：

`0e23d882c22104dc6e1ee0af0bed93265333785ea4f1ad079a11d8c719abd9c8`

| 本地复算指标 | 两份文件共同结果 |
| --- | --- |
| 顶层键 | 只有 `whiteList` |
| 主条目／唯一主 product ID | 137／137 |
| 含 `function` 的条目 | 137 |
| 品牌 | OPPO 56、OnePlus 32、realme 49 |
| 类型 `S` | 3 个 realme 音箱条目 |
| `supportSpp` | true 114、false 6、缺失 17 |
| Enco X3 `067410` | 存在 |
| Enco Free4 丹拿版 `06C010` | 存在 |
| Enco X4 `06EC10` | **不存在** |
| 来源版本、获取时间、内容版本、下载 URL、APK 摘要 | 文件未记录 |

137 是主条目数，没有展开 `children` 后重新统计，且包含其他品牌和音箱；不能说成“支持 137 款 OPPO 耳机”。[A1](EVIDENCE.md#a1)

OppoPods1 的 `DeviceModelRegistry` 注释明确声称该表取自官方 HeyMelody 云端配置；OppoPods2 称其为 bundled official model table。表确实包含官方风格的 `function`、`noiseReductionMode`、`protocolIndex`、EQ 和版本条件，并非仅名称清单。然而两仓库未见与这份资产配套的下载、解密、兼容字段转换及可复现提取工具，也没有该资产的版本和采集出处。因此“作者声明的官方配置来源”与“本项目可按固定 APK／请求复建的来源链”应分开记录。不能从 JSON 相同推导它属于本项目两来源中的哪一版本。[A2](EVIDENCE.md#a2)

OppoPods1 的 `docs/HeyMelody_Bluetooth_Protocol_Notes.md` 明确记录 JADX 分析官方欢律；`HeyMelody_Official_App_Protocol_Findings.md` 还分析官方 App 私有数据库、反馈日志、`control_<product>_<color>/config.json` 和白名单缓存。这充分说明作者的研究方法包含第一方逆向。它并不证明当前应用已完整实现笔记中的所有流程，更没有证明它解析了本项目所用的“无线耳机 17.6.3”组件。[A3](EVIDENCE.md#a3)

两者的“导入欢律资源”入口实际通过 root 读取 `com.heytap.headset/files/melody-model-download/control_…/res/image/` 下的 PNG，并保存为设备图片。**该入口不是 APK 解析器、能力配置导入器或官方组件代理。** 美术导入与设备能力表是两条独立路径。[A4](EVIDENCE.md#a4)

## 3. 运行时适配机制对照

| 维度 | OppoPods1 | OppoPods2 | 本项目当前实现 |
| --- | --- | --- | --- |
| 型号表 | 内嵌单份 137 条表，部分字段投影 | 同一份表，投影字段更少 | 两 APK 派生目录；欢律／无线耳机整体选择、分别缓存和更新 |
| 身份识别 | SPP `0103/8103` 精确 PID；名字模糊回退；可手选配置档 | SPP PID 优先；名字去符号后精确回退 | 精确 PID 对应所选来源配置；名称、美术不授予写权限 |
| 自动发现 | 系统 Hook 名称含 `oppo`；另可手选配对设备 | 系统 Hook 同样名称含 `oppo`；另可手选 | Windows 配对白名单、系统事件与广播身份信息融合 |
| ANC | 官方父／子 `protocolIndex` 生成位图；回报按配置反查；仍有手工档案 | 官方表判 legacy／顶层 adaptive，主要命令与等级固定映射 | 所选配置完整父子选项；0.8.3 主按钮发父编号，子控件发子编号 |
| 游戏模式 | 配置推导 `06/28`；缺字段倾向支持，未真正用回报切换 feature 路线 | 配置推导；PID 尚未知时可按回报推断 `06/28` | 根据实际 feature 回报及配置条件选择路线 |
| 空间功能／EQ | 区分旧空间开关和三档；静态 EQ 加耳机查询，支持自定义 EQ／按键研究 | 同样有部分空间／EQ 能力解析及查询 | 功能版本、条件、频率、容量及可解析状态共同限制 |
| SPP 选择 | 用户选两 UUID 顺序尝试，或显式 channel 15 | 固定 `0000079A…` | 所选配置允许的两个完整 UUID，经 SDP 匹配和校验 |
| 设备命令能力 | 发 `0100`，没有形成命令 bitmap 对 setter 的完整限制 | 主要查询通知能力；没有 `0100` 命令 bitmap 限制 | APK 位图映射 + 实机命令能力 + 配置 + 可解析状态取交集 |
| 固件／条件 | 多数原始条件未落入可写判定 | 多数条件未消费；EQ 版本列表合并而未按版本选择 | 已识别版本条件通过才启用；版本上下文缺失或值不可解释则 Conditional |
| 官方配置更新 | 未见内置／云版本选择器和双源更新链 | 未见此链 | 完整来源选择、严格内容版本更新、失败回退 |
| 操作结果 | 部分控件先乐观更新，设置响应主要记日志 | 部分设置后补查及同步，多处乐观更新；没有本项目同等请求关联保证 | ACK 接受 + 同设备状态回读；不确定 SET 不自动重发 |

依据：[A5](EVIDENCE.md#a5)、[A6](EVIDENCE.md#a6)、[A7](EVIDENCE.md#a7)、[A8](EVIDENCE.md#a8)、[A11](EVIDENCE.md#a11)。

### OppoPods1：官方索引驱动的范围更大

链路为：内嵌表 → `DeviceModelRegistry` → `DeviceCapabilities` → `CapabilityProfileFactory` → `DeviceProfile` → RFCOMM 控制器。PID 回来后会更新配置并补查状态。默认配置、手工绑定和白名单自动配置并存。[A5](EVIDENCE.md#a5)

它最值得参考的是 `ancPayload(index)`：产生 `01 01 <LSB bitmap>`，而非按型号重复写整包。例如同为关闭／降噪／通透，X3 的父索引是 0／1／2，Air4s 则是 1／0／2。因此“关闭恒为 01、降噪恒为 02”并不成立。Free4 顶层 adaptive 的 index 11 生成 `00 08`；X3 通透下面的 adaptive 子档 index 9 不自动变成第四个顶层主模式。[A6](EVIDENCE.md#a6)

这一思路与本项目 0.8.3 的父／子编号修正一致，可作为交叉检查线索。由于其表没有 X4，这不是 X4 提示音修复或 X4 子档 12／13／14 的额外真机证据。本项目现有两 APK 的 DEX 审计应继续作为判定来源。[A12](EVIDENCE.md#a12)

它也解析 `supportSpp` 和部分 `protocolType`，但这些结果没有被生成的控制 profile 及 setter 判定实际消费。未知型号 fallback 还会开放游戏、降噪等级、佩戴与双连接等固定指令；游戏字段缺失通常默认支持。配置档可编辑是一种人工兼容手段，不能等同官方证明或保守探测。[A7](EVIDENCE.md#a7)

### OppoPods2：也用官方表，但 ANC 和系统控制的简化更多

PID、双连接、空间功能、自定义 EQ、频率、预设 ID 与游戏 feature 的选择确实会受表影响。名字匹配不是其唯一型号判定机制，不能因 README 只列几个基础功能就判定它完全没有设备适配。[A8](EVIDENCE.md#a8)

但其 ANC 只抽取“是否 legacy”和“是否有顶层自适应”等标志；常规档位、智能／轻／中／深仍由固定命令及 UI 规则提供，没有像 OppoPods1 那样按每个型号的完整 ANC 父子树生成。`supportSpp`、UUID、`protocolType` 和大部分固件门槛未消费，EQ 版本候选列表被无条件合并，不能把表里存在的条目直接视为可正确控制。[A8](EVIDENCE.md#a8)

自动 Hook 两者都先依赖名称含 `oppo`，所以 OnePlus／realme／改名设备可能在查询 PID 前就无法进入自动连接。手动选择其他配对设备的路径存在，故这里描述的是自动发现缺口，不能说其他品牌在所有路径上绝对不可连接。[A5](EVIDENCE.md#a5)、[A8](EVIDENCE.md#a8)

## 4. 值得借鉴的经验及适用范围

| 经验 | 具体价值 | 对本项目的建议 |
| --- | --- | --- |
| 官方 APK + App 数据库 + UI config + 反馈日志 + 抓包互证 | 将 PID／色号、连接方式、通知与功能动作对应起来 | 扩展硬件样本时沿用此取证方法；保留型号、固件、动作、原包摘要，不只保存几个成功 SET |
| ANC 主档、用户细档和智能当前等级分离 | 父模式、子档及固件自动应用的等级是不同状态 | 用 X3／Free4／Air4s 增加人工比对样本；尤其检查新版主档与老机型位序，不将当前智能等级当用户主动设置 |
| 识别时序容错与 EQ 补查 | PID 建档前后、握手期间的 EQ 回包可能不同步 | 将“PID 晚到、EQ 列表先到／丢失、重连后补查”纳入后续真机验收；本项目已有查询与通知框架，无需另起控制层 |
| 通知能力驱动订阅 | 用 `0200/8200` 返回的 event IDs 订阅 `0205`，减少硬猜 | 对照现有订阅实现验证旧固件、少事件和重复事件；事件列表应再与已实现解码器取交集 |
| EQ 使用设备返回的 ID、名称、频率和参数 | 静态预设表不包含全部用户自定义条目 | 检查真实 EQ 列表和容量回报；避免以 `equalizer/customEqualizer` 功能版本推导段数或槽位数 |
| 控制链路与系统界面集成分层 | Hook／独立模式共享 profile，证明控制不必依赖小米 UI | 借鉴协议与状态层，保持 Windows Host 的单一连接所有权；Android 广播、反射和 Focus Island 无法直接移植 |
| 带命令语义的收发日志 | 便于区分读写、通知、响应和人工操作 | 对现有诊断检查是否能关联 source、PID、固件、seq、事件、ACK 与读回；采用有界存储和地址脱敏 |

依据：[A3](EVIDENCE.md#a3)、[A6](EVIDENCE.md#a6)、[A9](EVIDENCE.md#a9)、[A11](EVIDENCE.md#a11)。其中多数属于现有架构的验证样本或诊断补强，不能都算作本项目缺少的新功能。

### LE Audio：最值得单独继续调查的线索

OppoPods1 增加了 LE Audio 连接事件入口：即使 A2DP／HFP 已被系统拆除，也尝试找到 BR/EDR 对应设备并另外建立 SPP 控制；LE Audio 一只耳掉线时先检查其他 profile，避免立刻拆控制会话。这提示 Windows 不能仅凭 A2DP 状态判断耳机控制是否应存在。[A10](EVIDENCE.md#a10)

不过其 LE→经典地址回退是“配对表里第一个名字含 OPPO 且非纯 LE 的设备”，多个耳机时存在误关联可能。应借鉴问题与状态划分，使用本项目已有身份信息和严格分组来求解；不能复制名称回退。

OppoPods2 进一步实现 Android 蓝牙栈内的 LE Audio 协调：

1. HFP 私有 AT `VDID/VDSF/OESF/VDSP` 应答，并在 HFP 连接后延迟初始化 `+VDSP=1,1`。
2. 修复经典／HFP profile 策略，先提供厂商初始化机会，再尝试 LE，失败后放行经典连接。
3. 通过系统地址映射和设备 group 建立白名单关联、补齐双耳 direct GATT 持有、修复空音频上下文。
4. 用户主动断开后暂停整组补连、释放持有者；默认关闭总功能且设备白名单为空，避免仅凭耳机名称介入。

这是“音频能否正确建立”的经验，与 SPP 调 ANC 是不同层次。README 特别说明 Enco X3 对 HFP／AT 的依赖，并承认未移植 BLE/SIRK 跨手机协调和 HCI `0x13` 自动整组让位。[A10](EVIDENCE.md#a10)

**Windows 上的可用性仍待调查**：这些代码 Hook 的是 Android `com.android.bluetooth` 私有类，并非 Windows 普通应用可直接调用的接口。AT 应答、Java profile 显示 CONNECTED 或双耳 ACL 存在，也不能独立证明双耳 CIS 与实际声音。它们没有提供 Windows LC3 成功证据，更没有提供 LHDC／2.3 Mbps／无损编码能力证据。

后续若专项研究音频，应分别记录 host 握手、HFP 初始化、左右耳 LE 地址／group、ACL/CIS、最终 codec 和真实双耳播放。OppoPods2／其致谢的 OPPOLeaConnect 是值得继续检查的线索；本轮未获取该额外仓库，不对其实现作结论。

## 5. 不能直接照搬的协议与状态实现

以下是静态代码观察或推断，**不是本轮真机复现的故障**。

| 问题 | 本地证据 | 对本项目的含义 |
| --- | --- | --- |
| 长度仍按一个字节编码 | 两者 `Packets.kt`；OppoPods1 framer 也仅读一个长度字节 | 与其官方逆向笔记中的 7-bit varint 不一致；保留本项目增量帧／分片实现，长 EQ 包尤其不能照搬 |
| OppoPods2 一次 `InputStream.read` 当一包 | `RfcommController` reader 直接调用 handler | RFCOMM 是字节流，拆包／粘包可能使解析不完整；OppoPods1 有单字节长度 framer，不能将两者都写成完全无拆包 |
| 请求大量使用固定 seq | 两者 buildPacket 默认 `F0`，特定包另有固定值 | 没有本项目 command+seq 关联保证；多个同类请求、旧回包和通知可能混淆 |
| 批量状态查询仍固定 11 项 | OppoPods1 Factory；OppoPods2 Packets | 官方动态查询逻辑并未完整落地，不适合作为全系通用查询名单 |
| 缺配置／未知设备倾向乐观开放 | OppoPods1 fallback；两者游戏缺字段规则 | “字段缺失”应区分 unknown 与不支持，最终由设备回报和可用 codec 裁决 |
| 通知能力不等于控制命令能力 | `0200/8200` event IDs，与 `0100` bitmap 是两份信息 | 不能把能收到电量／通知解释为所有 setter 已获支持 |
| 早期回包扫描式兼容 | 结构化 feature pairs 失败后尝试逐字节找 key | 有误认其他字段的可能；未知布局应保留 raw 并禁写 |
| OppoPods1 游戏状态分支提前 return | 两个 controller 的 `810D` 路径 | 同包佩戴／双连接／空间值可能未处理；本项目应一次解析整份状态再分别投影 |
| UI 乐观更新不能证明设置成功 | OppoPods1 setter 与 ACK 日志路径 | 保留本项目 ACK+读回、未知状态和不自动重发 SET 的规则 |
| UUID 与 channel 固定关联缺乏普遍证据 | 作者笔记区分官方 UUID 与实测 channel 5／15 | 继续按具体服务 SDP 发现；不能将通道号或首个能连的 UUID 当全系规范 |

依据：[A7](EVIDENCE.md#a7)、[A8](EVIDENCE.md#a8)、[A13](EVIDENCE.md#a13)、[A14](EVIDENCE.md#a14)。

文档的时效也需要判断。OppoPods1 README 的固定游戏查询描述已比现源码粗糙；旧逆向笔记对 ANC 子档 type 有未核实说法。本项目 2026-10-08 的双 APK DEX 核对确认主／子控件均可使用 `0404 [01,01,…]`，应以具体调用链为准，不能仅因第三方旧笔记写 `01 02` 就判定 `01 01` 一定错误。[A12](EVIDENCE.md#a12)

## 6. 与本项目方案的关系及推荐方向

**继续采用现有第一方配置与协议解析方案，两个 OppoPods 作为交叉研究资料和硬件验收线索。** 本项目没有必要改成它们的乐观兼容和固定指令路线。

当前应用已实际接入欢律／无线耳机资源 catalog、来源切换及在线配置，不应再引用早期交接文档推断“生产环境只识别 X4”或“云配置尚未实现”。欢律目录有 85 个耳机 PID，无线耳机目录有 141 个耳机 PID（包含显式 child PID）；其登记数量、transport 初步允许数量和已验证硬件数量仍是三种不同指标。realme 外部控制路线在本项目保留识别但不自动授权控制。[A11](EVIDENCE.md#a11)

本项目已具备：精确 PID、两来源隔离、固件条件、命令 bitmap、功能版本与 GET 状态的交集、增量帧解析、响应关联、ACK+读回确认。仍需逐型号验证，尤其旧服务 UUID 对应的旧协议布局、复杂条件与未实现功能。当前仅把已识别版本字段投影为条件；其他未消费的源条件保留在原始 JSON，并不自动触发 Conditional 或禁写，仍需逐项核实。最新 X4 0.8.1 记录只验收了四种来源／缓存组合的初始化、GET 和监听，不能扩展为所有型号或所有 SET 的成功；0.8.3 的提示音因果仍需用户真机确认。[A11](EVIDENCE.md#a11)、[A12](EVIDENCE.md#a12)

后续工作按价值排序：

1. **优先增加跨型号验收样本**：X3、Free4、Air4s 与 X4；检查父／子 ANC 编号、游戏 `06/28`、旧／新空间路线、EQ 列表及容量。使用官方配置、实际 GET、SET 的 ACK 和读回分别建立证据。
2. **把连接／状态时序列为独立验收项**：PID 晚到、EQ 重查、手机与 Windows 同时改变状态、通知先于 GET、睡眠恢复、LE Audio 在线但 A2DP 离线、多副耳机地址关联。复用当前 Host 架构。
3. **单独开展 Windows LE Audio／HFP 调查**：先确认 Windows 栈允许观察或参与的接口和设备握手；再考虑 AT／group／双耳稳定性，不从 Android Hook 能力直接推导 Windows 可行性。

两仓库 README 均声明 GPL-3.0。本次仅整理定位和研究结论，没有复制其实现代码或耳机图片。

## 7. 成果与复核方式

- [README.md](README.md)：结论、方案对照、适用经验、局限与后续优先级。
- [EVIDENCE.md](EVIDENCE.md)：每项结论的本地源码路径、行号及证据性质。
- [SNAPSHOT.json](SNAPSHOT.json)：固定提交、白名单 SHA-256、统计、关键样本和网络获取状态。

白名单统计通过 PowerShell `ConvertFrom-Json`、按字段分组和 `Get-FileHash` 读取原文件完成；未改写 JSON。源码使用 `rg` 全文检索并核对实际调用点，避免将 README、注释或未调用解析函数当作已经生效的功能。未运行两个 Android 项目的测试，报告中的测试文件只作为已有作者样本的定位。

2026-10-08 按用户归档要求，本报告迁入 `docs/research/oppopods-comparison-2026-10-08/`，README、EVIDENCE 和 SNAPSHOT 纳入可提交范围。两个原始仓库副本位于 `research/upstreams/`；原快照路径保留在 SNAPSHOT，当前路径见本文源码链接，详见 [资料归档](../README.md)。
