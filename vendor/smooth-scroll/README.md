# SmoothScroll Interaction 本地补丁

应用继续使用官方 `SmoothScroll.Avalonia` 12.0.0.14（Controls），仅将其 Interaction 依赖固定到本地包 `SmoothScroll.Avalonia.Interaction` **12.0.0.15-heymelody.2**。这不是上游发布版本，也不覆盖官方同版包。AssemblyVersion 与 FileVersion 保持官方兼容的 `1.0.0.0`。较早 `.1` 原型保留，方向守卫等内容变化采用 `.2`，不以同版覆盖旧字节。

补丁以官方 commit `cff62b8c1351b52d09dcac303d8fefbcce36b276` 为基线。精细触控板输入组成连续 session，保留库的动画和受限弹性；session 的边界限制传入 server tracker，释放后撞边会重置惯性模型，避免隐藏能量；precision 使用独立连续采样间隔，过滤过低的原始速度并检查释放方向，避免 OS 惯性尾部再次被抬升成 fling 或反向。触摸／笔的原默认采样与弹性路径保留。具体文件及精确 pre/postimage 由 [UPSTREAM.json](UPSTREAM.json) 和 `precision-touchpad.patch` 共同确定，不以文件数量推断补丁范围。

普通应用构建只消费签入的 `vendor/nuget` 包，不执行本页的重建步骤，也不依赖 `research/`、源码下载、Git apply 或额外 PowerShell 工具。补丁的模拟输入验收不代表真实触控板、触屏／笔手感或 ARM64 硬件验证。

## 手动重建

需要 PowerShell 7、.NET 10 SDK 和 Git。先核对补丁及 UPSTREAM 中全部哈希已定稿；空哈希会在下载或构建前失败。

```powershell
pwsh.exe -NoProfile -File tools/Build-SmoothScrollPatch.ps1
```

默认从官方固定 commit 下载 ZIP，核对大小和 SHA256；可显式使用离线原件。`-SourceArchive` 仅免去源码下载，NuGet 依赖仍须已缓存或可获取。

```powershell
pwsh.exe -NoProfile -File tools/Build-SmoothScrollPatch.ps1 `
  -SourceArchive C:/archives/SmoothScroll.Avalonia-cff62b8c.zip `
  -OutputDir artifacts/smooth-scroll-rebuild `
  -PackagesPath artifacts/nuget-cache
```

需要代理时可显式加 `-Proxy http://127.0.0.1:59991`，同时用于源码下载和独立 restore 子进程。相对 OutputDir／PackagesPath 以仓库根解析；输出只进入全新 `build-<随机值>` 子目录，不递归删除旧目录。PackagesPath 只允许在工作区或显式 OutputDir 内。

脚本先校验 archive、补丁哈希、文件白名单与 preimage，再在新副本独立 Git 目录执行 `git apply --check` 和 apply，最后核对全部 postimage。它拒绝绝对／越界路径、增删／重命名／二进制补丁。独立工程固定 net10.0、Avalonia 12.1.4、IgnoresAccessChecksToGenerator 0.8.0，禁用上层 Directory.Build／中央版本输入，并固定 InformationalVersion／PathMap，避免混入本机应用 HEAD。

输出包含 nupkg、完整工具日志和 `build-receipt.json`。nupkg 内有 MIT 原文、README、UPSTREAM、patch 和 `source/patched/` 源码，来源文件不作为 runtime 或自动构建 hook。上游 MIT 的版权占位符原样保留，未补写作者或年份；包 Authors 沿用官方 Interaction 包的原字段。

构建完成后先核对 receipt、包身份、程序集版本和专项验收结果，再由维护者手动复制到 `vendor/nuget`。脚本不会修改该 feed；已有同名版本必须保留，内容变化应采用新的补丁版本。SDK 版本和包 SHA256 写入 receipt，源码与 DLL 可分别核对。未实测前不承诺每次 nupkg 的 ZIP 时间戳或压缩字节完全相同；相同版本号也不能证明包字节一致。

## 来源

- [官方仓库](https://github.com/zxbmmmmmmmmm/SmoothScroll.Avalonia)
- [固定源码 ZIP](https://codeload.github.com/zxbmmmmmmmmm/SmoothScroll.Avalonia/zip/cff62b8c1351b52d09dcac303d8fefbcce36b276)：5,447,482 字节，SHA256 `467050729AC3DA9F837876A5C7620BAA5D9B12FB5929AE621077F00353AF2081`。
- 本轮上游版本、分支、issue 与未采用方案的完整本地记录保存在 `research/smooth-scroll-review/20261024-upstream-trackpad/`，不参与普通构建。
