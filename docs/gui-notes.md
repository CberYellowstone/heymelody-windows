# HeyMelody Avalonia 界面（0.8.12）

0.8.12 将耳机状态提示移到顶部连接状态文本右侧，以“·”分隔，无提示时隐藏分隔符。长提示单行省略、悬停显示完整内容，多项在行内以“；”连接。操作失败、平台和 Demo 提示保留原有区域。通知分类与本轮验证见[通知提示修复](research/notification-header.md)；以下 0.8.11 发布证据保留历史版本。

0.8.12 本地 x64 构建通过全部 13 个默认检查程序及 59 项原生模拟窗口检查。ZIP 2841 个文件与发布目录的路径、大小和 SHA256 一致，本轮未操作真实耳机。

2026-10-10 补充：本轮游戏音效及 Hi-Res／空间修复见[研究说明](research/game-audio-spatial.md)，修复阶段的源码与 X4 验证版本为 0.8.10，随后随降噪界面调整升级至 0.8.11。本文修改前[原稿已归档](../research/game-audio-spatial-20261010/original-documents/docs/gui-notes.md)，发布更新前的稿件另存于[本地档案](../research/game-audio-spatial-20261010/pre-release-0.8.11-documents/docs/gui-notes.md)，下方历史发布验证记录保持原值。

0.6.0 完成 Windows Enco X4 功能的 Avalonia 初版迁移，0.6.1 改进后台刷新、交互及失败处理。当前界面使用 Avalonia 12.1.3、FluentAvaloniaUI 3.1.0 和 CommunityToolkit.Mvvm 8.4.2，功能随资源目录及实际设备能力提供。界面和客户端状态模型分别位于 `HeyMelody.App` 与 `HeyMelody.Client`；DTO、schema-3 双向命名管道契约及私有 worker 的 stdio 契约位于 `HeyMelody.Contracts`。`HeyMelody.Host` 独立托盘后台监管每台耳机的原生 worker，Windows 蓝牙调用不进入界面线程。

当前源码及发布脚本默认版本均为 0.8.12。主界面右侧降噪保持默认展开的 FluentAvalonia Expander，沿用降噪图标、说明和模式选择器；用户主动折叠后，普通状态刷新不会重新展开。真实设备和 Demo 沿用各自的会话展开状态。

0.8.11 本地 x64 自包含构建已通过全部 13 个默认检查程序，其中 App 93 项覆盖默认展开、标题点击折叠、刷新保留及真实设备／Demo 展开状态恢复；另行通过 59 项原生窗口模拟检查，蓝牙调用和设置提交均为 0。发布目录为 `release/HeyMelody_Windows_0.8.11`，ZIP 为 `release/HeyMelody_Windows_0.8.11_x64.zip`。本地[静态包报告](../artifacts/release-0.8.11-x64/static-validation.json)核对 x64 版本、依赖、资源与许可；[ZIP 校验](../artifacts/release-0.8.11-x64/zip-integrity.json)确认 2841 个文件的路径、大小和 SHA256 与发布目录一致。构建及原生检查日志同目录保留。本次包没有新增实机执行收据，前述 X4 验证仍属于修复阶段的源码控制链。

