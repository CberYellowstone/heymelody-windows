# 界面文本人工审计

生成四表 Excel：文本审阅、功能追踪、场景证据、覆盖清单。应用文案和产品运行行为不改变。只读扫描 `src/` 的 C#、AXAML 和内置欢律配置，不连接设备、不读取密钥或个人设备快照。

## 人工使用

打开输出目录的 `界面文本审计.xlsx`，从“文本审阅”按批次筛选。D 列保留原文或动态模板，F/G 列跳到场景和追踪表。H/I 分别填写功能链与场景核实状态，J 填写措辞结论，K/L 填写建议和备注。措辞通过但证据未核实的条目继续显示“待核实”。不适用的证据可明确选择“不适用”。

“功能追踪”的来源符号、参数位置、绑定、条件和直接调用来自语法分析；功能分组路径是待逐条确认的导航，不能视为每条文本已经完成运行时调用链验证。C# 同名接口分派、异常透传、动态型号条件须对照源码核实。场景截图是页面级离线模拟参考，不证明所有条目都已实际出现。无障碍、悬停、托盘和瞬态错误保留单独缺口。

截图通过相对链接打开，移动交付目录时保留 `screenshots/`。`screenshots/index.html` 可直接浏览全部截图。源码链接相对仓库定位；单独复制输出目录后，用追踪表内的文件、符号和行号定位。当前基线不覆盖未取得的在线/缓存增量。

完整字符串、上下文、输入 SHA256 和文件覆盖留在 `audit-data.json`；覆盖表另保留被排除候选及理由。XML 不允许的控制字符在 Excel 中显示成 `\u0000` 形式，JSON 中保留原值；候选中的字符串不作为公式执行。`src/` 扫描之外也记录 `packaging/` 启动脚本覆盖：当前只启动程序或向控制台报错，没有产品 GUI 文案。

## 重新生成

需要 .NET 10 SDK、Node、Python 3（仅标准库）及宿主提供的 `@oai/artifact-tool` 运行时。工具不安装依赖，不使用仓库 node_modules。Roslyn 来自 .NET SDK。

在 PowerShell 中运行以下命令，填写本机运行时路径及本轮新截图目录：

```powershell
pwsh -NoProfile -File tools/research/ui-text-audit/Invoke-UiTextAudit.ps1 `
  -RuntimeNodeModules '<runtime>/dependencies/node/node_modules' `
  -Python '<runtime>/dependencies/python/python.exe' `
  -AppScreenshots '<本轮主界面截图目录>' `
  -CalibrationScreenshots '<本轮校准截图目录>' `
  -ManagerScreenshots '<本轮设备管理截图目录>'
```

默认输出到新的 `outputs/ui-text-audit-<时间>-<随机ID>/`；输入基线、日志和工具副本归档到新的 `research/ui-text-audit/`。拒绝已有输出目录。截图参数可省略，缺图场景明确显示无截图。不能将旧截图作为新版本的已验证结果；使用它们时须另记来源版本。可用 `-VerificationReceipt <本轮测试收据.json>` 提供含 `summary` 的执行记录；未提供时工作簿明确说明没有测试执行收据，不沿用本轮的通过结论。

宿主代理使用 spreadsheets 技能时，在本轮第一次创建工作簿前按技能执行一次 marker；命令行工具本身不要求安装 Codex 技能。

现有离线截图入口（执行前选用新的输出目录；不会连接真实耳机）：

```powershell
dotnet run --project tests/HeyMelody.App.Tests -c Release -- --output-dir artifacts/<新目录>/app
dotnet run --project tests/HeyMelody.Calibration.UI.Tests -c Release
dotnet run --project tests/HeyMelody.DeviceManager.Tests -c Release
```

校准入口输出新的带时间目录；设备管理现有 runner 固定写 `artifacts/device-manager-ui`，使用前应保全旧图。界面测试通过与截图输出位置须记录到本轮证据，不能仅凭工具里的说明认定测试已运行。

## 保留人工意见

如果只填写了 K 列建议并希望应用到源码，可用 `import_suggestions.py <已保存工作簿副本.xlsx> <同批audit-data.json> <新输出.json>` 只读提取。它按文本 ID 核对原文，仅提取非空且不同于原文的建议；配置设备名称及 Bundles 资源单列为忽略项。结果需结合符号、条件和上下文逐条应用，不自动替换同名文案。工作簿正在 Excel 中打开时，应使用共享读取保全的快照，不修改或关闭原工作簿。

重新生成时同时传 `-PreviousWorkbook <已审阅.xlsx>` 和 `-PreviousData <同批audit-data.json>`。工具按 ID 读取人工列，不修改原文件；排序后仍可匹配。拒绝重复/缺失 ID、原文被改写或非法状态值。新增、原文变更和已删除记录分别标识，旧条目及意见保留。

稳定 ID 来自来源、符号签名、显示角色、去掉文案后的语法结构与局部序号，不依赖文字或行号。插入相同结构条目可能影响局部编号；必须人工核对新增/删除。AXAML 路径和配置数组位置改变也可能造成新增/删除。原文变更时保留建议及备注，功能/场景核实和措辞结论重新转为待处理，避免沿用旧结论。

## 验证

```powershell
dotnet run --project tools/research/ui-text-audit/Extractor -c Release -- --self-test
node tools/research/ui-text-audit/prepare.mjs --self-test
<python.exe> tools/research/ui-text-audit/workbook_io.py self-test
```

生成过程检查计数、ID 和场景关联；工作簿检查证据未齐仍待办、齐全后完成、公式错误和四表渲染；导出后用 ZIP/XML 验证原文、冻结窗格、筛选、验证规则、超链接和意见回读。原生超链接通过标准库补入 artifact-tool 导出的文件，避免 HYPERLINK 公式引擎差异。成功导出不代表原生 Excel 或真实硬件验证；`validation.json` 标明边界。
