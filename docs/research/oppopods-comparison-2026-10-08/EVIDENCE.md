# 源码定位与证据性质

本文路径相对本目录。`P1` 指 `../../../research/upstreams/OppoPods1/`，`P2` 指 `../../../research/upstreams/OppoPods2/`，`主项目` 指 `../../../`。行号绑定 [SNAPSHOT.json](SNAPSHOT.json) 中提交，不代表上游未来版本。链接指向本地原文件；行号写在链接旁，方便编辑器定位。

证据性质：**源码行为**指已追踪到的实际调用；**作者声明**指 README／注释／逆向记录，未独立复现其取证；**静态推断**指可由控制流推出的风险，未真机复现；**历史实测记录**指主项目或作者已有记录，不是本轮执行。

## A1

白名单内容与统计。

- P1 [device_models.json](../../../research/upstreams/OppoPods1/app/src/main/assets/device_models.json)，L1：单行 JSON，根只有 `whiteList`。
- P2 [device_models.json](../../../research/upstreams/OppoPods2/app/src/main/assets/device_models.json)，L1：与 P1 完整字节 SHA-256 相同。
- 本轮解析计数：137 主条目／137 唯一主 ID；OPPO 56、OnePlus 32、realme 49；3 个 realme type=S；SPP true 114、false 6、缺失 17；全部有 `function`。
- 在主 ID 集合中，X3 `067410`、Free4 `06C010` 存在，X4 `06EC10` 缺失；未展开 child ID 重新计数。
- 性质：本轮直接解析与摘要计算。完整元数据见 SNAPSHOT。

## A2

官方来源声明和真正加载入口。

- P1 [DeviceModelRegistry.kt](../../../research/upstreams/OppoPods1/app/src/main/java/moe/chenxy/oppopods/pods/DeviceModelRegistry.kt)，L10–18：声明 HeyMelody 云端 whiteList；L36–62、L66–87：读 assets 并建立 ID 索引；L128–147：PID 与名字回退。
- P1 [DeviceCapabilities.kt](../../../research/upstreams/OppoPods1/app/src/main/java/moe/chenxy/oppopods/pods/DeviceCapabilities.kt)，L60–61：只保留 UI 使用的字段，大量官方能力不解析。
- P2 [DeviceModelRegistry.kt](../../../research/upstreams/OppoPods2/app/src/main/java/moe/chenxy/oppopods/pods/DeviceModelRegistry.kt)，L8：bundled official model table；L24–53：加载 `whiteList`；L55–71：PID 优先、名称标准化精确匹配。
- P2 [DeviceCapabilities.kt](../../../research/upstreams/OppoPods2/app/src/main/java/moe/chenxy/oppopods/pods/DeviceCapabilities.kt)，L44–48：实际调用优先查询 PID，再回退名称。
- 全仓文件列表及源码检索未发现这份型号表配套的 APK 提取／解密脚本、双源版本选择器或云端表更新器。JSON 无版本、URL、APK 摘要或获取日期。
- 性质：加载链为源码行为；“该资产来自官方云端”为作者声明；未发现工具仅限定于本地两仓库，不能据此断言作者从未有过外部工具。

## A3

第一方逆向方法。

- P1 [HeyMelody_Bluetooth_Protocol_Notes.md](../../../research/upstreams/OppoPods1/docs/HeyMelody_Bluetooth_Protocol_Notes.md)，L3–13：JADX 官方欢律、UUID+SDP、varint 等研究；L32–50：官方类与 UUID。
- P1 [HeyMelody_Official_App_Protocol_Findings.md](../../../research/upstreams/OppoPods1/docs/HeyMelody_Official_App_Protocol_Findings.md)，L17–46：数据库、日志、UI config、加密缓存用途；L79–87：协议摘要；L1233–1251：白名单缓存记录；L1278：channel 需进一步取证。
- 性质：作者研究记录。笔记中的本地私有数据路径和 APK 混淆类名不等于本轮拥有对应原始样本，也不等于代码已经实现全部发现。

