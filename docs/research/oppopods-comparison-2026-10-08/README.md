# 两个 OppoPods 项目的适配调查摘要

2026-10-08 基于本地固定源码快照，对两个 Android OppoPods 项目的型号配置、协议实现与系统集成进行了只读对照。两者都使用作者声明来自官方的型号表，并包含第一方逆向经验；它们不是单纯按设备名称发送一组固定命令。其适配完整性、来源复现程度与真机范围仍需分别判断。

| 调查对象 | 固定提交 |
| --- | --- |
| [Leaf-lsgtky/OppoPods](https://github.com/Leaf-lsgtky/OppoPods) | `feb7b70ccf6757852a0a58197d0d4839c10a296a` |
| [1812z/OppoPods](https://github.com/1812z/OppoPods) | `c201e251e68b59d46b6bc2758146b2c7f13ca2a1` |

本次没有运行其 Android 测试，也未连接耳机；没有核对上游最新提交、发行版或 issues。完整理解、逐项源码定位与统计原件保存在 [research/oppopods-comparison-2026-10-08/](../../../research/oppopods-comparison-2026-10-08/)；[原调查报告](../../../research/oppopods-comparison-2026-10-08/originals/README.md)、[EVIDENCE](../../../research/oppopods-comparison-2026-10-08/originals/EVIDENCE.md) 和 [SNAPSHOT](../../../research/oppopods-comparison-2026-10-08/originals/SNAPSHOT.json) 保留当时的证据范围。

## 已核实的共同点与差异

两份内嵌 `device_models.json` 的字节摘要相同，共有 137 个唯一主产品 ID：OPPO 56、OnePlus 32、realme 49，其中包含 3 个音箱条目，未展开 children。X3 与 Free4 丹拿版存在，X4 `06EC10` 不存在。不能将这些条目称为已支持的 137 款 OPPO 耳机。文件未记录获取时间、内容版本或配套提取链；作者的官方来源声明不能替代可复建的版本出处。

| 维度 | OppoPods1 | OppoPods2 |
| --- | --- | --- |
| ANC | 更完整地按型号父/子 `protocolIndex` 生成位图和反查状态，同时保留手工配置档 | 使用官方表判部分能力，常规模式和等级仍有较多固定映射 |
| 身份与功能 | SPP 产品 ID、型号配置及部分设备回报共同参与；名称和手选有回退路径 | 同样使用产品 ID 与配置，并有部分回报驱动的游戏 feature 选择 |
| 通知与状态 | 有通知能力订阅和增量接收处理，但长度、请求序列及部分状态分支存在局限 | 有设置后补查和同步；一次 read 当一包、固定序列等实现需额外核对 |
| 系统集成 | Hook 和独立模式共享控制；包含 LE Audio 连接时序经验 | HyperOS 集成与 Android 蓝牙栈内 LE Audio 协调更突出 |
| 官方配置更新 | 未见与本项目同等的内置/云版本选择和双来源更新链 | 同样未见此完整链 |

两者的“导入欢律资源”是通过 root 读取欢律私有缓存中的 PNG，不能据此称为 APK 解析、能力配置导入或官方组件代理。它们的自动 Hook 先依赖名称含 `oppo`；手动连接路径存在，因此这是自动发现的限制，不能扩大为其他品牌在所有路径上都不可连接。

## 值得保留的经验

官方 APK、配置、数据库、日志和动作抓包互证的方法可用于增加硬件样本。ANC 主模式、用户子档和固件实时等级应分开；EQ 应以设备返回的 ID、频率、参数和容量为依据；通知能力与控制命令能力是不同信息。PID 晚到、EQ 补查、通知先于 GET、重连和多副耳机身份关联也适合作为独立验收场景。

LE Audio 在线而 A2DP/HFP 离线时，SPP 控制会话不一定应立即拆除。OppoPods2 的 HFP 初始化、设备组和双耳连接协调值得作为调查线索，但其 Android 私有 Hook 不能直接移植为 Windows 普通应用能力。AT 应答、profile 状态或 ACL 存在均不能单独证明双耳 CIS、实际播放、LC3 或 LHDC/无损音频成功。

## 对本项目的适用边界

继续以固定第一方 APK、所选来源配置及设备实际回报建立适配证据，将两项目用于交叉检查和硬件验收线索。单字节长度、固定 seq、一次 read 当一包、未知设备乐观开放及仅凭 UI 更新判断成功等实现不宜直接复制；未知布局应保留原包，写操作需相应命令与状态证据。

当前本项目已应用官方配置和美术，并据新的 APK/固件研究修复校准流程。本页对照基线为 2026-10-08，不代表后续修复前后行为不变。配置登记、transport 允许和真机验收依然是不同范围；已实现的版本条件检查也不意味着原 JSON 的所有条件都已消费。当前行为分别见 [设备配置](../cloud-device-configuration.md)、[美术摘要](../online-artwork/README.md) 和 [校准修复](../calibration-repair.md)。

后续优先增加 X3、Free4、Air4s 与 X4 的实际 GET/SET/ACK/读回样本，再分别验证连接时序与 Windows LE Audio 接口。第三方笔记中的推测须回到具体调用链核对；例如本项目双 APK 复核确认 ANC 主/子控件均可用 `0404 [01,01,…]`，不能仅凭旧笔记推断子档必须使用 `01 02`。

两个上游快照均声明 GPL-3.0；本次整理结论与定位，没有复制其实现代码或耳机图片。普通克隆不包含本地 `research/upstreams/` 和完整研究原件，详细源码行号与统计复核需要对应固定快照。
