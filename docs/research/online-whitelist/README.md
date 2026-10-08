# 欢律 / 无线耳机云端白名单获取验证

后续会话从 [设备适配交接](../device-adaptation-handoff.md) 和 [配置专题](../cloud-device-configuration.md) 开始。读取固定成功样本可运行 `python -B -X utf8 tools/research/accept_cloud_research.py`，新报告写入 `artifacts`，不覆盖本页原始证据。下文保留原获取阶段记录；重新获取会创建新 run 并更新 latest 指针。

本轮于 **2026-10-07 09:50 UTC（香港时间 17:50）**实际请求了两个 App 的白名单服务。两路元数据 POST 与两路配置 GET 全部返回 HTTP 200；元数据业务码均为 0。使用 `http://127.0.0.1:59991` 代理，串行请求、请求间隔至少一秒，没有访问手机、运行 APK 或修改桌面应用代码。现有 Python 环境足够，**不需要 AVD、root 或 ADB**。

已恢复两 APK 内的请求认证材料，验证内置白名单的 AES-GCM 解密，并对实际下载内容完成官方缓存格式的加密、落盘、解密和逐字节比对。篡改密文与错误密钥测试均被 GCM 拒绝。

## 实测结果

| 配置来源 | 内置有效内容版本 | 云端有效内容版本 | 下载字节数 | 主条目 / 展开产品 ID | 按官方规则选择 |
|---|---:|---:|---:|---:|---|
| 欢律 | 117006004 | 117006007 | 240331 | 85 / 85 | 云端 |
| 无线耳机 | 17006002 | 17006007 | 379989 | 143 / 144 | 云端 |

两份云文件的元数据文件版本均为 `2609280001`，文件名分别是 `hey-17.6-0928.headset` 和 `melody-17.6-0928.headset`。**扩展名 `.headset` 的实际内容是 UTF-8 JSON，并不是云端加密包。**响应为 `application/octet-stream`，此次没有 HTTP gzip 编码。

下载原文 SHA256：

- 欢律：`0c0288256928824090543dfaea5bd35e6f4538f190e2e9b673f871af7ab46127`
- 无线耳机：`d75a8612981ece2011617a0ed4314ade8be766e8365a78d58b4d15b1c10ce4a6`

服务端只提供 `downloadUrl/fileName/id/size/version`，没有返回配置文件 SHA256 或独立响应签名。上述摘要是本地证据指纹；不能称为服务端签名认证。服务器的 `size` 在 wire JSON 中是数字字符串，实际分别与下载的 240331、379989 字节相等。

相对内置，两路名单均没有新增或删除产品 ID，各有 7 个产品配置发生变化。共同新增内容是五款产品的 `dialogTags`，以及 X4、OnePlus Buds Pro 4 的 `firmwareTrack`；系统版还移除了后两款的 `immersiveRecord`。X3/X4 的 EQ 字段未变化，分别仍为默认六段与显式十段。

系统版展开范围包括 141 个耳机产品 ID、3 个音箱产品 ID。主条目 `065854` 的子配置显式声明了 `065814 / OnePlus Nord Newton`，所以 143 个主 ID 展开为 144 个识别 ID。欢律的 85 个 ID 全部包含其中。这是此次两个配置源的识别数据，不是 Windows 控制协议的实机验收结论。

## 三种解密 / 封装链

| 对象 | 处理方法 |
|---|---|
| APK 内的网络认证材料 | Base64 → 前 12 字节 nonce → AES-GCM；key 为 APK 签名证书 DER 的 SHA256；解密结果直接作为请求 HMAC 材料，没有 gzip |
| APK 内置白名单 | 前 12 字节 nonce → AES-GCM → gzip 解压 → UTF-8 JSON |
| 云端配置及私有缓存 | HTTPS 下载 UTF-8 JSON → gzip → AES-GCM → 本地缓存；读取缓存时 AES-GCM 解密 → gzip 解压 → UTF-8 JSON |

GCM tag 为 128 位，位于密文末尾。官方缓存 key 是每次安装本地随机生成的 32 字节，Base64 存放在 `melody-model-settings` SharedPreferences 的 `whitelistSecretKey`；它不是云端下发密钥，也不是网络 HMAC 材料。

研究脚本为缓存测试生成独立、临时的随机 key，在同一进程内写入并读回文件。保存的 `*.cache-decrypted.json` 与对应原文逐字节一致；key 不保存。因此离线复核可检查记录与文件摘要并独立重做内存中的加解密测试，但不能声称跨进程再次解密原来的临时密文。重新运行获取脚本会生成新的缓存验证样本。

## 请求契约与版本纠正

接口：`POST https://iot-earbuds-cn.heytapmobi.com/v1/earphone/new/latestWhiteList`。

请求体只有三个字符串字段，没有当前白名单版本参数：

```json
{"platform":"android","channel":"2","versionCode":"17006004"}
```

系统版使用 `channel="1"`、`versionCode="17006002"`。请求头使用 `Content-Type/appid/ts/nonce/sv/sign`，其中 `appid=earphone`、`sv=v1`；`sign` 是准确序列化的 UTF-8 JSON body 字节的 HMAC-SHA1 小写十六进制。此次 HMAC 不包含 `ts/nonce`。

**此次复核纠正了此前静态报告中的版本来源和指令解释：**