## A4

欢律资源导入只导图片。

- P1 [RootManager.kt](../../../research/upstreams/OppoPods1/app/src/main/java/moe/chenxy/oppopods/utils/RootManager.kt)，L7–12、L51–55、L61–101：扫描 `melody-model-download/control_…/res/image`，读取图片 bytes。
- P1 [MelodyImageImportDialog.kt](../../../research/upstreams/OppoPods1/app/src/main/java/moe/chenxy/oppopods/ui/dialogs/MelodyImageImportDialog.kt)，L155–179：导入到 PodImageStore。
- P2 [RootManager.kt](../../../research/upstreams/OppoPods2/app/src/main/java/moe/chenxy/oppopods/utils/RootManager.kt)，L18–29、L49–83：同类路径与 PNG 读取。
- P2 [MelodyImageImportDialog.kt](../../../research/upstreams/OppoPods2/app/src/main/java/moe/chenxy/oppopods/ui/dialogs/MelodyImageImportDialog.kt)，L57、L145–162：导入回调为图片 bytes map。
- 性质：源码行为。没有沿此入口读能力白名单、解析 APK 或代理官方控制组件。

## A5

P1 的 PID、配置档及自动发现。

- [DeviceProfileStore.kt](../../../research/upstreams/OppoPods1/app/src/main/java/moe/chenxy/oppopods/pods/DeviceProfileStore.kt)，L13–21、L118–143：自动模式及手动选择、设备绑定。
- [AppRfcommController.kt](../../../research/upstreams/OppoPods1/app/src/main/java/moe/chenxy/oppopods/pods/AppRfcommController.kt)，L175–185：建连发 `0100`、`0103`、`0200`；L287–302：PID 回来替换 profile 并补查。
- [RfcommController.kt](../../../research/upstreams/OppoPods1/app/src/main/java/moe/chenxy/oppopods/pods/RfcommController.kt)，L864–884：Hook 路径的 PID 更新。
- [DeviceModelRegistry.kt](../../../research/upstreams/OppoPods1/app/src/main/java/moe/chenxy/oppopods/pods/DeviceModelRegistry.kt)，L171–223：能力解析。
- [CapabilityProfileFactory.kt](../../../research/upstreams/OppoPods1/app/src/main/java/moe/chenxy/oppopods/pods/CapabilityProfileFactory.kt)，L24–53：能力 → profile 可见性和命令。
- [HeadsetStateDispatcher.kt](../../../research/upstreams/OppoPods1/app/src/main/java/moe/chenxy/oppopods/hook/HeadsetStateDispatcher.kt)，L261–266：自动识别名称含 `oppo`。
- [DevicePickerPage.kt](../../../research/upstreams/OppoPods1/app/src/main/java/moe/chenxy/oppopods/ui/DevicePickerPage.kt)，L84–89：所有配对设备，仅按 OPPO 名称优先排序。
- 性质：源码行为。`0100` 被发送本身不能证明其回包 bitmap 已被用作控制限制。

## A6

P1 的 ANC 父／子索引与样本。

