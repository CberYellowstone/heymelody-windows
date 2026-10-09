# Free4 丹拿版身份与颜色：独立静态审计

本文为公开整理稿；[整理前完整原文](../../../research/migrations/organization-2026-10-09/originals/docs/research/online-artwork/free4-identity-color-audit.md)保存在本地研究档案，不随普通Git clone提供。历史版本、来源及验证范围保留，完整研究对象与产物由 `research/` 保存。

2026-10-08。只解析本地两套 APK、已保存的官方白名单和资源结果；没有新增网络探测，也没有修改应用实现。

**丹拿版使用精确请求 `productId=06C010 / color=3 / moduleId=14` 已取得官方模型。** 两来源验证成功、ZIP 同为 8,820,468 字节、SHA256 同为 `efe7c68b71fe92a3b6c8e5901603cd889ac3e5f8737e612e3e42f3f6054b1a99`。此前 `-1/0/1/2` 的无资源结果遗漏了 3，只能描述当时这些请求的结果，不能推断模型不存在。现有请求认证即可获取，不需要 root、AVD 或手机私有缓存。

证据及复现：

- [完整离线证据](../../../research/online-artwork/evidence/free4-identity-color-audit.json)：配置 JSONPath、文件摘要、DEX 类/方法/偏移、指令字、成功请求与 ZIP 摘要校验。
- [离线审计脚本](scripts/audit_free4_identity_color.py)：读取 APK 数据，不执行 APK，不包含网络请求或认证材料。
- [成功请求原记录](../../../research/online-artwork/runs/20261008T051703.741040Z/results.json)。
- [2026-10-08 官方白名单](../../../research/online-whitelist/runs/20261008T051557.800877Z/results.json)。

## 请求 productId 与共享映射

两套 APK 的解密内置配置以及当前官方云配置都将普通版 `068C10`、丹拿版 `06C010` 分别列为独立产品，丹拿名称精确为 `OPPO Enco Free4（丹拿版）`。欢律 `compatWhiteList[8]/[9]`、系统耳机 `whiteList[134]/[135]` 均无 `defaultColor`；两条记录均支持 `support3DModel=1`。目标行里没有 alias、baseId、parentId、modelId 或资源 productId 重写字段。

模块 14 请求路径没有发现丹拿到普通版的转换：欢律 `Loa/e.b → Lsa/a.d`、系统 `LF8/h.b → LJ8/a.e` 保留传入 productId，单独调用颜色 resolver，然后使用传入 productId 和解析后的颜色组成缓存键。有效候选应是丹拿精确 PID `06C010`；普通版 `068C10` 的成功不能当作丹拿身份的证明。

全 DEX 扫描发现：欢律没有两目标 PID 的硬编码字符串引用；系统 APK 仅在 `classes2.dex Lw7/f.<clinit>` 中同时引用它们。完整检查该类后，列表用于 `DiagnosisUtils.filterSn`：对列表内产品的 22 字符、末尾 `00` 的诊断序列号去掉尾部两字符。这是序列号处理白名单，不是模型别名或共享资源映射。

## 颜色从哪里进入资源请求

| 链路 | 官方 APK 中的行为 | 证据位置 |
| --- | --- | --- |
| SPP 产品 ID | ACK `0x8103` 为独立产品 ID 分支；普通版/丹拿分别由白名单识别 | 欢律 `commands/m.M` 分支 `0x23EED6`；系统 `commands/n.M` 分支 `0x57EB86` |
| SPP 颜色 | GET `0x010B` 的 ACK `0x810B` 检查成功状态与数据长度，然后从 `payload[1]` 读取一个 unsigned byte | 欢律 `commands/m.M` 分支 `0x23EC26`；系统 `commands/n.M` 分支 `0x57E8D2` |
| unsigned 解码 | `aget-byte` 后 `and-int/lit16 0xFF`；没有 0/1/2 枚举限制 | 欢律 `Ln7/d.d`，`0x635F28`；系统 `Lz9/a.q`，`0x384FC4` |
| 更新设备颜色 | 将颜色放入 `Message(what=0x11,arg1=color)`，CoreService 直接调用 `DeviceInfo.setColorId(arg1)` | 欢律 `HeadsetCoreService$e.handleMessage`，`0x232736`；系统同类 `0x57177A` |
| 当前 OEM BLE 广告颜色 | `flags & 0x0020` 指示一个颜色字节，按可选字段顺序读取当前 cursor，再原样 `setColor`；helper 同样掩码 `0xFF` | `model/scan/k.c`，`0x2365C4..0x236624`；`LY6/b.q`，`0x21C940` |
| 请求颜色 resolver | 非 `-1` 原样返回；未知 `-1` 可用白名单 defaultColor；这些 OPPO 目标没有 defaultColor，仍为 `-1` | 欢律 `repository/earphone/Q.A`，`0x2E18DC`；系统 `repository/earphone/L.E`，`0x221614` |

因此没有代码依据把探测限制在 `-1..2`，也没有发现按外观色名重映射 3 的逻辑。**3 已证明是该精确 PID 的有效官方资源请求值。** 本次未收到丹拿实机 BLE 广告或 `0x810B` 抓包；APK 通用解析逻辑、模型内部银色命名、商城“星瀚银”色名都不能独立证明实机必然广播或返回 3，也不能把 3 标为官方默认色号。

离线复现（现有 Python，无需安装依赖）：

```powershell
$free4AuditPython = (uv python find --offline).Trim()
& $free4AuditPython -B -X utf8 tools/research/online-artwork/scripts/audit_free4_identity_color.py
```


## 归档说明

本页是公开整理结果，维护脚本位于 `tools/research/online-artwork/scripts/`；完整解读原稿、JSON、请求记录和模型原件保存在 `research/`。原始取证日期、摘要与请求结果未改变。目录位置见 [研究资料索引](../README.md)。
