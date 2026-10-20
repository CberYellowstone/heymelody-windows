# Shared, non-downloading Windows tool selection for native builds and artifact identities.
# Keep paths outside Identity: runner installation locations do not affect cache reuse.
function Get-FilamentPythonIdentity {
    param([Parameter(Mandatory)][string]$Executable)
    $probe = & $Executable -c 'import json, platform, struct; print(json.dumps({"implementation": platform.python_implementation(), "version": platform.python_version(), "pointerBits": struct.calcsize("P") * 8}))' 2>&1
    if ($LASTEXITCODE -ne 0) { throw 'The selected Python executable could not report its version.' }
    try { $details = ($probe -join "`n") | ConvertFrom-Json -ErrorAction Stop }
    catch { throw 'The selected Python executable did not return a valid tool identity.' }
    if ([string]$details.version -notmatch '^3\.[0-9]+\.[0-9]+$') { throw 'Python 3 is required to build Filament.' }
    return [ordered]@{ implementation = [string]$details.implementation; version = [string]$details.version; pointerBits = [int]$details.pointerBits }
}

function Test-FilamentToolchainIdentity {
    param([Parameter(Mandatory)]$Actual, [Parameter(Mandatory)]$Expected)
    $actualNode = [System.Text.Json.Nodes.JsonNode]::Parse(($Actual | ConvertTo-Json -Depth 12 -Compress))
    $expectedNode = [System.Text.Json.Nodes.JsonNode]::Parse(($Expected | ConvertTo-Json -Depth 12 -Compress))
    return [System.Text.Json.Nodes.JsonNode]::DeepEquals($actualNode, $expectedNode)
}