- [CapabilityProfileFactory.kt](../../../research/upstreams/OppoPods1/app/src/main/java/moe/chenxy/oppopods/pods/CapabilityProfileFactory.kt)，L58–87：从索引产生 `01 01 <bitmap>`；L90–99：等级独立生成；L154–163：固定查询名单。
- [DeviceModelRegistry.kt](../../../research/upstreams/OppoPods1/app/src/main/java/moe/chenxy/oppopods/pods/DeviceModelRegistry.kt)，L334–374：父档发送、子档发送／归并；L357–359：作者声称 X3／Free4 主档实测。
- [Packets.kt](../../../research/upstreams/OppoPods1/app/src/main/java/moe/chenxy/oppopods/pods/Packets.kt)，L439–472：动态索引反查；L906–942：智能当前等级解析。
- [AppRfcommController.kt](../../../research/upstreams/OppoPods1/app/src/main/java/moe/chenxy/oppopods/pods/AppRfcommController.kt)，L337–340；[RfcommController.kt](../../../research/upstreams/OppoPods1/app/src/main/java/moe/chenxy/oppopods/pods/RfcommController.kt)，L928–931：实际传入动态 profile。
- [CapabilityProfileFactoryTest.kt](../../../research/upstreams/OppoPods1/app/src/test/java/moe/chenxy/oppopods/pods/CapabilityProfileFactoryTest.kt)，L53–80：X3、Free4、通透 adaptive 子档；L153–162：Air4s NC／Off 位序。
- 性质：生成与解析为源码行为；“实测成功”为作者注释；测试样本仅静态阅读，本轮未执行。

## A7

P1 能力判定和未知回退的限制。

- [DeviceModelRegistry.kt](../../../research/upstreams/OppoPods1/app/src/main/java/moe/chenxy/oppopods/pods/DeviceModelRegistry.kt)，L118–125：列表不排除所有不可控条目；L174–183、L199–201：解析 SPP／协议／isSupported；L255–271：游戏缺字段默认支持。
- [DeviceProfileStore.kt](../../../research/upstreams/OppoPods1/app/src/main/java/moe/chenxy/oppopods/pods/DeviceProfileStore.kt)，L43–72：unknown fallback 的可见性和固定命令。
- [DeviceCapabilities.kt](../../../research/upstreams/OppoPods1/app/src/main/java/moe/chenxy/oppopods/pods/DeviceCapabilities.kt)，L96–104：空间路线与游戏 feature 推导。
- [CapabilityProfileFactory.kt](../../../research/upstreams/OppoPods1/app/src/main/java/moe/chenxy/oppopods/pods/CapabilityProfileFactory.kt)，L33–53、L115–117：profile 未承接 transport 许可字段；沿配置选择游戏 feature。
- [DeviceProfile.kt](../../../research/upstreams/OppoPods1/app/src/main/java/moe/chenxy/oppopods/pods/DeviceProfile.kt)，L81–89：沿预设游戏 feature 查询。
- 全仓调用点检索：没有以 `supportSpp/isSupported` 阻止实际写入的调用链，没有以 `0100` 命令 bitmap 形成 setter 的完整可用性判定；游戏 query 并未按回报切换所选命令。
- 性质：源码行为与调用点检索；不能扩大为“任何设备都能写入成功”。

## A8

P2 设备能力、固定 ANC、发现和订阅。

- [DeviceModelRegistry.kt](../../../research/upstreams/OppoPods2/app/src/main/java/moe/chenxy/oppopods/pods/DeviceModelRegistry.kt)，L74–102：部分能力投影；L105–118：EQ 各候选数组合并；L149–176：游戏默认与 legacy／adaptive 判断。
- [Packets.kt](../../../research/upstreams/OppoPods2/app/src/main/java/moe/chenxy/oppopods/pods/Packets.kt)，L291–301：三字节 PID；L305–357：固定 ANC／声透命令；L13–26：固定 seq 和单字节长度。
- [RfcommController.kt](../../../research/upstreams/OppoPods2/app/src/main/java/moe/chenxy/oppopods/pods/RfcommController.kt)，L104、L497–498：固定控制 UUID；L646–653、L761–770：通知能力查询和订阅；L743–755：PID 更新；L889–901：PID 未知时从回报推断游戏 feature；L1171–1187：legacy Off／NC 交换。
- [AncSwitch.kt](../../../research/upstreams/OppoPods2/app/src/main/java/moe/chenxy/oppopods/ui/components/AncSwitch.kt)，L74–112：常规 ANC 选项；L115–141：固定智能／轻／中／深；L152–160：声透选项。
- [HeadsetStateDispatcher.kt](../../../research/upstreams/OppoPods2/app/src/main/java/moe/chenxy/oppopods/hook/HeadsetStateDispatcher.kt)，L69–72：手动连接路径；L94–96：自动名称含 `oppo`。
- [DevicePickerPage.kt](../../../research/upstreams/OppoPods2/app/src/main/java/moe/chenxy/oppopods/ui/pages/DevicePickerPage.kt)，L140–143：全部配对设备、OPPO 名称优先。
- 性质：源码行为。registry 没有保存／消费 SPP、UUID、协议类型及完整固件条件，没有 `0100` bitmap setter 判定。

