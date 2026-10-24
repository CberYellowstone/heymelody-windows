#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$SourceArchive,
    [string]$OutputDir,
    [string]$PackagesPath,
    [uri]$Proxy
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$vendorRoot = Join-Path $repoRoot 'vendor/smooth-scroll'
$specPath = Join-Path $vendorRoot 'UPSTREAM.json'
$specJson = Get-Content -Raw -LiteralPath $specPath
$spec = $specJson | ConvertFrom-Json

function SafeChild([string]$Parent, [string]$Relative) {
    if ([IO.Path]::IsPathRooted($Relative) -or $Relative.Contains(':') -or
        @($Relative -split '[\\/]' | Where-Object { $_ -in '.', '..' }).Count) { throw "Unsafe relative path: $Relative" }
    $parentPath = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Parent))
    $path = [IO.Path]::GetFullPath((Join-Path $parentPath $Relative))
    $prefix = if ([IO.Path]::EndsInDirectorySeparator($parentPath)) { $parentPath } else { $parentPath + [IO.Path]::DirectorySeparatorChar }
    if (-not $path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path leaves its allowed directory: $Relative"
    }
    return $path
}
function VerifyHash([string]$Path, [string]$Expected) {
    if ($Expected -notmatch '^[a-fA-F0-9]{64}$') { throw "Expected SHA256 is not finalized: $Path" }
    if ((Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash -ine $Expected) { throw "SHA256 mismatch: $Path" }
}
function RunTool([string]$File, [string[]]$Arguments, [string]$Log, [string]$Directory) {
    $info = [Diagnostics.ProcessStartInfo]::new($File)
    $info.WorkingDirectory = $Directory
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $info.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    foreach ($key in @($info.Environment.Keys | Where-Object { $_ -like 'GIT_*' })) { [void]$info.Environment.Remove($key) }
    $info.Environment['AVALONIA_TELEMETRY_OPTOUT'] = '1'
    $info.Environment['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'
    if ($Proxy) {
        $info.Environment['HTTP_PROXY'] = $info.Environment['HTTPS_PROXY'] = $Proxy.AbsoluteUri
        $info.Environment['NO_PROXY'] = ''
    }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $info
    try {
        [void]$process.Start()
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $text = $stdout.GetAwaiter().GetResult()
        [IO.File]::WriteAllText($Log, $text + $stderr.GetAwaiter().GetResult(), [Text.UTF8Encoding]::new($false))
        if ($process.ExitCode -ne 0) { throw "$File failed ($($process.ExitCode)); see $Log" }
        return $text
    }
    finally { $process.Dispose() }
}

if ($spec.schemaVersion -ne 1 -or $spec.upstream.commit -cne 'cff62b8c1351b52d09dcac303d8fefbcce36b276' -or
    $spec.upstream.archiveRoot -cne ('SmoothScroll.Avalonia-' + $spec.upstream.commit) -or
    $spec.package.id -cne 'SmoothScroll.Avalonia.Interaction' -or
    $spec.package.version -cnotmatch '^12\.0\.0\.15-heymelody\.[1-9][0-9]*$' -or
    $spec.package.targetFramework -cne 'net10.0' -or $spec.package.dependencies.Avalonia -cne '12.1.4' -or
    $spec.package.dependencies.IgnoresAccessChecksToGenerator -cne '0.8.0' -or
    $spec.package.assemblyVersion -cne '1.0.0.0' -or $spec.package.fileVersion -cne '1.0.0.0') {
    throw 'Unsupported upstream snapshot or patched package identity.'
}
$patchPath = SafeChild $vendorRoot $spec.patch.file
VerifyHash $patchPath $spec.patch.sha256
$allowed = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($file in $spec.patch.files) {
    if (-not $file.path.StartsWith('src/SmoothScroll.Avalonia.Interaction/', [StringComparison]::Ordinal) -or
        -not $file.path.EndsWith('.cs', [StringComparison]::Ordinal) -or -not $allowed.Add($file.path)) {
        throw "Invalid or duplicate patch target: $($file.path)"
    }
    [void](SafeChild $repoRoot $file.path)
    if ($file.preimageSha256 -notmatch '^[a-fA-F0-9]{64}$' -or $file.postimageSha256 -notmatch '^[a-fA-F0-9]{64}$') {
        throw "Pre/postimage hashes are not finalized: $($file.path)"
    }
}
if ($allowed.Count -eq 0) { throw 'The patch target list is empty.' }
$headers = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($line in [IO.File]::ReadLines($patchPath)) {
    if ($line.StartsWith('diff --git ')) {
        if ($line -notmatch '^diff --git a/(\S+) b/(\S+)$') { throw 'Unsupported patch header.' }
        $oldPath, $newPath = $Matches[1], $Matches[2]
        if ($oldPath -cne $newPath -or -not $allowed.Contains($newPath) -or -not $headers.Add($newPath)) {
            throw "Unexpected patch target: $newPath"
        }
    }
    if ($line.StartsWith('--- ') -or $line.StartsWith('+++ ')) {
        if ($line -notmatch '^(--- a|\+\+\+ b)/(\S+)$' -or -not $allowed.Contains($Matches[2])) {
            throw 'Unexpected old/new filename in patch.'
        }
    }
    if ($line -match '^(new file mode|deleted file mode|old mode|new mode|rename |copy |GIT binary patch|--- /dev/null|\+\+\+ /dev/null)') {
        throw 'Only text edits to the declared existing C# files are allowed.'
    }
}
if (-not $headers.SetEquals($allowed)) { throw 'Patch headers do not match UPSTREAM.json.' }

$outputRoot = if ($OutputDir) {
    [IO.Path]::GetFullPath($(if ([IO.Path]::IsPathRooted($OutputDir)) { $OutputDir } else { Join-Path $repoRoot $OutputDir }))
} else { Join-Path $repoRoot 'artifacts/smooth-scroll-patch' }
$outputRoot = [IO.Path]::TrimEndingDirectorySeparator($outputRoot)
if ($outputRoot -ieq [IO.Path]::GetPathRoot($outputRoot)) { throw 'OutputDir must be a dedicated directory, not a volume root.' }
if ($outputRoot.IndexOfAny([char[]]';,%=') -ge 0) { throw 'OutputDir contains unsupported MSBuild PathMap separators.' }
$feedRoot = Join-Path $repoRoot 'vendor/nuget'
if ($outputRoot -ieq $feedRoot -or $outputRoot.StartsWith($feedRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Build output must not be inside the checked-in NuGet feed.'
}
[void][IO.Directory]::CreateDirectory($outputRoot)
$runRoot = SafeChild $outputRoot ('build-' + [Guid]::NewGuid().ToString('N'))
if (Test-Path -LiteralPath $runRoot) { throw 'The new build directory already exists.' }
[void][IO.Directory]::CreateDirectory($runRoot)
$patchSnapshot = SafeChild $runRoot 'precision-touchpad.patch'
Copy-Item -LiteralPath $patchPath -Destination $patchSnapshot
VerifyHash $patchSnapshot $spec.patch.sha256
$patchPath = $patchSnapshot
$archive = SafeChild $runRoot 'upstream.zip'
if ($SourceArchive) { Copy-Item -LiteralPath (Resolve-Path -LiteralPath $SourceArchive).Path -Destination $archive }
else {
    $request = @{ Uri = $spec.upstream.archiveUrl; OutFile = $archive }
    if ($Proxy) { $request.Proxy = $Proxy }
    Invoke-WebRequest @request
}
VerifyHash $archive $spec.upstream.archiveSha256
if ((Get-Item -LiteralPath $archive).Length -ne $spec.upstream.archiveBytes) { throw 'Unexpected archive length.' }
$extractRoot = SafeChild $runRoot 'source'
$zip = [IO.Compression.ZipFile]::OpenRead($archive)
try {
    foreach ($entry in $zip.Entries) {
        $name = $entry.FullName.Replace('\', '/')
        if (-not $name.StartsWith($spec.upstream.archiveRoot + '/', [StringComparison]::Ordinal)) { throw "Unexpected archive entry: $name" }
        [void](SafeChild $extractRoot $name)
        if ((($entry.ExternalAttributes -shr 16) -band 0xf000) -eq 0xa000) { throw "Archive links are not allowed: $name" }
    }
}
finally { $zip.Dispose() }
[IO.Compression.ZipFile]::ExtractToDirectory($archive, $extractRoot)
$sourceRoot = SafeChild $extractRoot $spec.upstream.archiveRoot
foreach ($file in $spec.patch.files) { VerifyHash (SafeChild $sourceRoot $file.path) $file.preimageSha256 }
$gitDir = SafeChild $sourceRoot '.git'
$gitArgs = @("--git-dir=$gitDir", "--work-tree=$sourceRoot", '-c', 'core.autocrlf=false', '-c', 'core.longpaths=true')
$null = RunTool 'git' ($gitArgs + @('init', '--quiet')) (SafeChild $runRoot 'git-init.log') $sourceRoot
# Preserve the archived bytes instead of applying the upstream working-tree EOL policy.
[void][IO.Directory]::CreateDirectory((SafeChild $gitDir 'info'))
[IO.File]::WriteAllText((SafeChild $gitDir 'info/attributes'), "* -text`n", [Text.UTF8Encoding]::new($false))
$null = RunTool 'git' ($gitArgs + @('apply', '--check', '--whitespace=error-all', $patchPath)) (SafeChild $runRoot 'patch-check.log') $sourceRoot
$null = RunTool 'git' ($gitArgs + @('apply', '--whitespace=error-all', $patchPath)) (SafeChild $runRoot 'patch-apply.log') $sourceRoot
foreach ($file in $spec.patch.files) { VerifyHash (SafeChild $sourceRoot $file.path) $file.postimageSha256 }
VerifyHash (SafeChild $sourceRoot $spec.license.file) $spec.license.sha256

$buildRoot = SafeChild $runRoot 'build'
$provenance = SafeChild $buildRoot 'provenance'
[void][IO.Directory]::CreateDirectory($provenance)
[IO.File]::WriteAllText((SafeChild $provenance 'UPSTREAM.json'), $specJson, [Text.UTF8Encoding]::new($false))
Copy-Item -LiteralPath $patchPath -Destination (SafeChild $provenance 'precision-touchpad.patch')
Copy-Item -LiteralPath (Join-Path $vendorRoot 'README.md') -Destination (SafeChild $provenance 'README.md')
Copy-Item -LiteralPath (SafeChild $sourceRoot $spec.license.file) -Destination (SafeChild $provenance 'LICENSE.txt')
$projectPath = SafeChild $buildRoot 'Interaction.Patch.csproj'
$sourceGlob = '../source/' + $spec.upstream.archiveRoot + '/src/SmoothScroll.Avalonia.Interaction/**/*.cs'
$sourceFiles = '../source/' + $spec.upstream.archiveRoot + '/src/SmoothScroll.Avalonia.Interaction/**/*'
$project = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><LangVersion>latest</LangVersion>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems><EnableDefaultNoneItems>false</EnableDefaultNoneItems>
    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally><TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <AssemblyName>SmoothScroll.Avalonia.Interaction</AssemblyName><RootNamespace>SmoothScroll.Avalonia.Interaction</RootNamespace>
    <PackageId>$($spec.package.id)</PackageId><Version>$($spec.package.version)</Version><PackageVersion>$($spec.package.version)</PackageVersion>
    <AssemblyVersion>1.0.0.0</AssemblyVersion><FileVersion>1.0.0.0</FileVersion>
    <InformationalVersion>$($spec.package.version)</InformationalVersion>
    <Authors>$($spec.package.authors)</Authors><Description>HeyMelody precision touchpad patch of upstream 12.0.0.14; see source/UPSTREAM.json.</Description>
    <PackageReadmeFile>README.md</PackageReadmeFile><PackageLicenseFile>LICENSE.txt</PackageLicenseFile>
    <RepositoryUrl>$($spec.upstream.repository)</RepositoryUrl><RepositoryCommit>$($spec.upstream.commit)</RepositoryCommit>
    <Deterministic>true</Deterministic><ContinuousIntegrationBuild>true</ContinuousIntegrationBuild>
    <IncludeSourceRevisionInInformationalVersion>false</IncludeSourceRevisionInInformationalVersion>
    <EnableSourceControlManagerQueries>false</EnableSourceControlManagerQueries>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="$sourceGlob" />
    <IgnoresAccessChecksTo Include="Avalonia.Base" />
    <PackageReference Include="Avalonia" Version="12.1.4" />
    <PackageReference Include="IgnoresAccessChecksToGenerator" Version="0.8.0" PrivateAssets="all" />
    <None Include="provenance/README.md" Pack="true" PackagePath="README.md" />
    <None Include="provenance/LICENSE.txt" Pack="true" PackagePath="LICENSE.txt" />
    <None Include="provenance/UPSTREAM.json;provenance/precision-touchpad.patch" Pack="true" PackagePath="source/" />
    <None Include="$sourceFiles" Exclude="**/bin/**;**/obj/**" Pack="true" PackagePath="source/patched/%(RecursiveDir)%(Filename)%(Extension)" />
  </ItemGroup>
</Project>
"@
[IO.File]::WriteAllText($projectPath, $project, [Text.UTF8Encoding]::new($false))
$config = SafeChild $buildRoot 'nuget-sources.config'
[IO.File]::WriteAllText($config, '<configuration><packageSources><clear/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources></configuration>')
$packagesRoot = if ($PackagesPath) {
    [IO.Path]::GetFullPath($(if ([IO.Path]::IsPathRooted($PackagesPath)) { $PackagesPath } else { Join-Path $repoRoot $PackagesPath }))
} else { SafeChild $runRoot 'packages' }
if (-not ($packagesRoot.StartsWith($repoRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
    $packagesRoot.StartsWith($outputRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase))) { throw 'PackagesPath must be inside the workspace or OutputDir.' }
if ($packagesRoot -ieq $feedRoot -or $packagesRoot.StartsWith($feedRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The checked-in feed must not be used as a mutable package cache.'
}
$sdk = (RunTool 'dotnet' @('--version') (SafeChild $runRoot 'sdk.log') $buildRoot).Trim()
if ($sdk -notmatch '^10\.') { throw "A .NET 10 SDK is required, found $sdk" }
$buildProperties = @('-p:ImportDirectoryBuildProps=false', '-p:ImportDirectoryBuildTargets=false',
    '-p:ManagePackageVersionsCentrally=false', '-p:NuGetAudit=false', '-p:UsedAvaloniaProducts=')
$null = RunTool 'dotnet' (@('restore', $projectPath, '--configfile', $config, '--packages', $packagesRoot) + $buildProperties) (SafeChild $runRoot 'restore.log') $buildRoot
$packRoot = SafeChild $runRoot 'packages-built'
$null = RunTool 'dotnet' (@('pack', $projectPath, '-c', 'Release', '--no-restore', '--output', $packRoot, ('-p:PathMap=' + $runRoot + '=/_/smooth-scroll-build')) + $buildProperties) (SafeChild $runRoot 'pack.log') $buildRoot
$package = SafeChild $packRoot ($spec.package.id + '.' + $spec.package.version + '.nupkg')
$assembly = SafeChild $buildRoot 'bin/Release/net10.0/SmoothScroll.Avalonia.Interaction.dll'
$versionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($assembly)
if ([Reflection.AssemblyName]::GetAssemblyName($assembly).Version.ToString() -ne '1.0.0.0' -or
    $versionInfo.FileVersion -ne '1.0.0.0' -or $versionInfo.ProductVersion -cne $spec.package.version) {
    throw 'Assembly/File/Informational version compatibility check failed.'
}
$zip = [IO.Compression.ZipFile]::OpenRead($package)
try {
    $nuspec = $zip.Entries | Where-Object { $_.FullName.EndsWith('.nuspec') } | Select-Object -First 1
    $reader = [IO.StreamReader]::new($nuspec.Open())
    try { [xml]$metadata = $reader.ReadToEnd() } finally { $reader.Dispose() }
    if ($metadata.package.metadata.id -cne $spec.package.id -or $metadata.package.metadata.version -cne $spec.package.version) { throw 'Packed identity does not match the specification.' }
    foreach ($name in 'LICENSE.txt', 'README.md', 'source/UPSTREAM.json', 'source/precision-touchpad.patch') {
        if (-not $zip.GetEntry($name)) { throw "Missing package provenance: $name" }
    }
    $licenseStream = $zip.GetEntry('LICENSE.txt').Open()
    try { $licenseHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($licenseStream)) }
    finally { $licenseStream.Dispose() }
    if ($licenseHash -ine $spec.license.sha256) { throw 'Packed license differs from upstream.' }
    if (-not @($zip.Entries | Where-Object { $_.FullName -match '^source/patched/.+\.cs$' }).Count) { throw 'Patched sources are missing from the package.' }
    if (@($zip.Entries | Where-Object { $_.FullName -match '^(build|buildTransitive|buildMultiTargeting|analyzers|tools|contentFiles)/' }).Count) {
        throw 'The package must not install automatic build hooks or extra compile tools.'
    }
}
finally { $zip.Dispose() }
$receipt = [ordered]@{
    schemaVersion = 1; success = $true; sdk = $sdk; packageId = $spec.package.id; version = $spec.package.version
    upstreamCommit = $spec.upstream.commit; archiveSha256 = $spec.upstream.archiveSha256; patchSha256 = $spec.patch.sha256
    package = $package; packageBytes = (Get-Item -LiteralPath $package).Length; packageSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $package).Hash
    assemblyVersion = '1.0.0.0'; fileVersion = '1.0.0.0'; informationalVersion = $versionInfo.ProductVersion
    assemblySha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $assembly).Hash; packageFeedModified = $false
    scope = 'Manual dependency rebuild only; no application, UI, installer or hardware execution.'
}
$receiptPath = SafeChild $runRoot 'build-receipt.json'
[IO.File]::WriteAllText($receiptPath, ($receipt | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
Write-Output ($receipt | ConvertTo-Json -Compress)
Write-Output "Receipt: $receiptPath"
