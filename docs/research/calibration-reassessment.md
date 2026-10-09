# 黄金听感与耳机贴合度重新研究（2026-10-09）

本文为公开整理稿；[整理前完整原文](../../research/migrations/organization-2026-10-09/originals/docs/research/calibration-reassessment.md)保存在本地研究档案，不随普通Git clone提供。历史版本、来源及验证范围保留，完整研究对象与产物由 `research/` 保存。

本轮从原始APK完整重新研究这两个功能，再以用户提供的Enco X4 v116可信固件重建包交叉核对。**研究开始时0.8.8实现存在错误字段与缺失流程。** 初始静态阶段之后，用户授权追加实测：贴合度双耳良好，黄金听感12项、滤波、保存/读回及旧方案公开字段恢复均已完成。研究与临时工具实测阶段未修改应用代码；用户随后授权的[现有组件修复](calibration-repair.md)另记源码改动和验证，以下研究比较仍描述修复前基线。

完整过程与结果位于 [research/calibration-reassessment-20261009](../../research/calibration-reassessment-20261009/README.md)：[研究过程](../../research/calibration-reassessment-20261009/WORKLOG.md)、[无线耳机](../../research/calibration-reassessment-20261009/wireless-earphones.md)、[欢律](../../research/calibration-reassessment-20261009/heymelody.md)、[固件](../../research/calibration-reassessment-20261009/firmware.md)、[协议与状态机](../../research/calibration-reassessment-20261009/PROTOCOL.md)、[Windows差异](../../research/calibration-reassessment-20261009/CURRENT-IMPLEMENTATION.md)。这里保存受版本管理的纠正摘要。

## 固定输入

| 来源 | 实际身份 | SHA-256 |
| --- | --- | --- |
| 第一方无线耳机 | `com.oplus.melody` 17.6.3，17006003 | `c1149df9e24ed85bf84f7c81c712cee794299d2b95057c6e221fb547f2717059` |
| 欢律 | `com.heytap.headset` 17.6.5，117006005 | `eacb314e092cfb6c2f075eee834498412255cadf184978f3dc16400f6e1dd5d4` |
| Enco X4 SW_VER=116 AP | 用户提供重建包的 `binary/ap.bin` | `0924bfe293f7d0b40dffb012eb4fdf4014374e4cc60c85b32a36fe7716802f24` |

JADX 1.5.3 重新导出两 APK 的全部代码/资源视图，关键 serializer、判断和 parser 用原 DEX 指令/fallback 复核；原日志分别有17/22个反编译错误，未视为全量正确源码。重新认证解密内置配置，并记录APK/DEX/证书/资源哈希。固件包已验证的人工模块与自动C分开使用，业务handler由原始派发表、callback及机器码定位，未修改可信输入包。

## 黄金听感：新的结论

黄金听感包含可选耳扫、主观测听、两类滤波结果、原音/增强试听、方案保存/历史选择；feature `0B` 只控制启用。自适应听感 `hearingOptimize/38` 是另一项功能。

测试纯音在X4耳机内部产生。主观测听type1..6由固件控制与波表步长共同证实为 **500、1000、2000、4000、6000、8000 Hz**，不是“力量感”等结果文案。该映射目前只对X4 v116有机器码证据；signed dB仍是控制参数，不等于已做实际声压校准。