## A9

查询时序、EQ 和日志经验。

- P1 [AppRfcommController.kt](../../../research/upstreams/OppoPods1/app/src/main/java/moe/chenxy/oppopods/pods/AppRfcommController.kt)，L116–137：EQ 状态；L257–262、L280–282、L298–299：电量与 PID 阶段补查；L315–329：设备 EQ 回包；L367–374：通知 IDs 订阅。
- P1 [Packets.kt](../../../research/upstreams/OppoPods1/app/src/main/java/moe/chenxy/oppopods/pods/Packets.kt)，L79–154：EQ 操作编码；L571–668：设备 preset 名称／频率／参数；L661–669：空响应；L878–902：通知能力 IDs。
- P1 [RfcommController.kt](../../../research/upstreams/OppoPods1/app/src/main/java/moe/chenxy/oppopods/pods/RfcommController.kt)，L970–976：Hook 通知订阅。
- P1 [BtLogStore.kt](../../../research/upstreams/OppoPods1/app/src/main/java/moe/chenxy/oppopods/pods/BtLogStore.kt)，L10–17、L27–50：带方向和语义的日志；[BtLogLabeler.kt](../../../research/upstreams/OppoPods1/app/src/main/java/moe/chenxy/oppopods/pods/BtLogLabeler.kt)：命令与事件标签。
- 性质：源码行为和作者握手时序注释。设备 EQ 解码细节需逐布局核对；不能将这些既定实现当作所有固件通用规范。

## A10

LE Audio 与音频／控制分层。

- P1 [HeadsetStateDispatcher.kt](../../../research/upstreams/OppoPods1/app/src/main/java/moe/chenxy/oppopods/hook/HeadsetStateDispatcher.kt)，L67–110：LE 事件独立触发 SPP；L236–257：LE→经典名字回退、检查其他 profiles；L279–297：已有连接 bootstrap。
- P2 [LeAudioConfig.kt](../../../research/upstreams/OppoPods2/app/src/main/java/moe/chenxy/oppopods/config/LeAudioConfig.kt)，L8–22：默认关闭、MAC 白名单空、HFP 拦截默认关闭、vendor／mask 默认。
- P2 [OppoLeAudioHook.kt](../../../research/upstreams/OppoPods2/app/src/main/java/moe/chenxy/oppopods/hook/leaudio/OppoLeAudioHook.kt)，L45–78：Android HFP 接口与 AT 应答；L88–105：连接后延迟 VDSP；L111–117：发送与 ACK。
- P2 [LeAudioConnectionHook.kt](../../../research/upstreams/OppoPods2/app/src/main/java/moe/chenxy/oppopods/hook/leaudio/LeAudioConnectionHook.kt)，L21–24：参考 OPPOLeaConnect、HFP 依赖和连接状态局限；L66–91：主动断开／整组暂停；L215–227：地址及 group 白名单关联；L346–370：GATT holder；L385–400：音频上下文修复与主耳激活。
- P2 [README.md](../../../research/upstreams/OppoPods2/README.md)，L54–79：LE Audio 策略、HFP 警示、未移植 BLE/SIRK 与 HCI 让位。
- 性质：Android 源码行为／作者设备经验。未实际验证 Android 模块，更未证明 Windows 栈具有对应可控接口、LC3 双耳播放或无损传输能力。

