# Linux Filament 构建复现工具

`run-case.sh` 在已准备好的 Linux sandbox 内运行现有构建与验收入口，保存每步完整日志、真实退出码、工具版本、输入摘要及首个构建错误。本目录是可维护工具，不记录某次运行已经通过。

## 调用

从 sandbox 内的**临时项目副本根目录**运行。旧提交的副本不必包含本工具；以下假设脚本作为只读输入挂载在 `/inputs/downloads/run-case.sh`：

```bash
bash /inputs/downloads/run-case.sh baseline \
  --source /inputs/filament \
  --output-root /work/cases/baseline/output \
  --evidence /work/evidence/baseline

bash /inputs/downloads/run-case.sh fixed \
  --source /inputs/filament \
  --output-root /work/cases/fixed/output \
  --evidence /work/evidence/fixed \
  --parallel 8 --runtime-identifier linux-x64
```

`--source` 是固定 commit 的干净 Filament Git checkout。项目代码与 baseline/fixed 差异由调用者准备；工具不会 checkout、修改源码或应用补丁。两组必须使用独立项目副本与新的构建输出。输出和证据目录可不存在或为空，不复用已有结果。

在 Windows 导出项目副本时，必须同时指定 `core.autocrlf=false` 和 `core.eol=lf`。例如，替换下列输出路径与 baseline SHA 后执行：

```powershell
git -c core.autocrlf=false -c core.eol=lf archive --format=tar --output=C:/Temp/filament-baseline-lf.tar cfaa0ed4b2ea3fea634053b13d342d822773fb76
```

本次排查中，默认导出的 tar 内 `CMakeLists.txt` 是 CRLF，即使 Git blob 为 LF；仅加 `core.autocrlf=false` 仍导出 CRLF，导致 LF 格式的 Git diff 补丁无法在 Linux 应用。将两项配置都固定后，先核对导出文本为 LF，再应用修复补丁；失败的原始输入与操作记录应另行保全，不覆盖原稿。

默认 RID 为 `linux-x64`，并行度为 8；可显式指定 `linux-arm64`，构建脚本仍检查实际主机架构。工具固定设置 `CC=clang-18`、`CXX=clang++-18`，要求 sandbox 中已有 PowerShell、.NET、Clang 18、CMake、Ninja、Git、Python3 和标准 Linux 命令。它不下载 rootfs、不安装依赖，也不修改宿主包、配置或缓存。

调用者须将 `HOME`、`XDG_CACHE_HOME`、`DOTNET_CLI_HOME`、`NUGET_PACKAGES`、`TMPDIR` 和其它必要缓存隔离到临时任务目录，并在 sandbox 内提供需要的网络与系统库。项目副本必须可写，因为现有 .NET 入口生成 `obj/bin`，portable 验收将报告写到该副本的 `artifacts/`。

## agentbox 外层入口

先在 agentbox 以 root 调用 `prepare-agentbox.sh /var/tmp/hm-filament-repro-<唯一编号>`。它下载并校验固定 Ubuntu Base 24.04.5、PowerShell 7.6.6、.NET SDK 10.0.402，仅在 bwrap rootfs 内安装 Clang 18 和构建依赖；完整准备日志写入任务目录的 `evidence/setup.log`。同一标记目录可继续未完成的准备，已有非空且缺少正确标记的目录会被拒绝。

固定 Filament 源码通过宿主已有 Git 下载到任务目录，再由 guest Git 核对提交与清洁状态。该方式沿用宿主现有 HTTPS 信任，不复制私人配置或关闭 TLS 校验；隔离 rootfs 的包安装、运行依赖及 NuGet 请求仍使用其自身信任。完成后 `evidence/setup.ready` 表示工具和源码已核对。

`run-agentbox.sh` 使用已安装的 bwrap，读取准备好的临时任务目录，挂载只读 rootfs 与 Filament 源码，再以 UID/GID 1000 调用 `run-case.sh`。从本仓库根目录、在 agentbox 中调用：

```bash
sudo bash tools/research/filament-linux-build/run-agentbox.sh \
  /var/tmp/hm-filament-repro-example baseline
sudo bash tools/research/filament-linux-build/run-agentbox.sh \
  /var/tmp/hm-filament-repro-example fixed
```