| 旧结论或当前行为 | 重新核对结果 |
| --- | --- |
| `hearingJudgeDb`选择V1/V2 | 字段只用于应用记录时的左右耳差异提示；earScan流程进入slider，无earScan走旧yes/no。当前Windows路由影响32个耳扫条目中另外24项 |
| `8116` offset5=总包数、offset6..7=key | offset5=耳侧，offset6..7=LE16浮点样本总数；外层关联key不在这里 |
| filter按包数量完成 | App按每耳浮点样本数量完成，384与其他格式分别处理；X4常规一条回复含左右共60个IIR系数 |
| 耳扫曲线套测听filter或未实现 | 独立 `011F/811F`，回复11字节头，含LE16采样率与样本总数，无enhanceType；X4左右共72个系数 |
| `040E action2/4` 需要count+UID | 两APK均为单字节 `[02]` / `[04]`。X4 action4不读body；action2通过前置检查后解析额外body，当前六字节action2在成功路径使工作RAM item_count清零 |
| 收到耳扫结果即可开始测听 | App先 `040D [04,00,UID]` 停耳扫，等ACK后 `040D [02,01]` 开测听 |
| 先保存耳扫，再保存测听 | App先 `0411` 描述，再 `040E action3`，ACK后 `0415 action3`，最终 `0403[0B,01]` 与GET同步；0411先发但不等其成功ACK |
| 2..40字符即固件可接受名称 | APK字符规则是本地recordName，wire Entity.name通常为日期；Windows直接提交名称需按X4的1..39 UTF-8字节限制。空名/40字节以上status2，未写NV，但之前已更新工作RAM |
| 麦克风权限说明手机负责测听 | App麦克风权限用于MediaPlayer audioSession的Visualizer；结果页音乐由手机播放，测听纯音由耳机产生 |

核心正常payload如下，不含EncoLink外帧：

| 命令 | 原厂payload |
| --- | --- |
| `040D` 测听 | `[02,01]`开始、`[02,00]`停止 |
| `040D` 耳扫 | `[04,01,UID:BE32]`开始、`[04,00,UID:BE32]`停止 |
| `040E` 单频 | `[03,01,side,type,db:i8]`，五字节，无UID/name |
| `040E` 暂停/停试听 | `[04]` / `[02]` |
| `040E` 试听/保存 | `[action,count,(side,type,db:i8)*count,UID:BE32,name:UTF8]`，action1/3；保存count12 |
| `0115` 成功读回 | `[00,action,count,items,UID:BE32,name:UTF8]`，X4正常action3/count12 |
| `0116` 请求 | `[count,items,UID:BE32]` |
| `8116` 成功数据 | `[00,UID:BE32,deviceType,totalSamples:LE16,packetId,enhanceType,floatLE32*N]` |
| `011E` 成功读回 / event14结果 | status或event前缀后 `[action,scanLen:LE16,scanBlob,UID:BE32]` |
| `011F` 请求 | `[scanLen:LE16,scanBlob,UID:BE32]` |
| `811F` 成功数据 | `[00,UID:BE32,deviceType,samplingFrequency:LE16,totalSamples:LE16,packetId,floatLE32*N]` |
| `0415` 试听/保存 | `[action,scanLen:LE16,scanBlob,UID:BE32]`，action1/3；停试听为 `[02]` |
| `0411` 单项描述 | `[01,01,01,00,restoreDesId]`，count与type+LE16长度+值 |

UID由App按signed BE32解释；固件native LDR/STR保持四字节原序，不能据其CPU小端反推wire小端。主观单频count1与完整保存count12是X4真实分流。X4常规scanBlob168字节、`0116`250字节回复、`011F`299字节回复均为样本特征，不外推到全型号。

停止扫描ACK不保证异步清理完成：X4仍可在随后type2 start返回忙状态15，应用需保留失败处理。

旧版测听保留-60..20、初始0、yes/no bracket与原厂分支收敛规则；新版为12档 `[-55,-49,-41,-35,-25,-19,-11,-5,3,7,13,17]`，初始index5/-19，300ms协调更新，两耳各六项。滑块实际由earScan后的流程进入；首次打开新版外层而无earScan时，start成功回调仍切旧yes/no。hearingJudgeDb只控制应用时左右同频阈值在12档表中index差≥3的提示，不是UI路由字段，也不是固定18dB判定。

`0411`的五字节App TLV与固件14字节NV存取不能混为同一长度规范：X4 handler不解析TLV而存前14字节，`0118`读后仅回status和前五字节。短请求后的额外缓存内容未闭合，不应推导App必须补九字节。

阈值完成后停止测听，按需请求两类曲线；5秒守卫检查左右曲线均存在，耳扫产品还检查耳扫左右曲线。不完整时弹出重试/退出，不是延时后无条件进入结果页。

