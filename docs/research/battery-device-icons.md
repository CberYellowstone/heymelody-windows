# 顶部电量标识的欢律来源

2026-10-19，顶部电量区域的左耳、右耳和耳机仓标识恢复为圆形 L／R 字母镂空与配套充电盒轮廓，三者均从欢律 App 自身的资源重新核实。图标继承当前主题前景与透明度，字母镂空透出原背景；其它系统／Fluent 图标不变。[欢律单源规则](heymelody-only.md)不变。

## 原件与资源映射

本机原件为 `research/apps/欢律.apk`，包名 `com.heytap.headset`，版本 `17.6.5`，versionCode `117006005`，长度 `26297357` 字节，SHA256 为：

`eacb314e092cfb6c2f075eee834498412255cadf184978f3dc16400f6e1dd5d4`

版本与哈希也由既有 `research/earbud-artwork/sources.json` 记录。APK 内的资源文件名已混淆，下表通过资源表映射得到，不能仅凭文件名猜测来源。

## 当前 14×14 资源与历史样式

当前使用同一欢律 APK 内的三份 `drawable` 资源，均只有一个默认配置映射：

| 角色／逻辑资源名 | resource ID | 原 APK entry／字节 | 原 entry SHA256 |
| --- | --- | --- | --- |
| 左耳 `melody_ui_battery_device_left` | `0x7F0805A9` | `res/lb1.xml`／776 | `1da5dbfaa0c1e577f4cdf03dd9d9b3918193240903d955f058d10b886eecd03b` |
| 右耳 `melody_ui_battery_device_right` | `0x7F0805AA` | `res/Gj.xml`／1088 | `204a3a93e07f55d7da22ad8e994753c76e74be0ccc276182277c9fc38335ec8c` |
| 仓 `melody_ui_battery_device_box` | `0x7F0805A8` | `res/Mz.xml`／1740 | `4ab4b0e9f6065df8e76e71e6bfaecfadf264802ca28ed3bfedbdd3725f36f857` |

反编译原件位于本机 `research/calibration-reassessment-20261009/decompiled/heymelody/resources/res/drawable/`：

| XML 文件 | 字节 | 文件 SHA256 |
| --- | --- | --- |
| `melody_ui_battery_device_left.xml` | 534 | `e7cfaa358d0e0f6bf450565a721cc28156fab59e165c2dfd5fa78b858da0250d` |
| `melody_ui_battery_device_right.xml` | 843 | `7914816e45d9d8b59b694d89eedc0f07d85b9613dc510f1e1f3986f6b4e3ff97` |
| `melody_ui_battery_device_box.xml` | 1495 | `efea527d003301685d9496c42347fd04031ecf2feff60e592cee929c8b1bbe6d` |

三者均为 14×14 viewport、14dp 尺寸、单 path、`evenOdd`，无 group 或 clip；原件为黑色、`fillAlpha=1`。直接解析原 APK 资源表和编译 XML，确认每条 pathData 与上述反编译原件逐字符串一致。它们也与用户指定的 `8b2576d6b` 中三条电量图形逐字符串一致，仅历史 Avalonia 字符串额外带有 `F0 ` 填充前缀；Git 历史显示该组形状初次引入于 `708d770`，`8b2576d6b` 沿用了它们。当前实现依据欢律原件，不恢复旧 `OppoGlyphs` 来源类。

桌面仍使用现有 14×14 图标槽和图标至标签的 6px 间距，继承主题前景，不固定黑白配色或另加背景层。深色主题呈浅色圆与深色字母镂空，浅色主题随前景反转。电量文字、未知值、充电闪电、提示与无障碍名称保持原行为；版本仍为 0.9.2。实现见 [AppIcons.cs](../../src/HeyMelody.App/AppIcons.cs)。

## 上一轮 19×16 适配（历史）

以下记录保留上一轮 `1927934` 的来源和适配事实；带柄耳塞方案已由上述 14×14 方案替换。

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

### 当时的实际用途与桌面适配

同一反编译树的 `sources/G4/C1239a.java`（`BatteryViewAdapter.onBindViewHolder`）把角色 1、2、3 分别绑定到左耳、右耳、仓资源。`heymelody_app_battery_item.xml` 中的 `mIcon_which` 位于 `BatteryItemView` 旁，证明三者用于区分电量项的角色。

三个 vector 均为 19×16 viewport。左右各两个可见 path，仓一个；每个 path 显式使用 `evenOdd`，无 stroke、无 group transform。左右的 clip 矩形为 `(0,0)–(19,16)`；仓为 `(1.5,0)–(17.5,16)`，原文件有三层相同裁剪。

原 Android 文件使用主题颜色属性及 `fillAlpha=0.54`。这里仅将其记作历史来源事实；上一轮桌面适配保留原 pathData、每条路径的填充规则与裁剪，等比居中，沿用当时原生风格控件的前景和透明度，不套用源 alpha。

这些是 App 专用界面几何，不属于设备云美术索引。现有设备美术闭包仍为 365 条记录、634 个依赖文件加 1 个补充扫描 PNG，共 635 文件；图标不授予型号控制权限，也不恢复已退出支持来源的配置、缓存或美术。

## 保全、许可与验证边界

上一轮文档原稿、大小／SHA256、七份已跟踪静态契约基线，以及 19×16 资源溯源收据位于本机 `research/heymelody-battery-icons-20261019/20261018T162129Z-6ac5afbc/`。

本次 14×14 复原的四份文档原稿、三份 XML 副本、七份契约哈希基线、解析脚本、编译 XML 和来源收据位于 `research/battery-icons-restore-20261019/20261018T183045Z-b5d7dc54/`；`source-verification.json` 保存完整路径、字节、SHA256、资源表映射和两组逐字符串比较结果。这些档案沿用研究目录忽略规则，不随普通 clone 提供；主仓库原 APK 和反编译文件未改写，普通构建不依赖它们。

这些图形属于原权利人的厂商素材。来源可核实不表示获得新的许可，项目源码许可不重授素材权利；见[资源权利声明](../../packaging/licenses/Extracted-Earphone-Resources-NOTICE.txt)。

本专题核对了本地原件、资源哈希、资源表映射、历史几何和文档链接。本次 App runner 113 项、Windows 原生窗口 smoke 92 项通过，`nativeBluetoothCalls=0`、`applicationSetSubmissions=0`；目视检查仅覆盖深／浅主题、1280×900／900×640 四组电量区域裁剪。范围校验确认除三个专用图标块外 `AppIcons.cs` 与基线一致，`MainView`、主题和项目文件未改。日志与收据位于本机 `artifacts/battery-icons-restore-20261018T183155Z-fd5ec9c5/`，仅有既有 `DeviceGestureTests.cs:185` 的 CS0067 警告。不声称真实耳机、全部型号或 ARM64 硬件已验证。
