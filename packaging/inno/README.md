# Inno Setup 来源与保全

本目录保留安装脚本使用的 Inno Setup 许可和简体中文消息文件。两个文件均从官方发布包中原样复制，未改写内容、编码或换行；复制前后已核对字节尺寸及 SHA256。

| 文件 | 发布包来源 | 字节数 | SHA256 |
| --- | --- | --- | --- |
| [LICENSE.txt](LICENSE.txt) | Inno Setup 6.7.3 的 `license.txt` | 1,521 | `2E5346868C2A18434489824E11D65C3031620F792FEFC415D05F19CD441ABF5C` |
| [Languages/ChineseSimplified.isl](Languages/ChineseSimplified.isl) | Inno Setup 7.1.0 x64 的 `Languages/ChineseSimplified.isl` | 21,516 | `E0B0B350E2245F3C5E65586DFE43D574F6E7F06F2261149ABA284954B3FC9A8D` |

官方源码参照：[6.7.3 许可原文](https://raw.githubusercontent.com/jrsoftware/issrc/is-6_7_3/license.txt)、[7.1.0 简中消息原文](https://raw.githubusercontent.com/jrsoftware/issrc/is-7_1_0/Files/Languages/ChineseSimplified.isl)。上表校验值对应发布包解出的原件；源码参照链接用于核对上游版本与内容。

本轮使用 **Inno Setup 6.7.3** 编译器。其官方发布包未包含简中消息文件，因此从官方 7.1.0 x64 包补入；该语言文件头部明确标为适用于 **Inno Setup 6.5.0+**，并保留维护者和来源说明。已用 6.7.3 的 `ISCC.exe` 完成纯命令行语言编译验证，日志显示 `Successful compile`；此验证没有启动安装器界面或安装应用。

下载件记录如下。SHA256 为本机实际下载文件的校验值，未将其表述为上游另行发布的校验清单。

| 官方下载件 | 字节数 | 本地 SHA256 |
| --- | --- | --- |
| [innosetup-6.7.3.exe](https://github.com/jrsoftware/issrc/releases/download/is-6_7_3/innosetup-6.7.3.exe) | 10,592,232 | `9C73C3BAE7ED48D44112A0F48E66742C00090BDB5BEF71D9D3C056C66E97B732` |
| [innosetup-7.1.0-x64.exe](https://github.com/jrsoftware/issrc/releases/download/is-7_1_0/innosetup-7.1.0-x64.exe) | 14,304,168 | `0362A383ED217D4C4239B5933866DD96D3EB2102737DA92F80F6057A4B40DF2F` |

本机原件位于 `artifacts/inno-toolchain/6.7.3/{app}/license.txt` 和 `artifacts/inno-toolchain/7.1.0-x64/{app}/Languages/ChineseSimplified.isl`；下载件位于 `artifacts/inno-toolchain/download/`；语言编译记录为 `artifacts/inno-toolchain/compiler-language-probe.log`。这些本地产物不随仓库提供，普通构建无需重新取得原件。更新本目录文件时应保留新来源版本并重新记录校验值。
