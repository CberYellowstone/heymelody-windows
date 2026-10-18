# 顶部电量标识的欢律来源

2026-10-19，顶部电量区域的左耳、右耳和耳机仓标识采用欢律 App 自身的矢量形状。左右是带柄耳塞轮廓，仓是充电盒轮廓。本次适配沿用当前界面前景与透明度。[欢律单源规则](heymelody-only.md)不变。

## 原件与资源映射

本机原件为 `research/apps/欢律.apk`，包名 `com.heytap.headset`，版本 `17.6.5`，versionCode `117006005`，长度 `26297357` 字节，SHA256 为：

`eacb314e092cfb6c2f075eee834498412255cadf184978f3dc16400f6e1dd5d4`

版本与哈希也由既有 `research/earbud-artwork/sources.json` 记录。APK 内的资源文件名已混淆，下表通过资源表映射得到，不能仅凭文件名猜测来源。

| 角色／逻辑资源名 | 原 APK entry／字节 | 原 entry SHA256 |
| --- | --- | --- |
| 左耳 `melody_ui_battery_left_ear_hint` | `res/D0.xml`／1776 | `fcbc6a23093a1931c3eb54cddd584b60c1504fbd4811a0e485f21334408cff27` |
| 右耳 `melody_ui_battery_right_ear_hint` | `res/_3.xml`／1760 | `ffb4b37bca37458e05dd00995003caa39fc8433d2f247cc41f81d46fca0c752d` |
| 仓 `melody_ui_battery_box_hint` | `res/lZ.xml`／2256 | `96f1690854b43ccef4a5e19b89c803cfc600363e68a93ea8c4b4a559effbf3e6` |

反编译文件位于本机 `research/calibration-reassessment-20261009/decompiled/heymelody/resources/res/drawable/`。下表为该目录中的原文件；它们的 pathData 与原 APK 编译 XML 解码结果逐字符串一致。

| XML 文件 | 字节 | 文件 SHA256 |
| --- | --- | --- |
| `melody_ui_battery_left_ear_hint.xml` | 1511 | `d72e1e67e95493315d027288dbbeb507042863ec43c03be35cdbcf2ae150e576` |
| `melody_ui_battery_right_ear_hint.xml` | 1496 | `4b14c52d04f427654b5b818b60fc20883a48360443670fd2f3fb4e41a76d5d76` |
| `melody_ui_battery_box_hint.xml` | 1917 | `aeae02a10c86a236e0a445016413f4468604db97612244013e3e9f04fec98d45` |

## 实际用途与桌面适配

同一反编译树的 `sources/G4/C1239a.java`（`BatteryViewAdapter.onBindViewHolder`）把角色 1、2、3 分别绑定到左耳、右耳、仓资源。`heymelody_app_battery_item.xml` 中的 `mIcon_which` 位于 `BatteryItemView` 旁，证明三者用于区分电量项的角色。

三个 vector 均为 19×16 viewport。左右各两个可见 path，仓一个；每个 path 显式使用 `evenOdd`，无 stroke、无 group transform。左右的 clip 矩形为 `(0,0)–(19,16)`；仓为 `(1.5,0)–(17.5,16)`，原文件有三层相同裁剪。

原 Android 文件使用主题颜色属性及 `fillAlpha=0.54`。这里仅将其记作来源事实；桌面适配保留原 pathData、每条路径的填充规则与裁剪，等比居中，沿用当前原生风格控件的前景和透明度，不套用源 alpha。应用实现见 [AppIcons.cs](../../src/HeyMelody.App/AppIcons.cs)。

这些是 App 专用界面几何，不属于设备云美术索引。现有设备美术闭包仍为 365 条记录、634 个依赖文件加 1 个补充扫描 PNG，共 635 文件；图标不授予型号控制权限，也不恢复已退出支持来源的配置、缓存或美术。

## 保全、许可与验证边界

本轮改写前文档原稿、大小／SHA256、七份已跟踪静态契约基线，以及完整资源溯源收据位于本机 `research/heymelody-battery-icons-20261019/20261018T162129Z-6ac5afbc/`。该档案沿用研究目录忽略规则，不随普通 clone 提供；原 APK 和反编译文件未改写，普通构建不依赖它们。

这些图形属于原权利人的厂商素材。来源可核实不表示获得新的许可，项目源码许可不重授素材权利；见[资源权利声明](../../packaging/licenses/Extracted-Earphone-Resources-NOTICE.txt)。

本专题的核验范围为本地原件、资源哈希、角色绑定、几何映射和文档链接。运行及窗口检查由本次应用验证单独记录；此处不声称真实耳机、全部型号或 ARM64 硬件已验证。
