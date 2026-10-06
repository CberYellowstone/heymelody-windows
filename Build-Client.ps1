[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '0.5.0',
    [switch]$SkipTests,
    [switch]$FinalizeOnly
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$releaseRoot = Join-Path $root 'release'
$stage = Join-Path $releaseRoot ('HeyMelody_Windows_' + $Version)
$archive = Join-Path $releaseRoot ('HeyMelody_Windows_' + $Version + '_AllArchitectures.zip')
if ((Test-Path -LiteralPath $archive) -or ((Test-Path -LiteralPath $stage) -and -not $FinalizeOnly)) {
    throw 'This version already has an output directory or archive. Use a new version or review the existing output.'
}
if ($FinalizeOnly -and -not (Test-Path -LiteralPath $stage -PathType Container)) { throw 'There is no published directory to finalize.' }
if (-not $SkipTests -and -not $FinalizeOnly) {
    foreach ($project in @('HeyMelody.Daily.Tests', 'HeyMelody.Client.Tests')) {
        & dotnet run --project (Join-Path $root ('tests/' + $project + '/' + $project + '.csproj')) -c Release
        if ($LASTEXITCODE -ne 0) { throw ('Tests failed: ' + $project) }
    }
}
New-Item -ItemType Directory -Path $stage -Force | Out-Null
$scratch = Join-Path $releaseRoot ('build-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch | Out-Null
$builds = @()
$packageCacheLine = & dotnet nuget locals global-packages --list
if ($LASTEXITCODE -ne 0 -or $packageCacheLine -notmatch '^global-packages:\s*(.+?)\s*$') { throw 'NuGet package-cache path is unavailable.' }
$packageCache = $Matches[1]
foreach ($architecture in @('x64', 'x86', 'arm64')) {
    $rid = 'win-' + $architecture
    $app = Join-Path $stage $architecture
    $hostOutput = Join-Path $app 'backend'
    if (-not $FinalizeOnly) {
        Write-Host ('Publishing ' + $rid)
        & dotnet publish (Join-Path $root 'src/HeyMelody.App/HeyMelody.App.csproj') -c Release -r $rid --self-contained true -o $app '-p:PublishSingleFile=false' '-p:PublishTrimmed=false' ('-p:Version=' + $Version) --nologo
        if ($LASTEXITCODE -ne 0) { throw ('GUI publish failed: ' + $rid) }
        & dotnet publish (Join-Path $root 'src/HeyMelody.Host/HeyMelody.Host.csproj') -c Release -r $rid --self-contained true -o $hostOutput '-p:PublishSingleFile=false' '-p:PublishTrimmed=false' ('-p:Version=' + $Version) --nologo
        if ($LASTEXITCODE -ne 0) { throw ('Host publish failed: ' + $rid) }
    }
    $runtimeConfig = Get-Content -LiteralPath (Join-Path $app 'HeyMelody.runtimeconfig.json') -Raw | ConvertFrom-Json
    $runtimeVersion = @($runtimeConfig.runtimeOptions.includedFrameworks | Where-Object { $_.name -eq 'Microsoft.NETCore.App' })[0].version
    $corePackage = Join-Path $packageCache ('microsoft.netcore.app.runtime.' + $rid + '/' + $runtimeVersion)
    foreach ($runtimeDirectory in @($app, $hostOutput)) {
        Copy-Item -LiteralPath (Join-Path $corePackage 'LICENSE.TXT') -Destination (Join-Path $runtimeDirectory 'LICENSE.txt')
        Copy-Item -LiteralPath (Join-Path $corePackage 'THIRD-PARTY-NOTICES.TXT') -Destination (Join-Path $runtimeDirectory 'THIRD-PARTY-NOTICES.txt')
    }
    $desktopPackage = Join-Path $packageCache ('microsoft.windowsdesktop.app.runtime.' + $rid + '/' + $runtimeVersion)
    Copy-Item -LiteralPath (Join-Path $desktopPackage 'LICENSE') -Destination (Join-Path $app 'LICENSE-WINDOWSDESKTOP.txt')
    $builds += [ordered]@{
        architecture = $architecture; rid = $rid; selfContained = $true
        gui = ($architecture + '/HeyMelody.exe'); host = ($architecture + '/backend/HeyMelody.Host.exe')
        guiSha256 = (Get-FileHash -LiteralPath (Join-Path $app 'HeyMelody.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
        hostSha256 = (Get-FileHash -LiteralPath (Join-Path $hostOutput 'HeyMelody.Host.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
        hardwareExecutionVerified = $false; validationScope = 'build-only; see VALIDATION.json for actual execution evidence'
    }
}
Copy-Item -LiteralPath (Join-Path $root 'packaging/Start-HeyMelody.ps1') -Destination (Join-Path $stage 'Start-HeyMelody.ps1')
Copy-Item -LiteralPath (Join-Path $root 'packaging/Start-HeyMelody.cmd') -Destination (Join-Path $stage 'Start-HeyMelody.cmd')
Copy-Item -LiteralPath (Join-Path $root 'packaging/README.md') -Destination (Join-Path $stage 'README.md')
Copy-Item -LiteralPath (Join-Path $root 'packaging/SOURCE-NOTES.md') -Destination (Join-Path $stage 'SOURCE-NOTES.md')
if (Test-Path -LiteralPath (Join-Path $root 'artifacts/phase5-release-validation.json')) {
    Copy-Item -LiteralPath (Join-Path $root 'artifacts/phase5-release-validation.json') -Destination (Join-Path $stage 'VALIDATION.json')
}
$manifest = [ordered]@{ schemaVersion = 1; version = $Version; builtAtUtc = [DateTime]::UtcNow.ToString('o'); builds = $builds; nativeArm64ExecutionVerified = $false }
$manifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $stage 'manifest.json') -Encoding UTF8
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $archive, [System.IO.Compression.CompressionLevel]::Optimal, $false)
# This is the exact owned GUID directory created above, verified inside release before recursive cleanup.
$resolvedScratch = [System.IO.Path]::GetFullPath($scratch)
$resolvedRelease = [System.IO.Path]::GetFullPath($releaseRoot).TrimEnd('\') + '\'
if (-not $resolvedScratch.StartsWith($resolvedRelease, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe build scratch path.' }
Remove-Item -LiteralPath $resolvedScratch -Recurse -Force
[ordered]@{ package = $archive; sha256 = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant(); version = $Version } | ConvertTo-Json -Compress
