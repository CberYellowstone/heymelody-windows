#Requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackageRoot,
    [ValidateNotNullOrEmpty()][ValidateSet('x64', 'arm64')][string[]]$Architectures = @('x64'),
    [string]$OutputRoot = (Join-Path $PSScriptRoot 'release/installers'),
    [string]$MakeNsisPath
)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Build this Windows installer on Windows with PowerShell 7.4 or later.' }
$repositoryRoot = $PSScriptRoot
$PackageRoot = (Resolve-Path -LiteralPath $PackageRoot).Path
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$architecturesToBuild = @($Architectures | Select-Object -Unique)

function Read-SmallJson([string]$Path) {
    if ((Get-Item -LiteralPath $Path).Length -gt 1MB) { throw "Unexpectedly large metadata: $Path" }
    return [IO.File]::ReadAllText($Path) | ConvertFrom-Json
}

function ConvertTo-NsisLiteral([string]$Value) {
    if ($Value.Contains("`n") -or $Value.Contains("`r")) { throw 'NSIS paths cannot contain line breaks.' }
    return $Value.Replace('$', '$$').Replace('"', '$\"')
}

function Assert-NoReparsePoints([string]$Directory) {
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($Directory)
    while ($pending.Count) {
        $entry = Get-Item -LiteralPath $pending.Pop() -Force
        if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Package links are not supported: $($entry.FullName)" }
        if ($entry.PSIsContainer) {
            foreach ($child in Get-ChildItem -LiteralPath $entry.FullName -Force) { $pending.Push($child.FullName) }
        }
    }
}

if (-not $MakeNsisPath) {
    $compiler = Get-Command makensis.exe -ErrorAction SilentlyContinue
    if ($compiler) { $MakeNsisPath = $compiler.Source }
    else {
        foreach ($programFilesRoot in @(${env:ProgramFiles(x86)}, $env:ProgramFiles)) {
            if (-not $programFilesRoot) { continue }
            $candidate = Join-Path $programFilesRoot 'NSIS/makensis.exe'
            if (Test-Path -LiteralPath $candidate -PathType Leaf) { $MakeNsisPath = $candidate; break }
        }
    }
}
if (-not $MakeNsisPath) { throw 'Install NSIS 3 (https://nsis.sourceforge.io/Download) or pass -MakeNsisPath.' }
$MakeNsisPath = (Resolve-Path -LiteralPath $MakeNsisPath).Path
$compilerVersion = (& $MakeNsisPath /VERSION | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $compilerVersion -notmatch '^v3\.') { throw 'NSIS 3 is required.' }