0.8.9 重新设计贴合度测试与黄金听感两个独立窗口，主窗口不变，见下方 [校准窗口](#校准窗口)。

0.8.8 重新整理右侧控制面板。设备名称、状态点、副标题以及连接／断开与刷新按钮组成固定页头，不随设置滚动。名称左侧的圆点用绿、黄、灰分别表示已验证控制、正在连接或写入（包括排队中的设置提交）、未连接；后台读取不改变圆点。连接时副标题只显示“已连接”，固件版本仍在设备信息中。离线展示缓存时，名称下方显示“上次连接 yyyy/M/d HH:mm”，取代原先的缓存提示信息条；时间取自 Host 缓存的最后一次核验采样（`PresentationSampleTime`）。其余副标题去掉“· 设置暂不可用”等重复后缀。页头下方是常驻 3px 的 ProgressBar 槽位，活动时淡入、空闲时淡出并停止动画，显示和消失都不改变下方布局。

所有卡片补齐图标与描述。图标按 Segoe Fluent Icons 码位解析：系统装有 Segoe Fluent Icons（Windows 11）时使用系统字体，否则使用 FluentAvalonia 自带的同码位符号字体；Segoe 没有对应字形的降噪、黄金听感、单只耳塞及左右耳／充电盒电量图标，改用 OPPO 无线耳机 17.6.3 资源中的矢量路径（`OppoGlyphs.cs`）。修正均衡器图标空白的问题。声音、耳机设置、操控三个动态分组补充描述；每个功能行直接作为原生设置条目显示，开关与标签居中对齐，开关右缘与其他编辑控件对齐。电量卡用图标区分左右耳和充电盒，充电时显示闪电图标，详细充电状态移入提示文字。黄金听感改为“听感配置”“建立听感”两条设置条目，贴合度测试使用与其他卡片相同的外观和高度，操作记录改为设置卡片并记住展开状态。降噪卡在尚无模式时显示说明文字，演示和非 Windows 预览提示均已精简。

0.8.7 起主界面底部设备卡片显示统一的通用矢量耳机图标、名称与状态，不使用 Enco X4 专属图片或图标资源请求。设备信息的复制按钮位于展开内容底部。设备连接状态刷新时保留选择及高亮，重启后恢复上次选择；未连接时保留上次读取的控件并禁用设备操作，见 [设备选择与离线控件](device-state.md)。左侧外观下方没有重复文字；降噪常显，其他设置默认折叠，用户主动展开的状态继续保留。

设备信息新增“复制”按钮，左侧为复制图标，右侧为文字。复制内容与该区显示一致，包含设备名称、产品编号和固件版本，未取得的值使用“—”；点击时读取当前设备值。贴合度测试与设备信息之间增加与其他设置一致的 8px 间距。三维模型默认 GPU 优先，已保存的软件选择及自动软件回退保留，见 [渲染设置](model-rendering.md)。主界面和管理器继续接收共享后台状态推送；本轮验证及历史范围见 [仓库说明](../REPOSITORY.md)。

## 界面与现有功能

参考 `research/main-page.png` 和 `research/headphone-configs.png`，默认窗口 1280×900，最小 900×640：左侧展示产品，右侧顶部固定显示身份、连接状态与操作，其下为电量和设置，底部固定设备选择区。宽度小于 1080 时改为上下布局，右侧页头仍固定，设置区域单独滚动。内嵌 X4 产品大图使用用户提供的 `research/x4-pods-with-earphones.png`；两张原始 1440×1440 透明 PNG 仍嵌入 App 的 `Assets/Devices/EncoX4`，运行时不依赖 `research` 目录，历史底栏素材不再用作设备卡片图标。

上述路径是图像采用时的原始出处。2026-10-08 归档后的访问入口为 [主界面参考](../research/screenshots/main-page.png)、[设置参考](../research/screenshots/headphone-configs.png)、[X4 产品大图](../research/screenshots/x4-pods-with-earphones.png) 与 [X4 底栏图标](../research/screenshots/x4-both-earphones.png)；文件仍仅在本地保存，组织规则见 [研究资料归档](research/README.md)。

早期按蓝牙名称识别 X4 的规则已在 0.8.1 移除。当前外观只依据实际产品 ID 与协议颜色：来源是 Host 按设备持久化的外观提示（`UiDevice.ProductId/Color`），以及所选设备会话中的实时值，不从蓝牙名称推断。美术目录中的已知值只增不减，资源版本号前进时在后台重新解析，内容相同时保留原渲染控件；没有资源服务的预览模式仍按产品 ID 显示内嵌的 X4 PNG。外观提示只影响显示，不授予控制能力。详见 [外观资源持久化](artwork-persistence.md)；应用设置已改为设置卡片，并提供“重新下载在线资源”。

`ProductArtwork` 通过 `Visual` 展示数据及 `VisualTemplate`（`IDataTemplate`）呈现产品大图，支持 PNG、动态 WebP、Lottie、三维模型和通用回退；宿主不读取蓝牙状态，也不硬编码 X4 判断。大图保留响应式尺寸限制和输入能力，图片模板等比居中，不在宿主统一套用 Viewbox。素材延迟加载，加载流及时关闭，应用结束时释放共享资源；各宿主独立创建呈现控件。底栏图标的专用宿主和加载逻辑已移除，视觉更新继续保留原有 `UiDevice` 列表及选择绑定，不重建列表或改变滚动位置。

当前源码已移除最底部状态/版本栏，以及设备栏的“我的设备”标题和刷新按钮。底栏采用 300×64 的横向设备卡片，通用耳机图标与名称/状态作为一组居中，选中项使用动态主题强调色边框，其他项使用主题灰色描边；长名称省略显示并提供完整名称提示。设备卡片与末尾的“+”放在同一个水平滚动区域，空间足够时整组居中；溢出时不添加首尾空白，首次打开、切换设备及调整窗口宽度后将选中卡片尽量移到视口中央，偏移受实际内容边界限制。首项选中时停在最左侧，末项选中时停在最右侧且“+”完整可见；靠近两端的选中设备完整显示，中间设备可居中。普通鼠标滚轮、触控板及滚动条均可横向滚动，用户滚动后保留当前位置。“+”通过 Avalonia Launcher 打开 Windows 蓝牙设置（`ms-settings:bluetooth`），失败提示与应用设置共用对话框协调器；非 Windows 预览禁用该入口。设备栏只显示当前有效的白名单设备；后台监听 Windows 配对和连接事件并推送目录变化，切换选择不影响其他耳机的监听。

独立 `HeyMelody.DeviceManager` 使用相同的 UI 依赖和主题偏好，默认 520×720、最小 400×480，允许双向拉伸，仅有关闭按钮。候选为已配对 Classic 音频设备，按 Authenticated 和缓存 CoD 筛选，不扫描、不连接；错误或缺失的 CoD 可能导致设备暂不显示。名单键保存到 `%LOCALAPPDATA%/HeyMelody/devices.json`，使用稳定地址的带用途前缀 SHA-256；界面仍只持有 Host 会话 Identity。保存会重新验证所选设备，原子替换文件；枚举失败不清空历史名单，损坏配置可以显式打开管理器重新保存修复。

有效名单为空才自动打开管理器；保存成功后重新检查名单再进入主界面，取消或关闭时无旧有效名单则退出主界面进程，已启动的后台托盘继续运行。应用设置提供“管理设备”：先释放设置对话框所有权，确认丢弃 EQ 草稿并取消尚未开始的界面设置，等待期间暂停主程序操作；取消恢复缓存选择，保存后保留仍有效的原选择，后台继续管理每台耳机的控制会话。管理器底部 72px 操作栏覆盖滚动列表，零勾选时保存和取消均禁用，但关闭始终可退出。Windows 11 build 22621 及以上用 DWM 的 Mica Alt，其余情况回退到主题实色；透明度与非活动背景由系统策略决定。

0.6.3 修正透明标题栏下原生与托管关闭按钮的叠加。透明标题栏与 `DwmExtendFrameIntoClientArea` 的全窗口扩展同时露出了 DWM 原生关闭图形和 Avalonia 12 的自绘关闭按钮；`FAAppWindow` 使用 Avalonia 的自绘标题栏，FluentAvalonia 模板本身没有另画一套关闭按钮。`MicaAltBackdrop` 通过 `Win32Properties.AddWindowStylesCallback` 移除 `WS_SYSMENU`，抑制原生标题栏按钮，保留 Avalonia 关闭按钮和 DWM 的 Mica Alt、圆角及阴影。回调在 XAML 的窗口属性赋值与样式重算之前注册，首次显示前已生效；释放窗口时移除回调。没有禁用 DWM 非客户区渲染。

使用 FluentAvalonia 的 `FAAppWindow`、`FASettingsExpander`、`FASettingsExpanderItem`、`FAInfoBar` 和 `FAContentDialog`，以及主题提供的按钮、下拉框、开关、文本框和滑块。系统、浅色、深色主题可在应用设置中切换，偏好保存到 `%LOCALAPPDATA%/HeyMelody/preferences.json`。无效偏好会回退到系统主题。项目图标沿用原创的 `Assets/HeyMelody.ico`。

保留配对设备枚举、手动连接和断开、左右耳及充电盒电量与充电状态、降噪、五种声音预设、十段自定义 EQ、双设备连接、游戏模式、空间音频及 Hi-Res。显示状态来自后台 GET、RFCOMM 通知和经关联的 BLE 观测；可写能力仍需 RFCOMM 型号和能力核验。无法读取的电量显示 `—`，未知开关显示“未确认”并禁用写入，部分读取失败有单独提示。

## 交互约束

- 选项使用原始协议编号，不以列表下标代替。降噪主按钮点击即提交父模式，子档选择独立提交子档；已回读确认的相同主模式不重复写入，父态回读不会自动选中首个子档。主切换未确认时，再次写入需明确重试确认；旧 Host 未提供主模式能力时禁用主按钮。声音预设与空间音频沿各自控件提交；开关仅由用户点击提交。
- 同一耳机的命令串行，不同耳机使用独立 worker；界面编辑独立进行。界面接收后台订阅的单调 revision 状态；初始化、重新连接、手动刷新及设置前后验证仍执行 GET，普通状态更新由蓝牙通知推动。`IsRefreshing` 只表示读取活动；`Busy` 表示连接等生命周期操作或待执行的用户提交，不作为统一禁用条件。提交期间禁用同组的提交按钮或开关，下拉选项、EQ 名称和滑块仍可编辑，其他组仍可提交。空间音频与 Hi-Res 共用一组，预设和自定义 EQ 共用一组；每组最多保留一个提交。应用设置在读取和写入期间仍可打开。
- 写入要求快照 ControlReady、型号和能力均已核验，后台实例、设备身份及连接代次与冻结目标一致。BLE 可补充新鲜电量，后台记录盒盖/佩戴状态并发布开盖事件；现有界面不单独展示盒盖/佩戴状态，设置权限仍由 RFCOMM 实际核验。读取失败的状态保持未知，相关写入禁用。状态推送和刷新不提交设置；不持续强制执行已结束的用户目标。下拉框按协议编号保留待选值，EQ 按条目与编辑版本保留本地调整；相同内容的列表不会反复替换。提交使用冻结的设置，回读不会覆盖提交后继续编辑的内容。
- “保存均衡器”执行保存并选用；“启用此 EQ”只选用已有且无未保存调整的条目。切换条目、切换或断开耳机、新建、刷新可能改变连接的设备列表及关闭窗口，会先确认丢弃草稿。取消这些草稿丢弃或关闭确认会保留草稿；普通状态刷新不会询问丢弃草稿。
- 游戏音效开关与类型每次操作最多提交一次，明确失败只提醒一次，未知响应只读核对，不弹出再次写入确认。失败、取消或无效提交后，复用控件恢复耳机观察值，其他草稿与较新的编辑仍保留。其他用户目标最多执行两次逻辑尝试：收到完整后台回执、连接及同一设备身份可核实的绝对值设置失败时，等待 500 ms 并重新读取；目标已达到则结束，仍未达到且安全才自动重试一次。需要再次创建 EQ、状态或能力不足以安全自动继续时，使用 `FAContentDialog` 询问“确认 / 取消”，默认取消。确认后再次读取和校验；确认本身不能绕过身份、连接或能力检查。后端每次尝试均重新核对能力和状态，不在传输层重放 SET。手机改变设置不会单独触发冲突确认；本次 Windows 提交仍使用冻结的明确目标。
- 取消再次写入确认时，会清空全部待选设置、EQ 草稿与尚未开始的写入，然后立即完整刷新。该行为不适用于普通草稿丢弃或关闭确认。第二次失败只弹出“确定”提醒；关闭提醒后立即完整刷新，不再重试。明确断连、控制连接状态未知或未取得完整后台结果时，第一次就提醒，关闭提醒后只读刷新，不自动重发。最终刷新失败只更新状态提示，不再次打开同一操作的对话框。设置、重试、失败与应用设置对话框共用协调器。
- 空间音频与 Hi-Res 各自提交目标，不弹出关闭另一项的互斥确认、不提交前置关闭操作；两项均显示耳机实际读回值。`AllowMutexChange` 与 `SpatialHiResMutex` 字段保留接口兼容。新建 EQ 失败后，先核对不在原列表中的唯一同内容条目：已选中则结束，未选中则复用该 ID；有歧义或重名时不再次创建。后续保存使用已核实的 ID，避免创建重复条目。
- ACK 接受与当前目标已回读到是两项独立事实。未知或拒绝的响应不会因匹配回读而改成成功响应；失败及未知响应提示不会被普通状态推送清除。同版本的待选值在目标已观察到后解除保护，后续手机端修改可正常显示；提交期间的新编辑仍保留。
- 应用设置不会与正在执行的控制确认重复弹出。关闭先停止接收新操作并取消尚未开始的写入，收起设置框和当前控制确认，再处理草稿确认；取消关闭会保留草稿并恢复窗口操作。确认关闭后解除事件订阅并断开界面管道连接，再结束主界面进程；已经开始的后台设置继续完成 ACK 和回读。
- 操作日志只在当前进程保留最近 80 项；遮蔽设备标识、MAC 与设备路径，不记录原始包或异常堆栈。

“连接或同一设备身份无法核实”包括：后台进程崩溃、超时或返回损坏/不匹配的 RPC 记录，导致客户端没有完整回执；重新读取时无法取得有效型号、能力或连接状态；返回的设备身份与当前选择/连接会话不一致；后台重启后丢失原地址到随机身份的映射。此时设置可能已经到达耳机，不能把旧请求转移到另一副同名耳机。程序提示并只读刷新，身份映射丢失时要求重新选择并连接。手机修改某个设置本身不属于身份无法核实，也不能仅凭一次 GET 超时断言蓝牙已断开。

## 校准窗口

`HeyMelody.FitTest` 与 `HeyMelody.HearingSetup` 共用 `HeyMelody.Calibration.UI` 的 `CalibrationWindow`／`CalibrationView`。0.8.9 起窗口外观与设备管理器一致：`FAAppWindow` 仅保留关闭按钮，默认 520×720、最小 400×520，可拉伸；构造时先注册 `MicaAltBackdrop`，Windows 11 build 22621 及以上使用 DWM Mica Alt，其余情况回退为主题实色。该类源码仍属设备管理器，命名空间改为 `HeyMelodyWindows.Windowing` 后由两个项目共同编译，不新增程序集。页面由固定页头、滚动内容和覆盖在底部的 72px 操作栏组成，操作栏使用 `LayerOnMicaBaseAltFillColorDefaultBrush`，内容底部留出 84px 以便最后一项可滚到操作栏上方。按钮按 Windows 向导惯例右对齐为“上一步 · 下一步（强调色）· 取消”；完成后取消变为强调色“关闭”，停止失败时为“重试停止”。左侧状态文字显示“第 x / 12 步”或“正在停止测试…”。

页头含标题、步骤指示和固定 3px 活动进度条（淡入淡出，不移动内容）。支持耳道扫描的黄金听感显示“耳道扫描 → 听音测试”两步：当前步为强调色圆形，完成后显示对勾，未开始为灰色圆环；图标为 OPPO `melody_ui_gold_hearing_detect_ear_tag` 的耳朵轮廓和五段波形。阶段由 `CalibrationViewModel.Phase` 根据 Host 阶段的先后推导：`Starting`／`CheckingWear` 从就绪或失败进入时属于扫描（无扫描型号属于测听），从 `ScanCompleted` 进入时属于测听，失败、取消过程保留原阶段；它只决定显示，不改变命令。

主图沿用 ColorOS 向导的同心波纹，改为随主题变化的强调色圆环，中心为耳朵或左右耳塞矢量；扫描、贴合度检测进行中以及测试音已确认时显示扩散波纹，测右耳时耳朵镜像。测听页依次为主说明“左右拖动音量条，找到“滴滴”声刚好消失的临界点”、副说明“当前测试左耳 · 1000 Hz”（未验证型号显示“频段 N”），以及左右耳各 6 段、共 12 个 `ProgressBar` 组成的进度：已完成为 100、当前为 50、未开始为 0，模板中的轨道与指示条统一为 4px 圆角，下方标注左耳／右耳并突出当前组。音量条为 0–11 档原生 `Slider`，在下方显示刻度，两端为音量图标；拖动、键盘与释放沿用原有防抖和 ACK 确认，下方显示当前档位与“重试播放”。V1 型号改为两个等宽的“听到了／没听到”。暂停时保留进度，原因显示在信息栏。

就绪页显示所选资源来源的官方音量指引 Lottie（V2）或主图，以及“佩戴好两只耳机”“保持环境安静”两张说明卡。结果页先等待两类滤波完整，再显示“试听对比”卡（原音／黄金听感两个切换按钮跟随 Host 的试听模式，点击当前模式不重复发送，另有“结束试听”）和“配置名称”卡；“保存并启用”位于操作栏，替换确认对话框不变。左右耳差异提示只在信息栏显示一次，Host 的暂停、拒绝和噪声消息同样进入信息栏，不再覆盖或追加到正文。贴合度结果为左右两张卡片，以绿色、橙色、红色圆形耳塞图标表示“贴合良好／一般／较差”（协议 1／0／6），下方信息栏给出调整建议。设计参考为用户提供的 ColorOS 截图，原件与取舍记录在本地 [research/calibration-wizard-design-20261009](../research/calibration-wizard-design-20261009/README.md)，不随 Git 分发。

## 构建与界面验证

```powershell
dotnet build src/HeyMelody.Host -c Release
dotnet run --project src/HeyMelody.App -c Release
dotnet run --project tests/HeyMelody.Client.Tests -c Release
dotnet run --project tests/HeyMelody.Host.Tests -c Release
dotnet run --project tests/HeyMelody.App.Tests -c Release
dotnet run --project tests/HeyMelody.App.Tests -c Release -- --native-smoke
dotnet run --project tests/HeyMelody.App.Tests -c Release -- --native-backend-smoke
dotnet run --project tests/HeyMelody.App.Tests -c Release -- --native-startup-smoke
dotnet run --project tests/HeyMelody.DeviceManager.Tests -c Release
dotnet run --project tests/HeyMelody.DeviceManager.Tests -c Release -- --native-smoke
dotnet run --project tests/HeyMelody.DeviceManager.Tests -c Release -- --native-alt-f4-smoke
dotnet run --project tests/HeyMelody.Calibration.UI.Tests -c Release
dotnet run --project tests/HeyMelody.Calibration.UI.Tests -c Release -- --native
```

0.8.9 在 Linux x64 上以 Release 配置运行默认测试中可在该平台执行的 11 个 runner，共 545 项通过：Protocol 64、Resources 25、Calibration 48、Calibration.UI 11、Devices 19、Control 19、Daily 110、Probe 23、Client 137、App 81、DeviceManager 8。Calibration.UI 新增阶段推导检查，并在无头窗口中核对步骤指示、底部 72px 操作栏及按钮顺序、左右两组进度条数值（已完成 100、当前 50、未开始 0）、测右耳时主图镜像、差异提示只出现一次和对话框窗口外观，输出浅色与深色截图。FitTest、HearingSetup、DeviceManager 另以 `win-x64` 构建，Host 启用 Windows 目标构建，均无警告。未运行 Host.Tests、Windows.OfflineChecks、校准与设备管理器的 `--native` 原生窗口检查、真实 Mica Alt 渲染、打包及两个静态校验器；Linux 截图缺少中文字体，只用于核对布局。未使用蓝牙，也未进行声学测试。

0.8.8 在 Linux x64 上以 Release 配置运行默认测试中可在该平台执行的 11 个 runner，共 483 项通过：Protocol 47、Resources 25、Calibration 18、Calibration.UI 5、Devices 19、Control 19、Daily 101、Probe 23、Client 137、App 81、DeviceManager 8。App.Tests 在非 Windows 上为 81 项（Windows 预期 89 项），新增固定页头、三态状态点、上次连接副标题、卡片图标与描述，以及阻塞读取和排队写入期间进度槽位与内容坐标完全不变的检查。App 另行交叉编译 `win-x64`，Host.Tests 在启用 Windows 目标后编译通过。未运行面向 Windows 的 Host.Tests、Windows.OfflineChecks、各 `--native-*` 原生窗口检查、打包及两个静态校验器；Linux 截图缺少中文字体，只用于核对布局与图标。

0.8.7 的 Release App.Tests 通过 [88 项](../artifacts/avalonia-ui-20261009-064833-8db56972/validation.json)，新增实际 ListBox 绑定的连接刷新、多设备排序、空选择恢复及后台重启检查，以及四张明暗主题／宽窄离线缓存截图。复制按钮默认折叠时不可点击，展开后位于字段下方并通过实际鼠标命中验证；通用图标与文字整组居中。另行运行的原生主窗口检查通过 [59 项](../artifacts/release-087-20261009/native-main.log)，原生 Demo 通过 [40 项](../artifacts/native-demo-087-20261009/native-demo-receipts.json)，Automatic 在本机实际使用 OpenGL GPU，保留 20 个 mesh、94,927 个三角形，展开后全部 34 个控件可用。以上为源码／模拟服务验证；剪贴板使用替身，未验证真实耳机、完整发布客户端启动、系统剪贴板往返或原生 ARM64 硬件。

0.8.6 的 Release App.Tests 已通过 84 项，报告为 [validation.json](../artifacts/avalonia-ui-20261008-235907-07a68893/validation.json)。当时新增真实／演示设备的浅色、深色、宽窄默认折叠及设备信息区域截图，核对降噪常显、文字设备卡片、复制按钮与 8px 实际间距；复制测试覆盖当前字段、失败重试、并发和切换／关闭后的迟到反馈。交互检查等待展开动画完成并确认实际鼠标命中目标控件，剪贴板写入使用替身，未验证系统剪贴板往返。另行运行的原生主窗口检查通过 59 项，使用内存服务，不提交耳机设置。

0.8.6 的原生演示检查通过 [40 项](../artifacts/native-demo-086-agent-20261009a/native-demo-receipts.json)，隔离未写入的临时偏好以验证新默认 `Automatic`，本机实际为 OpenGL GPU。丹拿版保留 20 个 mesh、94,927 个三角形并生成四张原生模型帧；动态分组默认折叠，主动展开并完成布局后全部 34 个控件可用。它与 headless 布局报告都只验证源码测试窗口，不是完整发布客户端或 ARM64 硬件执行记录。

以下 App.Tests 47／44 项计数描述 0.7.0 的历史基线；当前 runner 已扩展用例并自行报告总数。App.Tests 使用真实 Skia 绘制 Avalonia 控件，覆盖连接、无设备、未知型号、部分读取、断开五种状态的浅色/深色和 1280×900/900×640，在 Windows 上共 47 项检查，包括 20 张截图、原有交互与设备栏检查、3 组卡片/整栏居中、自然滚动边界及手动滚动检查、5 组设备外观和展示模板检查、7 项启动与管理进程生命周期检查及设置到管理器的对话框释放检查。系统入口测试注入模拟 Launcher，不打开 Windows 设置；非 Windows 预期总计 44 项。默认输出在 `artifacts/avalonia-ui-*`，包含 PNG 哈希与 `validation.json`，也可通过 `--output-dir` 指定新输出目录。测试仅使用模拟服务，不调用原生蓝牙。Headless 调度显式使用 `Dispatch<bool>`，避免无返回值 async 委托变为 `async void` 而漏报失败；在外部调用线程等待及释放会话。

`--native-smoke` 单独在 Windows 创建屏幕外真实 Win32 `FAAppWindow`。0.7.0 的历史运行共 54 项，验证标题栏、产品图片和当时的底栏图标；0.8.6 改为检查文字设备卡片与新面板布局。历史运行还覆盖两个主题和宽窄布局、设置及重试/失败对话框和服务只释放一次。使用 8 台模拟设备验证底栏溢出、不额外留白及启动/调整尺寸后的选中卡片按自然边界定位。该测试同样使用内存服务，不启动蓝牙后台。交互测试另外覆盖读取阻塞期间的真实鼠标提交、继续编辑、对话框串行和关闭取消。

X4 素材与展示 View 在发布打包前已完成 Release 源码构建。修正型号身份匹配后的最终 App 检查 45 项通过，报告为 `artifacts/x4-visuals-b3241436/headless-final/validation.json`，执行日志为 `artifacts/x4-visuals-b3241436/headless-final.log`；主程序原生窗口 54 项检查通过，日志为 `artifacts/x4-visuals-b3241436/native-smoke.log`；Client 的 74 项检查通过，日志为 `artifacts/x4-visuals-b3241436/client-tests.log`。三个测试回执的原生蓝牙调用均为 0，原生窗口测试的设置提交为 0，系统设置没有打开。已检查连接状态下浅色 1280×900、深色 1280×900 和深色 900×640 截图中的 PNG、图标及响应式布局，最终截图位于 `headless-final`。汇总报告 `artifacts/x4-visuals-b3241436/validation-summary.json` 记录最终测试、截图及源文件与嵌入素材的哈希。这组普通源码检查使用默认 `0.6.0` 版本，不是各架构发布包的专属执行回执；当时没有运行完整发布脚本或生成新 ZIP，随后 0.6.5 发布构建另行记录。

显式运行 `dotnet run --project tests/HeyMelody.App.Tests -c Release -- --native-settings-smoke` 会滚动到末尾并通过真实“+”按钮打开 Windows 蓝牙设置，当前预期共执行 60 项检查；默认测试和 `--native-smoke` 不打开系统设置。0.6.4 设备栏改进及本轮素材替换均未执行此模式。该模式仅记录 Windows 是否接受 URI 请求，不断言系统页面已显示，不关闭系统设置窗口，也不调用真实蓝牙。

历史 0.6.5 的 `Build-Client.ps1` 执行 Protocol 37 项、Devices 13 项、Control 19 项、Daily 52 项、Probe 23 项、Host 14 项、Client 74 项、App 45 项、DeviceManager 6 项，共 283 项检查；0.6.5 构建已执行并通过完整默认测试，日志为 `artifacts/release-065/build.log`。0.6.4 发布时 App 为 40 项，总计 278 项检查通过；0.6.2 和 0.6.3 发布时 App 为 37 项，总计 275 项检查通过。0.6.2 的主程序 `--native-smoke` 另外执行 38 项，`--native-startup-smoke` 使用真实 Win32 窗口验证 5 组启动、异常恢复和关闭场景；当时管理器 `--native-smoke` 执行 33 项 DWM、窗口样式、浅深色和尺寸检查，并输出 compositor 截图到 `artifacts/device-manager-native`。这些原生模式不由发布脚本默认执行，也不访问真实蓝牙或写用户白名单。Client 新增管理生命周期和 RPC 检查，Host 新增隔离目录的稳定身份、配对筛选、原子保存及失效/损坏名单检查；管理器检查多选、刷新、保存失败、关闭取消、操作栏及系统设置入口。

0.6.3 的管理器原生修复验证已通过：`--native-smoke` 和 `--native-alt-f4-smoke` 各执行 75 项检查，报告分别为 `artifacts/device-manager-native-063/report.json` 和 `artifacts/device-manager-native-063-alt-f4/report.json`。浅深色及两种窗口尺寸下均保留 DWM 非客户区渲染和 Mica Alt，只有一个托管关闭按钮及关闭命中区域；compositor 截图确认单一关闭图形、圆角和阴影。标题拖动及右下角拉伸命中保持有效，实际点击关闭按钮和向测试窗口发送 Alt+F4 均返回取消退出码 1。候选设备使用内存模拟服务，原生蓝牙调用和用户白名单写入均为 0；这些是源码原生窗口验证，不代表已验证各架构发布 EXE 或真实耳机。

已发布 0.6.1 当时的 App 检查为 24 项、总计 228 项，旧报告仍保留。旧 App.Tests 报告曾将 `version` 硬编码为 `0.6.0`；当前从 App 程序集取得版本，并显式标为 source/mock 检查，仍不构成发布包的专属执行回执。这些源码测试没有真实蓝牙调用，不能替代耳机和手机同时操作的实机验证。

离屏预览使用相同界面和状态模型，输出文件必须不存在：

```powershell
dotnet src/HeyMelody.App/bin/Release/net10.0/HeyMelody.dll --ui-preview artifacts/preview-light.png
dotnet src/HeyMelody.App/bin/Release/net10.0/HeyMelody.dll --ui-preview artifacts/preview-dark.png --theme dark --size 900x640
dotnet src/HeyMelody.App/bin/Release/net10.0/HeyMelody.dll --ui-preview artifacts/preview-eq.png --size 1280x1800 --expand-settings
```

`--scenario` 可选 `connected`、`empty`、`unsupported`、`partial`、`disconnected`。模拟服务与预览状态模型分别拒绝设置写入。预览不包含系统原生标题栏。0.8.8 起 `--expand-settings` 在窗口显示、模板实例化后再展开并等待展开动画；此前在显示前调用，实际不会展开任何分组。

`--ui-live-preview <新PNG路径>` 通过只读包装访问原 Windows 后台，仅允许唯一已连接且名称精确为 OPPO Enco X4 的设备。没有可信快照即退出 1，不生成连接截图；结束时释放自己的客户端订阅与管道，共享 Host 继续运行。只读包装允许枚举、选择和读取，不恢复已被用户暂停的控制会话，不允许设置写入，也不启动轮询；共享后台仍可能对其他已连接白名单耳机执行正常监听。

三 RID 的自包含发布使用 `Build-Client.ps1`。0.6.5 使用 `-Version 0.6.5` 完成全部 283 项默认测试及 x64/x86/ARM64 构建，ZIP 为 `release/HeyMelody_Windows_0.6.5_AllArchitectures.zip`。该轮 App 的 45 项检查报告为 `artifacts/avalonia-ui-20261006-201921-50225a6d/validation.json`，另行重跑主程序原生窗口的 54 项检查通过，日志为 `artifacts/release-065/native-smoke.log`，原生蓝牙调用和设置提交均为 0。静态报告 `release/HeyMelody_Windows_0.6.5_STATIC-VALIDATION.json` 核对三架构 9 个入口程序集的 `0.6.5.0` 版本、哈希、依赖、.NET 10.0.12 自包含运行时、许可，以及 ZIP 中的 1513 个文件路径/大小和 316 个关键内容哈希，全部通过。`artifacts/release-065/embedded-artwork.json` 的 6 项检查确认两张原始 PNG 在三架构的 GUI 程序集中完整嵌入，内容与提供的文件逐字节匹配。构建及静态日志位于 `artifacts/release-065`；这些源码测试与静态校验不代表三架构发布 EXE 的真实耳机执行或原生 ARM64 硬件验证。

0.6.4 历史发布使用 `-Version 0.6.4` 完成全部 278 项默认测试及 x64/x86/ARM64 构建，ZIP 为 `release/HeyMelody_Windows_0.6.4_AllArchitectures.zip`。该发布前主程序 `--native-smoke` 的 50 项检查另行通过，验证首设备在真实 Win32 窗口中停在左边界且无额外留白；首、中、尾及邻近边界设备、末尾“+”完整可见和溢出状态变化由 App 的 40 项检查覆盖，离屏报告为 `artifacts/avalonia-ui-20261006-192934-9d5eae32/validation.json`。该轮构建和原生日志位于 `artifacts/release-064`。静态报告 `release/HeyMelody_Windows_0.6.4_STATIC-VALIDATION.json` 核对三架构 9 个入口程序集的 `0.6.4.0` 版本、哈希、依赖、运行时、许可，以及 ZIP 中的 1513 个文件路径/大小和 316 个关键内容哈希，全部通过。ZIP SHA-256 为 `1768863d0b81120507d142c97e3067fcc8e7b12d0733497be2a0cb4974846f05`。已有 ZIP 不含本轮 X4 PNG 和展示 View 改进，其静态报告也不验证这组新源码。

0.6.3 历史发布通过 `-Version 0.6.3` 完成全部 275 项测试及 x64/x86/ARM64 构建，ZIP 为 `release/HeyMelody_Windows_0.6.3_AllArchitectures.zip`。静态验证报告 `release/HeyMelody_Windows_0.6.3_STATIC-VALIDATION.json` 核对三架构 9 个入口程序集的 `0.6.3.0` 版本、可执行文件与托管 DLL 哈希、原生依赖、共享运行时和许可，以及 ZIP 中的 1513 个文件路径/大小及 143 个关键内容哈希，全部通过。

上述 0.6.3 发布包生成后，源码继续改进主界面设备栏的居中及共同滚动布局。当时完成 Release 构建，App 的 40 项检查及主程序原生窗口的 50 项检查通过；离屏报告位于 `artifacts/avalonia-ui-20261006-190031-92941599/validation.json`。该轮没有重新打包，现有 0.6.3 ZIP 不包含这组设备栏改进；0.6.4 已将其与自然滚动边界调整一同发布。该历史报告验证的是首尾额外留白方案，不能作为 0.6.4 自然边界或后续 X4 素材行为的回执。

0.6.2 历史发布通过 `-Version 0.6.2` 完成完整测试和 x64/x86/ARM64 发布，ZIP 位于 `release/HeyMelody_Windows_0.6.2_AllArchitectures.zip`。静态包报告 `release/HeyMelody_Windows_0.6.2_STATIC-VALIDATION.json` 核对各架构 GUI、管理器、Host 的版本、可执行文件与托管 DLL 哈希、原生依赖、共享运行时、许可，以及 ZIP 中的 1513 个文件路径/大小和 143 个关键内容哈希。发布版 x64 管理器的模拟启动/关闭回执位于 `artifacts/published-manager-0.6.2-x64-mock-smoke.json`，使用 `--ui-preview`，关闭返回 1，复验中用户配置未改变。

发布包的 `manifest.json` 和可选执行回执绑定 GUI、管理器和 Host 的 EXE 哈希；托管 DLL 哈希另记在静态包报告中。报告明确未验证真实蓝牙及 ARM64 硬件执行。旧报告仍保留，但历史 0.5.0 硬件证据不能证明本次迁移。模拟截图、原生窗口测试、构建和静态检查不代表真实耳机写入；只读 live preview 也不能证明本次提交、重试及手机并发行为。