| 来源 | manifest 安装版本号 | application meta-data.versionCode | 内置白名单载入后的有效版本 |
|---|---:|---:|---:|
| 欢律 | 117006005 | 17006004 | 117006004 |
| 无线耳机 | 17006003 | 17006002 | 17006002 |

请求使用 `application meta-data.versionCode`，并非 manifest 安装版本号。欢律内置加载器的 `0xb0` 指令是 **add-int/2addr**，因此内置内容版本是元数据版本**加一亿**，不是此前误报的减法。该加法不能应用到请求 body 或云文件元数据版本。

三个版本各有用途：

- API body `versionCode`：当前 App 的元数据兼容版本。
- `WhitelistInfoDO.version`：云文件版本 long，和本地记录作“是否相等”比较，以判断是否需要下载。
- `WhitelistContentDO.versionCode`：能力内容版本 int，用于判断实际采纳哪份配置。

`compatToContent()` 对非 null 的 `compatWhiteList/compatVersionCode` 优先采用，包括空列表和 0。欢律云 JSON 的 `versionCode=0`，但 `compatVersionCode=117006007`，实际版本必须取后者。

官方启动策略选择内容版本较大的一整份配置；相等时内置优先。运行中仅在新内容版本严格更大时发布新配置。因此完整复刻官方行为时，“优先云端”应理解为**优先兼容且更新的云端内容**，不是无条件覆盖内置。此次两路云配置都满足该条件。

## 可复现证据

关键 DEX 定位如下，偏移为对应 DEX 的绝对字节偏移：

| 事实 | 欢律 | 无线耳机 |
|---|---|---|
| GlobalUrlImpl 的认证材料来源 | `classes.dex Ll4/a.d`，code `0x62bacc`、literal `0x62bafc` | `classes2.dex LC7/a.c`，code `0x120c44`、literal `0x120c74` |
| 认证材料 AES-GCM 解密 | `classes3.dex common/util/E.x`，`0x25a480` | `classes.dex LY6/b.F`，`0x21ce7c` |
| 三字段请求体构造 | `classes3.dex net/F.t`，`0x2c7320` | `classes2.dex net/D.q`，`0x20a494` |
| 内置版本覆盖 | `classes3.dex A9/a.get`，`0x108684–0x1086ba` | `classes.dex M6/h.get`，`0x1e895a–0x1e8962` |
| 兼容字段转换 | `classes3.dex WhitelistContentDO.compatToContent`，`0x2522d4` | `classes.dex` 同方法，`0x58db58` |

实际获取目录：[本轮完整结果](../../../research/online-whitelist/runs/20261007T095049.319796Z/results.json)、[名单与版本分析](../../../research/online-whitelist/runs/20261007T095049.319796Z/analysis.json)、[独立离线复核](../../../research/online-whitelist/runs/20261007T095049.319796Z/verification.json)。完整静态契约见 [static-contract.json](../../../research/online-whitelist/evidence/static-contract.json)。

独立离线复核共 **52 项全部通过**，包括 APK 指纹、内置解密字节一致性、云端文件与记录一致性、元数据大小、缓存记录、新密钥内存加解密、兼容字段优先级、版本选择及 gzip 解压上限。元数据 HTTP 原文未保留，无法再次计算其传输摘要；保存的临时缓存密文也因 key 未保留而不能跨进程再次解密。这两项限制在验证报告中明确列出。

每个 App 子目录保存：内置原始密文、内置 JSON、云下载原始字节、云 JSON、删去完整下载 URL 的元数据、本地缓存测试密文、缓存解密结果。实际认证材料、请求签名/nonce/headers 和测试缓存 key 不写盘。

## 运行方法

在仓库根目录运行；使用已具备 cryptography 的 Python 3.11+ 环境，不安装依赖：

```powershell
$whitelistPython = 'python'
& $whitelistPython -B -X utf8 docs/research/online-whitelist/scripts/fetch_whitelist.py --offline
```

重新在线获取两个配置源：

```powershell
& $whitelistPython -B -X utf8 docs/research/online-whitelist/scripts/fetch_whitelist.py --online --proxy http://127.0.0.1:59991
$whitelistLatest = Get-Content research/online-whitelist/latest-run.json -Raw | ConvertFrom-Json
$whitelistRun = Join-Path docs/research/online-whitelist $whitelistLatest.run
& $whitelistPython -B -X utf8 docs/research/online-whitelist/scripts/analyze_whitelist.py --run $whitelistRun
& $whitelistPython -B -X utf8 docs/research/online-whitelist/scripts/verify_run.py --run $whitelistRun
```

不传 `--proxy` 时直接连接，并忽略系统环境中的代理变量。`--packages` 可以只选择其中一个包名。获取器默认离线，只有 `--online` 才访问网络；HTTP 403/429 后停止后续网络请求。

研究工具额外限制输出目录、响应大小和重定向，正常验证 TLS 证书；没有复刻尚未核实的 Android TLS pinning 或所有 OkHttp 行为。这些限制是研究工具规则，不是官方 App 行为。研究没有将配置接入桌面应用，没有实现设置中的配置源切换，也没有操作耳机。


## 2026-10-08 归档位置与提交范围

研究说明和复现脚本保存在本目录；原件、配置、静态取证、运行记录及派生数据保存在 [`research/online-whitelist/`](../../../research/online-whitelist/)。历史 JSON 内的采样路径与摘要保持原值，文档链接和工具入口指向当前位置。目录总览见 [研究资料索引](../README.md)。