## A11

当前主项目资源接入、能力交集、协议和真机边界。

- [oppo-device-support.md](../oppo-device-support.md)，L32–50：APK 内置配置证书摘要→AES-GCM→gzip 链。
- [cloud-device-configuration.md](../cloud-device-configuration.md)，L48–85：认证、请求版本、云 JSON 与加密缓存、版本选择规则。旧文档中未接入部分须以当前代码为准。
- [BundledResourceStore.cs](../../../src/HeyMelody.Resources/BundledResourceStore.cs)，L35–36；[provenance.json](../../../src/HeyMelody.Resources/Bundles/provenance.json)，L2–14：两来源内置目录与来源摘要。
- [ResourceCatalog.cs](../../../src/HeyMelody.Resources/ResourceCatalog.cs)，L28–99：兼容、精确 PID、child、类型和 ExternalOnly；[ResourceCoordinator.cs](../../../src/HeyMelody.Resources/ResourceCoordinator.cs)，L70–91、L104–136、L392–408：来源隔离和严格更新。
- [VendorResourceClient.cs](../../../src/HeyMelody.Resources/VendorResourceClient.cs)，L26–40、L80–91：实际请求；[ResourceCache.cs](../../../src/HeyMelody.Resources/ResourceCache.cs)，L6–33：缓存布局。
- [NativeHeadsetService.Resources.cs](../../../src/HeyMelody.Host/NativeHeadsetService.Resources.cs)，L31–36：生产环境注入全量 resolver；[BackendSupervisor.Resources.cs](../../../src/HeyMelody.Host/BackendSupervisor.Resources.cs)，L49–84：配置变更与 worker／校准互斥。
- [DeviceProfile.cs](../../../src/HeyMelody.Devices/DeviceProfile.cs)，L7–40、L69–78：来源、功能版本、ANC／EQ；[ProfileMetadata.cs](../../../src/HeyMelody.Devices/ProfileMetadata.cs)，L112–119：已识别版本条件投影；[VersionConditionEvaluator.cs](../../../src/HeyMelody.Devices/VersionConditionEvaluator.cs)，L8–35：已识别条件的版本上下文缺失／不可解释值处理。其他未知源字段只保留 Raw JSON，不自动产生 Conditional。
- [DailyRefresh.cs](../../../src/HeyMelody.Daily/DailyRefresh.cs)，L125–157、L238–268；[DailyFeatures.cs](../../../src/HeyMelody.Daily/DailyFeatures.cs)，L41–43、L62–109、L189–199：型号／bitmap／配置／状态交集。
- [CommandCapabilities.cs](../../../src/HeyMelody.Protocol/CommandCapabilities.cs)，L3–10、L83–114：69-bit APK 映射、unknown bits 与不单独授权 setter。
- [Packets.cs](../../../src/HeyMelody.Protocol/Packets.cs)，L47–146：增量帧／分片；[DailySession.cs](../../../src/HeyMelody.Daily/DailySession.cs)，L35–69、L86–106：响应关联；[DailyApply.cs](../../../src/HeyMelody.Daily/DailyApply.cs)，L16–19、L119–147：ACK、读回与不重发 SET。
- [NativeHeadsetService.cs](../../../src/HeyMelody.Host/NativeHeadsetService.cs)，L369–382；[WindowsSdp.cs](../../../src/HeyMelody.Windows/WindowsSdp.cs)，L13–21、L82–102：已知完整 UUID 与 SDP。
- [enco-x4-0.8.1.md](../enco-x4-0.8.1.md)，L17–28：四组合 X4 只读／通知实测；[daily-feature-codecs.md](../daily-feature-codecs.md)，L3、L74–82、L94：其他硬件和未解释功能边界。
- 性质：当前源码行为与既有研究／实测记录，本轮没有重新运行。目录覆盖、满足初步 transport 条件、真机控制成功必须区分。

