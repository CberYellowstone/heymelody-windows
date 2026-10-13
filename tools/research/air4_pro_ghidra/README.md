# Air4 Pro 固件 Ghidra 复现工具

本工具只导入本机已经保全、解包的固件和导出静态分析，不下载、解包、刷写或连接设备。完整原件、下载响应与块映射留在 `research/air4-pro-control-2026-10-13/`；不要把原始固件、APK 或专有反编译产物作为普通构建依赖。

本轮人工结论见 [完整研究报告](../../../research/air4-pro-control-2026-10-13/reports/firmware-control-analysis.md)。读取大型索引和 C/assembly 前遵守根 [AGENTS.md](../../../AGENTS.md) 的尺寸统计、结构预览和限量分段规则。

## 输入与经过验证的工具

- 主 raw：`research/air4-pro-control-2026-10-13/firmware/unpacked-v125/air4-pro-v125.raw.bin`，1,914,748 字节，SHA256 `168c1a32fb4dc46e2b274accb4a2c539cab8ae2ffd388aaa6b9b00bacb583fbd`。
- Ghidra 12.1.4 PUBLIC：本轮 `tooling/ghidra_12.1.4_PUBLIC/`，原官方 ZIP 和 SHA256 校验依据保存在同目录工具清单。
- 成功运行的 Linux JDK：`tooling/jdk-21.0.12.1+1/`。系统 Java 25 的失败日志另保留，不能仅凭版本号声称原环境运行成功。
- 装载地址依据：raw 0x10 是 Thumb LDR/BX，raw 0x54 为 `0x0C010015`；执行别名 `0x0C010000`，数据别名 `0x2C010000`。`Air4Import.java` 按观察值检查输入，建立字节映射数据别名，设置 Thumb context 和已知锚点。
- 首次语言 `ARM:LE:32:Cortex` 没有解码启动 MSPLIM，保留为独立首轮。主结果使用 `ARM:LE:32:v8-m`，可解码该指令；这不是已确认具体 Cortex 核或完整硬件存储布局。

## 主分析命令

以下命令从仓库根运行，假定本轮工具已经保全解压。`AIR4_RESEARCH` 为本任务变量；三个 XDG 目录隔离 Ghidra 设置和缓存，避免默认用户目录写入。不会更改 HOME 或 CODEX_HOME。

```bash
AIR4_RESEARCH="$PWD/research/air4-pro-control-2026-10-13"
mkdir -p "$AIR4_RESEARCH/decompiler/project" \
  "$AIR4_RESEARCH/decompiler/export-v125-v8m" \
  "$AIR4_RESEARCH/decompiler/logs" \
  "$AIR4_RESEARCH/decompiler/user-config" \
  "$AIR4_RESEARCH/decompiler/user-cache" \
  "$AIR4_RESEARCH/decompiler/user-data"

JAVA_HOME="$AIR4_RESEARCH/tooling/jdk-21.0.12.1+1" \
XDG_CONFIG_HOME="$AIR4_RESEARCH/decompiler/user-config" \
XDG_CACHE_HOME="$AIR4_RESEARCH/decompiler/user-cache" \
XDG_DATA_HOME="$AIR4_RESEARCH/decompiler/user-data" \
GHIDRA_HEADLESS_MAXMEM=4G \
"$AIR4_RESEARCH/tooling/ghidra_12.1.4_PUBLIC/support/analyzeHeadless" \
  "$AIR4_RESEARCH/decompiler/project" Air4ProV125V8M \
  -import "$AIR4_RESEARCH/firmware/unpacked-v125/air4-pro-v125.raw.bin" \
  -processor ARM:LE:32:v8-m -cspec default \
  -loader BinaryLoader -loader-baseAddr 0x0C010000 \
  -scriptPath "$PWD/tools/research/air4_pro_ghidra" \
  -preScript Air4Import.java 0x0c010000 0x2c010000 \
  -postScript Air4Export.java "$AIR4_RESEARCH/decompiler/export-v125-v8m" 15 \
  -analysisTimeoutPerFile 900 -max-cpu 4 \
  -log "$AIR4_RESEARCH/decompiler/logs/ghidra-v8m.log" \
  -scriptlog "$AIR4_RESEARCH/decompiler/logs/scripts-v8m.log" \
  > "$AIR4_RESEARCH/decompiler/logs/headless-v8m-console.log" 2>&1
```