远程 Windows 终端也可直接指定 WSL root 用户，替换示例脚本路径与任务目录：

```powershell
wsl.exe -d agentbox -u root -- bash /mnt/c/path/to/repo/tools/research/filament-linux-build/run-agentbox.sh /var/tmp/hm-filament-repro-example baseline
wsl.exe -d agentbox -u root -- bash /mnt/c/path/to/repo/tools/research/filament-linux-build/run-agentbox.sh /var/tmp/hm-filament-repro-example fixed
```

外层调用者必须先准备 `rootfs/`、`inputs/run-case.sh`、`sources/filament/.git`、`cases/baseline/project/`、`cases/fixed/project/` 及 `cache/`、`evidence/`、`home/`、`tmp/`。`.hm-filament-repro` 标记文件内容必须是任务目录 basename；目录必须位于 `/var/tmp/hm-filament-repro-` 前缀下。两份项目、cache、home、tmp 和输出父目录须供 UID/GID 1000 写入。

外层入口固定隔离 PATH、HOME、XDG、.NET、NuGet、临时目录，固定并行度 8；每组输出为 `cases/<case>/output/`，证据为 `evidence/<case>/`。它不安装 rootfs 或依赖，不自动应用补丁，也不清理任务目录。baseline 的预期构建失败仍返回非零；分别捕获其退出码后再运行 fixed，不能用 `baseline && fixed` 串接。

## 结果与退出码

- `baseline` 使用与 CI 相同的 `-FetchBuildTools -SkipSmoke` 入口。构建失败时保留其真实非零退出码，生成 `baseline-build-failed` 摘要；调用者须检查首个错误是否为待复现问题。提前发生的环境或依赖失败不能当作目标问题已复现。baseline 意外通过时工具返回 1，构建步骤自己的退出码仍记录为 0。
- `fixed` 构建成功后依次运行不带模型参数的 C ABI smoke、`Verify-FilamentRuntime.ps1 -RequireCurrent` 和两个现有 portable 验收；任一步失败立即返回对应非零退出码，只有全部通过且报告已收集才返回 0。
- 独立 ABI smoke 使用 `env -u LD_LIBRARY_PATH`，同时检查构建树的 RPATH 是否足以定位这次输出的桥接库。
- ABI smoke 只检查 ABI 2 版本、结构布局与非法后端参数，不创建有效 renderer；这里的成功不证明 GPU、EGL/Vulkan 驱动或真实图形效果可用。

终端仅打印步骤、退出码和证据位置。每步完整输出保存在 `<step>.log`，命令与工作目录在 `<step>.command.txt`，退出码在 `<step>.exit-code`。`summary.json` 汇总步骤、输入位置和首个构建错误；`first-build-error.json` 单独保存可定位的首错。`SHA256SUMS` 覆盖证据文件。fixed 还保存两个 portable 报告和静态运行时验证 JSON。

`environment.command.txt` 等函数步骤由本脚本实现，归档时须同时保留本次脚本原字节及 SHA256。完整环境日志只投影指定变量，不倾倒可能含凭据的环境全集。日志可能很大，读取前仍遵循根 [AGENTS.md](../../../AGENTS.md) 的尺寸、结构预览和分段限制。

## 归档与清理

外层操作负责记录 rootfs 来源与 SHA256、GitHub run/job/SHA、包安装过程、baseline/fixed 源码或补丁摘要，以及 sandbox/WSL 的边界。Ubuntu 24.04 rootfs 可匹配 CI 用户空间和 Clang 主版本，WSL 内核、包补丁、runner image 和图形驱动仍可能不同。

删除临时目录前，先将原始 CI 日志、本次工具原件、两组证据、最终 runtime/manifest/build receipt 和必要 CMake 配置归档到新的本地 `research/` 专题，并核验归档尺寸与 SHA256。公开文档只整理结论、依据和验证范围，不提交完整 rootfs、包缓存、构建树或私人设备资料。

本工具不执行清理。外层先结束本任务进程、退出 bwrap 并确认无残留挂载，再核验临时任务绝对路径后删除该目录；不得按宽泛通配符清理宿主目录或全局缓存。根 [研究工具说明](../README.md) 提供完整档案与公开摘要的组织规则。