原厂多条历史主要在手机数据库，包含描述、参数、耳扫及曲线；耳机保存当前方案。`EncryptDao`仅将MAC做SHA-256映射，profile data仍是JSON/SQL TEXT，表按UID主键、无account列。App结果页可在应用成功前写手机历史，手机有记录不能证明耳机已应用。Windows普通JSON历史、替换和完整回读校验是自身设计。

App文字描述按已有描述/相同triplets复用，否则按耳机enhanceType在1..6、7..12、13..20三组内随机挑选；这不是手机听力评分。用户重命名仅更新本地recordName，wire Entity.name保持另一字段。

## 耳机贴合度

两APK与X4共同支持 `0405 [01]` 开始、`[00]`停止；event4为 `[04,01,leftResult,02,rightResult]`，去掉event字节后固定四字节，没有count。应用显示0=一般、1=良好、6=较差，其他值为错误。启动ACK0只代表接受命令。

App有双耳入耳/连接条件、15秒UI等待、异常与退出stop。X4实际检测有12秒总timeout，前置检查共享声学校准忙碌、通话、TWS、佩戴、低电量和audio focus：9通话、10 TWS未连、11佩戴、15忙、16低电量等均须保留原码，不能合并成贴合较差。音乐播放不等于busy15，固件有音频focus仲裁；Windows音乐自动暂停/恢复仍需实测。

佩戴查询是`0109`，`0107`实际上查询OTA升级能力。当前CalibrationEngine和DailyRefresh均误用0107，可能错误阻止正常佩戴检测；本次中间研究也曾犯此错误，已经[原DEX补充纠正](../../research/calibration-reassessment-20261009/status-query-supplement.md)。X4的event广告、注册和主动推送不等价：0205三项02为拒绝，而真实event4/14仍到达，详见[通知补充](../../research/calibration-reassessment-20261009/notification-registration-addendum.md)。

固件当前路径为main的custom leak检测与左右耳TWS同步；相邻CP1 leak算法到该main共享结果的完整消息映射未闭合。`eartip_detect`另有内部状态机，不能只凭命名当成`0405`的唯一实现。

## 配置、现有实现与后续交接

重解内置配置得到：主hearingEnhancement34项、hearingEnhancementNew1项、hearingJudgeDb8项、earScan32项、fitDetection31项；两来源共有型号这五字段无差异，其他字段/云配置不能因此取并集。X4 `06EC10` 的五字段依次为1/0/1/1/1，欢律minVersion16010000；第一方还有LE过滤/系统会话条件。完整版型号矩阵见 [calibration-models.json](../../research/calibration-reassessment-20261009/evidence/apk-config/calibration-models.json)。

当前Windows的V1/V2路由、filter结构/聚合、耳扫停止阶段、保存顺序/描述、名称字节边界、结果试听与异常通知需要后续修正；既有校准18项测试是旧模拟基线，不能证明此次纠正后的协议已实现。优先按原DEX/固件证据修codec与流程，再开展 [逐阶段真机对照](../../research/calibration-reassessment-20261009/PROTOCOL.md)。

本轮没有手机HCI抓包、实际声压校验、完整算法差分和动态跨核trace。追加实测使用research临时原始控制工具，不能证明当前0.8.8完整客户端已经符合修正后的契约。

## 追加实机结果

完整过程、失败与重试、用户操作、原始帧和恢复范围见[实测报告](../../research/calibration-reassessment-20261009/LIVE-TEST.md)。贴合度收到`0401010201`，左右均良好。黄金听感收到真实168字节耳扫结果，用户完成12项，0116/011F分别返回60/72个系数；新方案保存、启用、读回一致，随后恢复原阈值、blob、UID、名称、描述编号与开关，四类GET与备份逐字节相同。

实测曾发现临时研究UI漏绑滑块发送回调：界面调大但线上始终是最低档-55，而用户报告声音过小或听不到。该轮作废；修正后每项默认-19，实际发送/ACK与12项确认全部一致。这个缺陷属于研究工具，未改应用UI。首次耳扫后断线根因未知；公开字段恢复不等于全NV恢复，0411描述隐藏尾九字节无法通过0118备份。