重新复现应使用新的项目名、输出目录和日志名，避免覆盖已有证据。`Air4Export.java` 只接受新目录或预先创建的空目录，非空目录、文件或符号链接会在启动反编译器前被拒绝；所有导出文件均独占创建，已有同名文件不会被覆盖。首轮 Cortex 命令只把语言改为 `ARM:LE:32:Cortex`，项目名为 `Air4ProV125`，输出为 `export-v125`；其原项目、日志和输出均保留。

## 导出和目标索引

`Air4Export.java` 尝试所有 Ghidra 已发现的可执行函数，每函数默认 45 秒，本轮实际参数 15 秒。输出包括全部 C 成功产物、每个函数的 assembly、函数调用/被调关系、引用、已定义字符串、已定义程序列表，以及失败和耗时汇总。原始程序映像基址属性仍是 BinaryLoader 默认零；真正代码块装载在执行别名，索引 `raw_offset` 按该别名计算。

```bash
python3 tools/research/air4_pro_ghidra/analyze_exports.py \
  "$AIR4_RESEARCH/firmware/unpacked-v125/air4-pro-v125.raw.bin" \
  "$AIR4_RESEARCH/decompiler/export-v125-v8m" \
  "$AIR4_RESEARCH/analysis/ghidra-v8m-pass-1"

python3 tools/research/air4_pro_ghidra/verify_0114_wire.py \
  "$AIR4_RESEARCH/firmware/unpacked-v125/air4-pro-v125.raw.bin" \
  "$AIR4_RESEARCH/analysis/0114-static-wire"

python3 tools/research/air4_pro_ghidra/reconstruct_0105_wire.py \
  "$AIR4_RESEARCH/firmware/unpacked-v125/air4-pro-v125.raw.bin" \
  "$AIR4_RESEARCH/analysis/0105-static-wire"
```

三个 Python 工具在读取输入前独占创建输出目录，拒绝任何已存在的目录、文件或符号链接，产物也使用独占写入。不要预先创建示例中的 `analysis/` 子目录；运行失败后保留空目录或部分产物，排查后使用新的输出路径重试。工具不会清空失败目录。

`analyze_exports.py` 通过 ASCII 字符串、四字节对齐指针和最多三层引用生成模块和控制候选，字符串噪声、指针低位和候选调用链都需要人工核实。程序会在本地处理大索引，终端只返回简短统计；不能把本地解析视为全文安全可读。

`verify_0114_wire.py` 只接受指定 raw SHA256，验证 17 条原 Thumb 指令字节，并独立解码 MOVW 命令和三处 BL 目标。它重构已初始化的三个 codec 分支：`0→1`、`2→2`、`255→8`，得到 0x8114 声明 payload2、实际 payload3 的 12 字节外帧。序号 0x5A 是示例标记；输出 `.bin` 明确是静态重构，不能写成真机抓包或设备设置验证。

`reconstruct_0105_wire.py` 校验 v125 原始版本和格式字符串，重构实际九个 triplet 的字段顺序、状态字节与计数。输出分别选择本机左/右125、未知peer000、示例box000；这两个动态状态是供离线边界测试的选择，不能写成实际设备状态。peer000只在peer全局值为零且TWS helper未触发本机版本回填时保留；TWS断开不会在该handler中主动清除已知peer版本。

离线保全验收不依赖研究档案、APK 或固件，只使用临时目录和模拟输入；Java 保护代码的验收需要 PATH 中有 JDK 的 `javac` 和 `java`。它不启动 Ghidra、应用界面或设备连接，也不生成截图：

```bash
python3 -m unittest discover -s tools/research/tests -p test_air4_pro_preservation.py -v
```

## 自动结果与人工理解的边界

本轮 v8-m 主导出为 7,139 个已发现函数、7,138 份成功 C、1 个失败，以及 7,139 份 assembly；成功状态不表示没有警告，也不表示所有可执行代码都已发现。原映像中代码、常量池和数据边界、间接跳转、ROM 调用、具体核心、RAM 布局与多核关系仍有未闭合处。不要运行这些 C 文件，也不要把它们当成可重新编译的原始源码。

人工深追集中于 SPP 模式选择、L1 收发、GET 0x0100/0x0105/0x0114、版本来源、正常 codec 分支、部分异步通知和两版官方 App 的处理差异。详细报告逐项记录证实、推断、失败与未验证范围。完整自动产物、保全清单与初始检查点均留在新研究档案，旧 APK 和研究历史不改写。