## A12

本项目最新 ANC 证据优先级。

- [anc-mode-prompt.md](../anc-mode-prompt.md)，L3、L21–39：两 APK 官方调用链，父／子控制的 payload；L60–74：当前行为、离线回归与仍待真机的提示音验证。
- P1 [HeyMelody_Bluetooth_Protocol_Notes.md](../../../research/upstreams/OppoPods1/docs/HeyMelody_Bluetooth_Protocol_Notes.md)，L158–160、L355–365：旧等级 type 讨论与待验证记录。
- 性质：本项目双 APK DEX 证据与第三方旧研究笔记对照。不能把旧笔记某个 type 说法当作新的权威协议结论；本轮未重跑 APK 审计。

## A13

字节流、长度、seq 和固定查询的简化。

- P1 [Packets.kt](../../../research/upstreams/OppoPods1/app/src/main/java/moe/chenxy/oppopods/pods/Packets.kt)，L24–37、L42–55：单字节长度／默认 seq；L184–192：framer 仅读单字节长度；L707–760：feature pairs 及扫描式回退。
- P1 [CapabilityProfileFactory.kt](../../../research/upstreams/OppoPods1/app/src/main/java/moe/chenxy/oppopods/pods/CapabilityProfileFactory.kt)，L155–163：固定 11 项 batch。
- P2 [Packets.kt](../../../research/upstreams/OppoPods2/app/src/main/java/moe/chenxy/oppopods/pods/Packets.kt)，L13–26：单字节长度／默认 seq；L339、L346：固定特定 seq；L437–444：`QUERY_STATUS` 的固定查询，`0B` 表示 11 个 feature，payload 共 12 字节；L788–849：结构化／扫描式状态解析。
- P2 [RfcommController.kt](../../../research/upstreams/OppoPods2/app/src/main/java/moe/chenxy/oppopods/pods/RfcommController.kt)，L709–719：read 块直接当包。
- P1 [HeyMelody_Official_App_Protocol_Findings.md](../../../research/upstreams/OppoPods1/docs/HeyMelody_Official_App_Protocol_Findings.md)，L79–86、L1255–1260：笔记已要求 varint 和动态 batch。
- 性质：源码行为；长帧／拆包影响为静态推断。本轮无抓包，不声称已复现这些错误。

## A14

状态更新和 transport 的具体限制。

- P1 [AppRfcommController.kt](../../../research/upstreams/OppoPods1/app/src/main/java/moe/chenxy/oppopods/pods/AppRfcommController.kt)，L359–364：游戏值后 return；L378–384：同包其他字段；L403–412：SET 回应日志；L426–455、L525–533：乐观 UI。
- P1 [RfcommController.kt](../../../research/upstreams/OppoPods1/app/src/main/java/moe/chenxy/oppopods/pods/RfcommController.kt)，L961–967、L981–996：同类提前 return。
- P1 [OppoRfcommSocketFactory.kt](../../../research/upstreams/OppoPods1/app/src/main/java/moe/chenxy/oppopods/pods/OppoRfcommSocketFactory.kt)，L19–21、L31–35、L42–55、L59–75：两 UUID 顺序尝试与显式 channel 15。
- P1 [HeyMelody_Official_App_Protocol_Findings.md](../../../research/upstreams/OppoPods1/docs/HeyMelody_Official_App_Protocol_Findings.md)，L450–470：官方 UUID 与作者实测 channel 的证据等级不同。
- 性质：控制流和 socket 选择为源码行为；同包其他状态更新遗漏、多设备误关联等影响为静态推断，未本轮真机复现。


## 当前资料位置

P1、P2 的源码副本分别位于 `research/upstreams/OppoPods1/` 和 `research/upstreams/OppoPods2/`。上文链接指向这些副本；原仓库地址和固定提交见 SNAPSHOT。整理仅调整访问路径，没有更换取证样本或行号。