function Enter-FilamentWindowsDevShell {
    param(
        [Parameter(Mandatory)]$Toolchain,
        [ValidateSet('x64','arm64')][string]$Architecture = 'x64'
    )
    Import-Module (Join-Path $Toolchain.VisualStudioPath 'Common7/Tools/Microsoft.VisualStudio.DevShell.dll') -ErrorAction Stop
    $installer = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer'
    $env:PATH = $installer + [IO.Path]::PathSeparator + $env:PATH
    $expectedCompiler = if ($Architecture -eq 'arm64') { $Toolchain.TargetCompiler } else { $Toolchain.HostCompiler }
    $devArguments = "-arch=$Architecture -host_arch=x64 -vcvars_ver=$($Toolchain.MsvcVersion) -winsdk=$($Toolchain.WindowsSdkVersion)"
    Enter-VsDevShell -VsInstallPath $Toolchain.VisualStudioPath -SkipAutomaticLocation -DevCmdArguments $devArguments | Out-Null
    $selectedSdk = ([string]$env:WindowsSDKVersion).Trim().TrimEnd('\','/')
    if (([string]$env:VCToolsVersion).Trim() -ne $Toolchain.MsvcVersion -or $selectedSdk -ne $Toolchain.WindowsSdkVersion) {
        throw 'Visual Studio selected a different MSVC toolset or Windows SDK than the native build identity.'
    }
    $selectedCompiler = (Get-Command cl.exe -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    if (-not [string]::Equals([IO.Path]::GetFullPath($selectedCompiler), [IO.Path]::GetFullPath($expectedCompiler), [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Visual Studio selected a compiler that does not match the native build identity.'
    }
}

function Get-FilamentWindowsToolchain {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][ValidateSet('win-x64','win-arm64')][string]$RuntimeIdentifier,
        [Parameter(Mandatory)][string]$OutputRoot,
        [string]$PythonExecutable
    )
    if (-not [Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Windows)) {
        throw 'Windows native toolchain discovery requires a Windows host.'
    }
    $savedEnvironment = [Environment]::GetEnvironmentVariables()
    try {
        $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
        if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio C++ Build Tools are required.' }
        $installationJson = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 Microsoft.VisualStudio.Component.VC.CMake.Project -format json
        if ($LASTEXITCODE -ne 0) { throw 'Visual Studio toolchain discovery failed.' }
        $installations = @(($installationJson -join "`n") | ConvertFrom-Json)
        if ($installations.Count -ne 1) { throw 'Install Visual Studio C++ and CMake components.' }
        $installation = $installations[0]
        $vs = [string]$installation.installationPath
        Import-Module (Join-Path $vs 'Common7/Tools/Microsoft.VisualStudio.DevShell.dll') -ErrorAction Stop
        $env:PATH = (Split-Path -Parent $vswhere) + [IO.Path]::PathSeparator + $env:PATH
        Enter-VsDevShell -VsInstallPath $vs -SkipAutomaticLocation -DevCmdArguments '-arch=x64 -host_arch=x64' | Out-Null
        $msvc = ([string]$env:VCToolsVersion).Trim()
        $sdk = ([string]$env:WindowsSDKVersion).Trim().TrimEnd('\','/')
        if ($msvc -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$' -or $sdk -notmatch '^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$') {
            throw 'Visual Studio did not select an identifiable MSVC toolset and Windows SDK.'
        }
        $targetArchitecture = if ($RuntimeIdentifier -eq 'win-arm64') { 'arm64' } else { 'x64' }
        $compilerRoot = Join-Path $vs "VC/Tools/MSVC/$msvc/bin/Hostx64"
        $hostCompiler = Join-Path $compilerRoot 'x64/cl.exe'
        $targetCompiler = Join-Path $compilerRoot "$targetArchitecture/cl.exe"
        $cmake = Join-Path $vs 'Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe'
        $ninja = Join-Path $vs 'Common7/IDE/CommonExtensions/Microsoft/CMake/Ninja/ninja.exe'
        foreach ($tool in @($hostCompiler,$targetCompiler,$cmake,$ninja)) {
            if (-not (Test-Path -LiteralPath $tool -PathType Leaf)) { throw "Missing required Windows native build tool: $tool" }
        }
        $sdkRoot = [string]$env:WindowsSdkDir
        foreach ($sdkFile in @("Include/$sdk/um/Windows.h", "Lib/$sdk/um/$targetArchitecture/kernel32.lib", "Lib/$sdk/ucrt/$targetArchitecture/ucrt.lib")) {
            if (-not (Test-Path -LiteralPath (Join-Path $sdkRoot $sdkFile) -PathType Leaf)) { throw "The selected Windows SDK is incomplete for $targetArchitecture." }
        }
        $cmakeOutput = (& $cmake --version 2>&1) -join "`n"
        if ($LASTEXITCODE -ne 0 -or $cmakeOutput -notmatch '(?m)^cmake version ([0-9]+(?:\.[0-9]+)+(?:[-+][0-9A-Za-z.-]+)?)\s*$') { throw 'Could not identify the Visual Studio CMake version.' }
        $cmakeVersion = $Matches[1]
        $ninjaOutput = ((& $ninja --version 2>&1) -join "`n").Trim()
        if ($LASTEXITCODE -ne 0 -or $ninjaOutput -notmatch '^[0-9]+(?:\.[0-9]+)+(?:[-+][0-9A-Za-z.-]+)?$') { throw 'Could not identify the Visual Studio Ninja version.' }

        $lock = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'filament.lock.json') -Raw | ConvertFrom-Json
        $pythonIdentity = $null
        if ($PythonExecutable) {
            $PythonExecutable = (Resolve-Path -LiteralPath $PythonExecutable -ErrorAction Stop).Path
            $pythonIdentity = Get-FilamentPythonIdentity -Executable $PythonExecutable
        } else {
            # Prefer installed tools consistently, even if a previous cold build downloaded Python.
            foreach ($command in @('python3','python')) {
                $installedPython = Get-Command $command -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
                if (-not $installedPython -or $installedPython.Source -like '*WindowsApps*') { continue }
                try {
                    $pythonIdentity = Get-FilamentPythonIdentity -Executable $installedPython.Source
                    $PythonExecutable = $installedPython.Source
                    break
                } catch { $pythonIdentity = $null }
            }
            if (-not $PythonExecutable) {
                $embeddedPython = Join-Path ([IO.Path]::GetFullPath($OutputRoot)) "tools/python-$($lock.windowsPython.version)/python.exe"
                if (Test-Path -LiteralPath $embeddedPython -PathType Leaf) {
                    $PythonExecutable = $embeddedPython
                    $pythonIdentity = Get-FilamentPythonIdentity -Executable $PythonExecutable
                } else {
                    $pythonIdentity = [ordered]@{ implementation = 'CPython'; version = [string]$lock.windowsPython.version; pointerBits = 64 }
                }
            }
        }
        $identity = [ordered]@{
            schemaVersion = 1
            rid = $RuntimeIdentifier
            hostToolsArchitecture = 'x64'
            visualStudioVersion = [string]$installation.installationVersion
            msvcToolsetVersion = $msvc
            hostCompilerVersion = [string](Get-Item -LiteralPath $hostCompiler).VersionInfo.FileVersion
            targetCompilerVersion = [string](Get-Item -LiteralPath $targetCompiler).VersionInfo.FileVersion
            windowsSdkVersion = $sdk
            cmakeVersion = $cmakeVersion
            ninjaVersion = $ninjaOutput
            python = $pythonIdentity
        }
        return [pscustomobject]@{
            Identity = $identity; VisualStudioPath = $vs; MsvcVersion = $msvc; WindowsSdkVersion = $sdk
            CMakeExecutable = $cmake; NinjaExecutable = $ninja; PythonExecutable = $PythonExecutable
            HostCompiler = $hostCompiler; TargetCompiler = $targetCompiler
        }
    } finally {
        foreach ($key in @([Environment]::GetEnvironmentVariables().Keys)) {
            if (-not $savedEnvironment.Contains($key)) { [Environment]::SetEnvironmentVariable([string]$key, [NullString]::Value, 'Process') }
        }
        foreach ($key in $savedEnvironment.Keys) { [Environment]::SetEnvironmentVariable([string]$key, [string]$savedEnvironment[$key], 'Process') }
    }
}