$manifestPath = Join-Path $PackageRoot 'manifest.json'
$manifest = Read-SmallJson $manifestPath
$version = [string]$manifest.version
if ($manifest.schemaVersion -ne 2 -or $version -notmatch '^\d+\.\d+\.\d+$') { throw 'Expected a Build-Client schema-2 package manifest and three-part version.' }
$numericVersion = [version]$version
if ($numericVersion.Major -gt 65535 -or $numericVersion.Minor -gt 65535 -or $numericVersion.Build -gt 65535) { throw 'Version components exceed Windows file-version limits.' }
foreach ($architecture in $architecturesToBuild) {
    $build = @($manifest.builds | Where-Object architecture -eq $architecture)
    if ($build.Count -ne 1 -or -not $build[0].selfContained -or $build[0].rid -ne "win-$architecture") { throw "Package does not contain a self-contained win-$architecture build." }
    foreach ($relative in @("$architecture/HeyMelody.exe", "$architecture/HeyMelody.DeviceManager.exe", "$architecture/backend/HeyMelody.Host.exe", "$architecture/HeyMelody.runtimeconfig.json", 'SOURCE-NOTES.md')) {
        if (-not (Test-Path -LiteralPath (Join-Path $PackageRoot $relative) -PathType Leaf)) { throw "Missing package file: $relative" }
    }
    $installerPath = Join-Path $OutputRoot "HeyMelody_Windows_${version}_${architecture}_Setup.exe"
    if (Test-Path -LiteralPath $installerPath) { throw "Installer already exists; select a new output directory: $installerPath" }
    if (Test-Path -LiteralPath "$installerPath.json") { throw "Installer receipt already exists: $installerPath.json" }
}
Assert-NoReparsePoints $PackageRoot
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
$workRoot = Join-Path $repositoryRoot ('artifacts/nsis-build-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $workRoot | Out-Null
$utf8Bom = [Text.UTF8Encoding]::new($true)
$packageCache = Join-Path $repositoryRoot 'artifacts/nuget-cache'
$restoreArguments = @("-p:RestorePackagesPath=$packageCache")
$fallbackCache = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget/packages'
if (Test-Path -LiteralPath $fallbackCache -PathType Container) { $restoreArguments += "-p:RestoreFallbackFolders=$fallbackCache" }

foreach ($architecture in $architecturesToBuild) {
    $work = Join-Path $workRoot $architecture
    $payload = Join-Path $work 'payload'
    New-Item -ItemType Directory -Path $payload -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $PackageRoot $architecture) -Destination $payload -Recurse
    # Root launchers, provenance and notices stay with the selected architecture.
    foreach ($file in Get-ChildItem -LiteralPath $PackageRoot -File -Force) {
        Copy-Item -LiteralPath $file.FullName -Destination $payload
    }
    foreach ($name in @('README.md', 'README-使用说明.md')) {
        Copy-Item -LiteralPath (Join-Path $repositoryRoot 'packaging/README.md') -Destination (Join-Path $payload $name) -Force
    }
    # Narrow the package manifest so it does not advertise an absent architecture.
    $payloadManifest = Read-SmallJson $manifestPath
    $payloadManifest.builds = @($payloadManifest.builds | Where-Object architecture -eq $architecture)
    $payloadManifest | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $payload 'manifest.json') -Encoding utf8
    $runtimeConfig = Read-SmallJson (Join-Path $payload "$architecture/HeyMelody.runtimeconfig.json")
    $framework = @($runtimeConfig.runtimeOptions.includedFrameworks | Where-Object name -eq 'Microsoft.NETCore.App')
    if ($framework.Count -ne 1 -or $framework[0].version -notmatch '^10\.\d+\.\d+$') { throw 'The GUI must include the .NET 10 runtime.' }
    $helperOutput = Join-Path $work 'helper'
    & dotnet publish (Join-Path $repositoryRoot 'tools/HeyMelody.InstallerSupport/HeyMelody.InstallerSupport.csproj') -c Release -r "win-$architecture" --self-contained true -o $helperOutput "-p:RuntimeFrameworkVersion=$($framework[0].version)" '-p:PublishSingleFile=true' '-p:PublishTrimmed=true' '-p:IncludeNativeLibrariesForSelfExtract=true' '-p:EnableCompressionInSingleFile=false' '-p:CopyDebugSymbolFilesFromPackages=false' @restoreArguments
    if ($LASTEXITCODE -ne 0) { throw "Installer helper publish failed: $architecture" }
    # A standalone helper runs from NSIS's temporary directory, including on retry
    # after an interrupted uninstall has removed part of the application's runtime.
    $files = @(Get-ChildItem -LiteralPath $payload -File -Recurse -Force | Sort-Object FullName)
    $installLines = [Collections.Generic.List[string]]::new()
    $uninstallLines = [Collections.Generic.List[string]]::new()
    $relativeFiles = [Collections.Generic.List[string]]::new()
    $lastDirectory = $null
    foreach ($file in $files) {
        $relative = [IO.Path]::GetRelativePath($payload, $file.FullName).Replace('/', '\')
        $relativeFiles.Add($relative)
        $directory = [IO.Path]::GetDirectoryName($relative)
        if ($lastDirectory -cne $directory) {
            $target = if ($directory) { '\' + (ConvertTo-NsisLiteral $directory) } else { '' }
            $installLines.Add('SetOutPath "$INSTDIR' + $target + '"')
            $lastDirectory = $directory
        }
        $installLines.Add('File "' + (ConvertTo-NsisLiteral $file.FullName) + '"')
        $uninstallLines.Add('Delete "$INSTDIR\' + (ConvertTo-NsisLiteral $relative) + '"')
    }
    $directories = @(Get-ChildItem -LiteralPath $payload -Directory -Recurse -Force | Sort-Object { $_.FullName.Length } -Descending)
    $removeDirectoryLines = foreach ($directory in $directories) {
        'RMDir "$INSTDIR\' + (ConvertTo-NsisLiteral ([IO.Path]::GetRelativePath($payload, $directory.FullName).Replace('/', '\'))) + '"'
    }
    [IO.File]::WriteAllLines((Join-Path $work 'install-files.nsh'), $installLines, $utf8Bom)
    [IO.File]::WriteAllLines((Join-Path $work 'uninstall-files.nsh'), $uninstallLines, $utf8Bom)
    [IO.File]::WriteAllLines((Join-Path $work 'uninstall-directories.nsh'), [string[]]$removeDirectoryLines, $utf8Bom)
    $installerPath = Join-Path $OutputRoot "HeyMelody_Windows_${version}_${architecture}_Setup.exe"
    $definitions = @(
        '!define APP_VERSION "' + $version + '"'
        '!define APP_ARCH "' + $architecture + '"'
        '!define OUTPUT_FILE "' + (ConvertTo-NsisLiteral $installerPath) + '"'
        '!define GENERATED_DIR "' + (ConvertTo-NsisLiteral $work) + '"'
        '!define APP_ICON "' + (ConvertTo-NsisLiteral (Join-Path $repositoryRoot 'src/HeyMelody.App/Assets/HeyMelody.ico')) + '"'
        '!define HELPER_EXE "' + (ConvertTo-NsisLiteral (Join-Path $helperOutput 'HeyMelody.InstallerSupport.exe')) + '"'
        '!define ESTIMATED_SIZE ' + [Math]::Ceiling(($files | Measure-Object Length -Sum).Sum / 1KB)
    )
    $configPath = Join-Path $work 'build-config.nsh'
    [IO.File]::WriteAllLines($configPath, $definitions, $utf8Bom)
    & $MakeNsisPath /NOCONFIG /INPUTCHARSET UTF8 /V2 "/DBUILD_CONFIG=$configPath" (Join-Path $repositoryRoot 'packaging/nsis/HeyMelody.nsi')
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $installerPath -PathType Leaf)) { throw "NSIS compilation failed: $architecture (work files retained at $work)" }
    $receipt = [ordered]@{
        schemaVersion = 1; version = $version; architecture = $architecture
        createdAtUtc = [DateTime]::UtcNow.ToString('o'); compiler = $compilerVersion
        packageManifestSha256 = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
        installerSha256 = (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash.ToLowerInvariant()
        scope = 'current-user'; defaultDirectory = '%LOCALAPPDATA%\Programs\HeyMelody'
        compatibilityProbeCommand = '/S /CHECKCOMPATIBILITY'
        compatibilityHardwareId = 'ACPI\VEN_QCOM&DEV_043A'; payloadFiles = $relativeFiles.ToArray()
        validationScope = 'packaged only; no hardware or installer execution implied'
    }
    $receipt | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath "$installerPath.json" -Encoding utf8
    Write-Host "Created $installerPath ($($receipt.installerSha256)); work files: $work"
}
