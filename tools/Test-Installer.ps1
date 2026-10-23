#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$InstallerPath,
    [string]$TestRoot,
    [string]$HelperPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Installer execution checks require Windows.' }

$repositoryRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$artifactsRoot = Join-Path $repositoryRoot 'artifacts'
$uninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\HeyMelody_is1'
$legacyUninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\HeyMelody'
$startMenuPrograms = [Environment]::GetFolderPath('Programs')
$startMenuShortcut = Join-Path $startMenuPrograms 'HeyMelody.lnk'
$legacyStartMenuDirectory = Join-Path $startMenuPrograms 'HeyMelody'
$preferencesPath = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'HeyMelody/preferences.json'
$installerPreferencesPath = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'HeyMelody/installer-preferences.json'
$desktopShortcutPath = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'HeyMelody.lnk'
$checks = [Collections.Generic.List[object]]::new()
$runs = [Collections.Generic.List[object]]::new()

function Assert-Condition([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Test-SamePath([string]$First, [string]$Second) {
    if ([string]::IsNullOrWhiteSpace($First) -or [string]::IsNullOrWhiteSpace($Second)) { return $false }
    return [IO.Path]::GetFullPath($First).TrimEnd('\').Equals(
        [IO.Path]::GetFullPath($Second).TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)
}

function Assert-ChildPath([string]$Path, [string]$Parent) {
    $prefix = [IO.Path]::GetFullPath($Parent).TrimEnd('\') + '\'
    Assert-Condition ([IO.Path]::GetFullPath($Path).StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) "Path must stay below $Parent : $Path"
}

function Read-UninstallRegistration([string]$KeyPath = $uninstallKey) {
    $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
        [Microsoft.Win32.RegistryHive]::CurrentUser, [Microsoft.Win32.RegistryView]::Registry32)
    try {
        $key = $base.OpenSubKey($KeyPath, $false)
        if ($null -eq $key) { return $null }
        try {
            $values = [ordered]@{}
            foreach ($name in $key.GetValueNames()) { $values[$name] = $key.GetValue($name) }
            return $values
        }
        finally { $key.Dispose() }
    }
    finally { $base.Dispose() }
}

function Get-PreferenceSnapshot([string]$Path = $preferencesPath) {
    if (-not (Test-Path -LiteralPath $Path)) {
        return [pscustomobject]@{ exists = $false; length = 0; sha256 = $null }
    }
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $hasher = [Security.Cryptography.SHA256]::Create()
    try {
        $length = $stream.Length
        $sha = $hasher.ComputeHash($stream)
        return [pscustomobject]@{ exists = $true; length = $length; sha256 = [BitConverter]::ToString($sha).Replace('-', '').ToLowerInvariant() }
    }
    finally { $hasher.Dispose(); $stream.Dispose() }
}

function Assert-PreferencesUnchanged([string]$Stage) {
    $current = Get-PreferenceSnapshot
    Assert-Condition ($current.exists -eq $preferencesBefore.exists -and
        $current.length -eq $preferencesBefore.length -and $current.sha256 -ceq $preferencesBefore.sha256) (
        "Preferences changed during $Stage. The original file has not been restored automatically; inspect preferences.json and results.json.")
    $installerCurrent = Get-PreferenceSnapshot $installerPreferencesPath
    Assert-Condition ($installerCurrent.exists -eq $installerPreferencesBefore.exists -and
        $installerCurrent.length -eq $installerPreferencesBefore.length -and $installerCurrent.sha256 -ceq $installerPreferencesBefore.sha256) (
        "Installer preferences changed during $Stage. The original file has not been restored automatically; inspect installer-preferences.json and results.json.")
}

function Read-DesktopPreference {
    if (-not (Test-Path -LiteralPath $installerPreferencesPath)) { return $true }
    $options = [System.Text.Json.JsonDocumentOptions]::new()
    $options.MaxDepth = 256
    $text = [IO.File]::ReadAllText($installerPreferencesPath, [Text.UTF8Encoding]::new($false, $true))
    $document = [System.Text.Json.JsonDocument]::Parse($text, $options)
    try {
        Assert-Condition ($document.RootElement.ValueKind -eq [System.Text.Json.JsonValueKind]::Object) 'Existing installer preferences must have an object root; they were left untouched.'
        $choice = [System.Text.Json.JsonElement]::new()
        if (-not $document.RootElement.TryGetProperty('desktopShortcut', [ref]$choice)) { return $true }
        if ($choice.ValueKind -eq [System.Text.Json.JsonValueKind]::True) { return $true }
        Assert-Condition ($choice.ValueKind -eq [System.Text.Json.JsonValueKind]::False) 'Existing desktopShortcut must be a JSON boolean; it was left untouched.'
        return $false
    }
    finally { $document.Dispose() }
}

function Add-Check([string]$Name) {
    $checks.Add([ordered]@{ name = $Name; passed = $true; atUtc = [DateTime]::UtcNow.ToString('o') })
    Write-Host "PASS: $Name"
}

function Wait-ForRemoval([string[]]$Paths) {
    foreach ($path in $Paths) { Assert-ChildPath $path $TestRoot }
    $timer = [Diagnostics.Stopwatch]::StartNew()
    do {
        $remaining = @($Paths | Where-Object { Test-Path -LiteralPath $_ })
        if ($remaining.Count -eq 0) { return }
        if ($timer.Elapsed.TotalSeconds -ge 10) { break }
        Start-Sleep -Milliseconds 100
    } while ($true)
    $sample = @($remaining | Select-Object -First 3) -join ', '
    Assert-Condition $false "Uninstaller cleanup did not finish within 10 seconds ($($remaining.Count) paths remain): $sample"
}

function Invoke-SilentProcess([string]$Executable, [string]$Arguments, [string]$LogName) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $Executable
    # Inno Setup accepts quoted /DIR and /LOG values containing spaces.
    $start.Arguments = $Arguments
    $start.WorkingDirectory = $TestRoot
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        Assert-Condition ($process.Start()) "Could not start $Executable"
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $deadline = [DateTime]::UtcNow.AddMinutes(10)
        while (-not $process.WaitForExit(1000)) {
            if ([DateTime]::UtcNow -gt $deadline) {
                throw "Timed out waiting for installer process $($process.Id). It was left running; no process was terminated."
            }
        }
        [IO.File]::WriteAllText((Join-Path $TestRoot "$LogName.stdout.txt"), $stdout.GetAwaiter().GetResult())
        [IO.File]::WriteAllText((Join-Path $TestRoot "$LogName.stderr.txt"), $stderr.GetAwaiter().GetResult())
        $runs.Add([ordered]@{ name = $LogName; exitCode = $process.ExitCode })
        return $process.ExitCode
    }
    finally { $process.Dispose() }
}

function Assert-Installed {
    foreach ($relative in $payloadFiles) {
        Assert-Condition (Test-Path -LiteralPath (Join-Path $installDirectory $relative) -PathType Leaf) "Missing installed payload: $relative"
    }
    Assert-Condition (Test-Path -LiteralPath $uninstallerPath -PathType Leaf) 'The generated uninstaller is missing.'
    Assert-Condition (Test-Path -LiteralPath $uninstallDataPath -PathType Leaf) 'The Inno uninstall log is missing.'
    $registration = Read-UninstallRegistration
    Assert-Condition ($null -ne $registration) 'The HKCU 32-bit uninstall registration is missing.'
    Assert-Condition (Test-SamePath ([string]$registration['InstallLocation']) $installDirectory) 'InstallLocation differs from the custom installation directory.'
    Assert-Condition ($registration['DisplayVersion'] -ceq [string]$receipt.version) 'The registered version does not match the installer receipt.'
    Assert-Condition ($registration['UninstallString'] -ceq ('"' + $uninstallerPath + '"')) 'UninstallString does not name the generated uninstaller.'
    $quietUninstall = [string]$registration['QuietUninstallString']
    Assert-Condition ($quietUninstall.StartsWith(('"' + $uninstallerPath + '" '), [StringComparison]::OrdinalIgnoreCase) -and
        $quietUninstall -match '(?i)(?:^|\s)/(?:VERYSILENT|SILENT)(?:\s|$)') 'QuietUninstallString does not name the Inno uninstaller with a silent switch.'
    Assert-Condition ($registration['NoModify'] -eq 1 -and $registration['NoRepair'] -eq 1) 'Uninstall registration exposes unsupported modify/repair operations.'

    Assert-Condition (-not (Test-Path -LiteralPath $legacyStartMenuDirectory)) 'Installation created a legacy HeyMelody Start menu directory.'
    Assert-Condition (Test-Path -LiteralPath $startMenuShortcut -PathType Leaf) 'The single current-user HeyMelody Start menu shortcut is missing.'
    $shortcutPaths = @($startMenuShortcut)
    if ($desktopShortcutExpected) {
        Assert-Condition (Test-Path -LiteralPath $desktopShortcutPath -PathType Leaf) 'The saved/default desktop shortcut choice was not applied.'
        $shortcutPaths += $desktopShortcutPath
    }
    else {
        Assert-Condition (-not (Test-Path -LiteralPath $desktopShortcutPath)) 'Silent installation created a desktop shortcut despite the saved false choice.'
    }
    $shell = New-Object -ComObject WScript.Shell
    try {
        foreach ($path in $shortcutPaths) {
            $shortcut = $shell.CreateShortcut($path)
            try {
                Assert-Condition (Test-SamePath $shortcut.TargetPath (Join-Path $installDirectory "$architecture\HeyMelody.exe")) "Shortcut does not target the main application: $path"
                Assert-Condition ($shortcut.Arguments -ceq '') "Shortcut has unexpected arguments: $path"
                Assert-Condition (Test-Path -LiteralPath $shortcut.TargetPath -PathType Leaf) "Shortcut target does not exist: $path"
                Assert-Condition (Test-SamePath $shortcut.WorkingDirectory (Join-Path $installDirectory $architecture)) "Shortcut has an incorrect working directory: $path"
                Assert-Condition (Test-Path -LiteralPath $shortcut.WorkingDirectory -PathType Container) "Shortcut working directory does not exist: $path"
            }
            finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut) }
        }
    }
    finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
}

# Stop before any writes if this user's existing installation could be affected.
Assert-Condition ($null -eq (Read-UninstallRegistration)) 'An existing HKCU HeyMelody installation is registered. Leave it untouched and use a clean test account.'
Assert-Condition ($null -eq (Read-UninstallRegistration $legacyUninstallKey)) 'An existing legacy NSIS installation is registered; it was left untouched.'
Assert-Condition (-not (Test-Path -LiteralPath $startMenuShortcut)) 'The current-user HeyMelody Start menu shortcut already exists; it was left untouched.'
Assert-Condition (-not (Test-Path -LiteralPath $legacyStartMenuDirectory)) 'The legacy current-user HeyMelody Start menu directory already exists; it was left untouched.'
Assert-Condition (-not (Test-Path -LiteralPath $desktopShortcutPath)) 'The current-user HeyMelody desktop shortcut already exists; it was left untouched.'
$InstallerPath = [IO.Path]::GetFullPath($InstallerPath, $repositoryRoot)
Assert-ChildPath $InstallerPath $repositoryRoot
Assert-Condition (Test-Path -LiteralPath $InstallerPath -PathType Leaf) 'InstallerPath must name an existing generated installer.'
$receiptPath = "$InstallerPath.json"
Assert-Condition (Test-Path -LiteralPath $receiptPath -PathType Leaf) 'The corresponding .exe.json installer receipt is required.'
$receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
Assert-Condition ($receipt.schemaVersion -eq 2 -and $receipt.installerFormat -ceq 'inno-setup' -and $receipt.scope -ceq 'current-user') 'Expected a schema-2 current-user Inno Setup receipt.'
if ([string]::IsNullOrWhiteSpace($HelperPath)) { $HelperPath = [string]$receipt.helperPath }
Assert-Condition (-not [string]::IsNullOrWhiteSpace($HelperPath)) 'The receipt must identify the actual installer helper, or provide -HelperPath.'
$HelperPath = [IO.Path]::GetFullPath($HelperPath, $repositoryRoot)
Assert-ChildPath $HelperPath $artifactsRoot
Assert-Condition (Test-Path -LiteralPath $HelperPath -PathType Leaf) 'The retained installer helper is missing.'
$helperSha256 = (Get-FileHash -LiteralPath $HelperPath -Algorithm SHA256).Hash.ToLowerInvariant()
Assert-Condition ($helperSha256 -ceq $receipt.helperSha256) 'The actual installer helper SHA256 does not match its receipt.'
$architecture = [string]$receipt.architecture
Assert-Condition ($architecture -in @('x64', 'arm64')) 'The receipt architecture must be x64 or arm64.'
$installerSha256 = (Get-FileHash -LiteralPath $InstallerPath -Algorithm SHA256).Hash.ToLowerInvariant()
Assert-Condition ($installerSha256 -ceq $receipt.installerSha256) 'The installer SHA256 does not match its receipt.'
$payloadFiles = @($receipt.payloadFiles)
Assert-Condition ($payloadFiles.Count -gt 0) 'The installer receipt contains no payload files.'
if ([string]::IsNullOrWhiteSpace($TestRoot)) { $TestRoot = Join-Path $artifactsRoot ('installer-execution-' + [Guid]::NewGuid().ToString('N')) }
$TestRoot = [IO.Path]::GetFullPath($TestRoot, $repositoryRoot)
Assert-ChildPath $TestRoot $artifactsRoot
Assert-Condition (-not (Test-Path -LiteralPath $TestRoot)) 'TestRoot must be a new directory; existing test output is never reused.'
for ($ancestor = $TestRoot; $ancestor -and -not (Test-SamePath $ancestor $repositoryRoot); $ancestor = Split-Path -Parent $ancestor) {
    if (Test-Path -LiteralPath $ancestor) {
        Assert-Condition (((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) "TestRoot has a reparse-point ancestor: $ancestor"
    }
}
$installDirectory = Join-Path $TestRoot '自定义 安装'
$uninstallerPath = Join-Path $installDirectory 'unins000.exe'
$uninstallDataPath = Join-Path $installDirectory 'unins000.dat'
$silentArguments = '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-'
$uninstallArguments = '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART'
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($relative in $payloadFiles) {
    Assert-Condition ($relative -is [string] -and -not [string]::IsNullOrWhiteSpace($relative)) 'Every payload path must be a nonempty string.'
    Assert-Condition (-not [IO.Path]::IsPathRooted($relative) -and $relative -notmatch '(^|[\\/])\.{1,2}([\\/]|$)|[:\r\n]') "Unsafe payload path in receipt: $relative"
    Assert-ChildPath (Join-Path $installDirectory $relative) $installDirectory
    Assert-Condition ($seen.Add($relative)) "Duplicate payload path: $relative"
}
$lockedRelative = "$architecture\coreclr.dll"
$removedRuntimeRelative = "$architecture\hostfxr.dll"
Assert-Condition ($seen.Contains($lockedRelative) -and $seen.Contains($removedRuntimeRelative)) 'The receipt lacks the .NET runtime files required for the interrupted-uninstall check.'
$extraUserFile = Join-Path $installDirectory '用户自留 文件.txt'
Assert-Condition (-not $seen.Contains([IO.Path]::GetFileName($extraUserFile))) 'The test user-file name collides with a payload file.'
$preferencesBefore = Get-PreferenceSnapshot
$installerPreferencesBefore = Get-PreferenceSnapshot $installerPreferencesPath
$desktopShortcutExpected = Read-DesktopPreference
[void][IO.Directory]::CreateDirectory($TestRoot)
$resultPath = Join-Path $TestRoot 'results.json'
$succeeded = $false
$failure = $null
$lock = $null

try {
    Add-Check 'Clean user installation state and verified installer receipt'
    $exitCode = Invoke-SilentProcess $HelperPath 'probe-compatibility' '00-compatibility-probe'
    Assert-PreferencesUnchanged 'read-only hardware probe'
    Assert-Condition ($exitCode -ne 20) 'This test requires non-QCOM043A hardware; the read-only probe detected an affected GPU. No installation was attempted.'
    Assert-Condition ($exitCode -eq 0) "Read-only hardware probe failed with $exitCode. No installation was attempted."
    Add-Check 'Read-only hardware probe confirms preferences will not be modified on this machine'
    $exitCode = Invoke-SilentProcess $InstallerPath ($silentArguments + ' /DIR="' + $installDirectory + '" /LOG="' + (Join-Path $TestRoot '01-install.inno.log') + '"') '01-install'
    Assert-PreferencesUnchanged 'initial installation'
    Assert-Condition ($exitCode -eq 0) "Initial installation exited with $exitCode."
    Assert-Installed
    Add-Check 'Silent installation into a custom directory containing spaces and Chinese characters'

    [IO.File]::WriteAllText($extraUserFile, 'User-owned test file: ' + [Guid]::NewGuid().ToString('N'), [Text.UTF8Encoding]::new($false))
    $userFileSha256 = (Get-FileHash -LiteralPath $extraUserFile -Algorithm SHA256).Hash
    $exitCode = Invoke-SilentProcess $InstallerPath ($silentArguments + ' /DIR="' + $installDirectory + '" /LOG="' + (Join-Path $TestRoot '02-upgrade.inno.log') + '"') '02-upgrade'
    Assert-PreferencesUnchanged 'upgrade'
    Assert-Condition ($exitCode -eq 0) "Upgrade exited with $exitCode."
    Assert-Installed
    Assert-Condition ((Get-FileHash -LiteralPath $extraUserFile -Algorithm SHA256).Hash -ceq $userFileSha256) 'Upgrade changed or removed the user-owned file.'
    Add-Check 'Upgrade preserves user-owned files, preferences, registration and the single Start menu shortcut'

    # Inno's original uninstaller needs its adjacent .dat log; do not copy it away.
    $lock = [IO.File]::Open((Join-Path $installDirectory $lockedRelative), [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
    try {
        $exitCode = Invoke-SilentProcess $uninstallerPath ($uninstallArguments + ' /LOG="' + (Join-Path $TestRoot '03-uninstall-locked.inno.log') + '"') '03-uninstall-locked'
    }
    finally { $lock.Dispose(); $lock = $null }
    Assert-PreferencesUnchanged 'interrupted uninstall'
    Assert-Condition ($exitCode -ne 0) 'Uninstall unexpectedly succeeded while a payload file was exclusively locked.'
    Assert-Condition ($null -ne (Read-UninstallRegistration)) 'A failed uninstall removed the uninstall registration.'
    Assert-Condition (Test-Path -LiteralPath (Join-Path $installDirectory $lockedRelative) -PathType Leaf) 'The exclusively locked runtime file disappeared.'
    Assert-Condition (Test-Path -LiteralPath (Join-Path $installDirectory $removedRuntimeRelative) -PathType Leaf) 'The failed preflight deleted hostfxr.dll.'
    Assert-Installed
    Assert-Condition ((Get-FileHash -LiteralPath $extraUserFile -Algorithm SHA256).Hash -ceq $userFileSha256) 'A failed uninstall changed or removed the user-owned file.'
    Add-Check 'Locked-file preflight rejects uninstall before deleting payload, registration or user files'

    $removedRuntimePath = [IO.Path]::GetFullPath((Join-Path $installDirectory $removedRuntimeRelative))
    Assert-ChildPath $removedRuntimePath $TestRoot
    Remove-Item -LiteralPath $removedRuntimePath
    Assert-Condition (-not (Test-Path -LiteralPath $removedRuntimePath)) 'The controlled partial-runtime fixture was not created.'
    $exitCode = Invoke-SilentProcess $uninstallerPath ($uninstallArguments + ' /LOG="' + (Join-Path $TestRoot '04-uninstall-retry.inno.log') + '"') '04-uninstall-retry'
    Assert-PreferencesUnchanged 'uninstall retry'
    Assert-Condition ($exitCode -eq 0) "Uninstall retry exited with $exitCode."
    Wait-ForRemoval @($uninstallerPath, $uninstallDataPath, (Join-Path $installDirectory '.installer'))
    foreach ($relative in $payloadFiles) {
        Assert-Condition (-not (Test-Path -LiteralPath (Join-Path $installDirectory $relative))) "Uninstall left a shipped payload file: $relative"
    }
    Assert-Condition (-not (Test-Path -LiteralPath $uninstallerPath)) 'Uninstall left the installed uninstaller behind.'
    Assert-Condition (-not (Test-Path -LiteralPath $uninstallDataPath)) 'Uninstall left its .dat log behind.'
    Assert-Condition (-not (Test-Path -LiteralPath (Join-Path $installDirectory '.installer'))) 'Successful uninstall left its private support directory behind.'
    Assert-Condition ($null -eq (Read-UninstallRegistration)) 'Uninstall left the HKCU registration behind.'
    Assert-Condition (-not (Test-Path -LiteralPath $startMenuShortcut)) 'Uninstall left the current-user Start menu shortcut behind.'
    Assert-Condition (-not (Test-Path -LiteralPath $legacyStartMenuDirectory)) 'Uninstall left a legacy HeyMelody Start menu directory behind.'
    Assert-Condition (-not (Test-Path -LiteralPath $desktopShortcutPath)) 'Uninstall left its desktop shortcut behind.'
    Assert-Condition ((Get-FileHash -LiteralPath $extraUserFile -Algorithm SHA256).Hash -ceq $userFileSha256) 'Uninstall changed or removed the user-owned file.'
    Add-Check 'Independent Inno uninstall succeeds after controlled runtime removal and preserves the user-owned file'
    $succeeded = $true
}
catch {
    $failure = $_.Exception.Message
    throw
}
finally {
    if ($null -ne $lock) { $lock.Dispose() }
    $preferencesAfter = $null
    try { $preferencesAfter = Get-PreferenceSnapshot } catch { $preferencesAfter = [ordered]@{ error = $_.Exception.Message } }
    $installerPreferencesAfter = $null
    try { $installerPreferencesAfter = Get-PreferenceSnapshot $installerPreferencesPath } catch { $installerPreferencesAfter = [ordered]@{ error = $_.Exception.Message } }
    [ordered]@{
        schemaVersion = 1; success = $succeeded; createdAtUtc = [DateTime]::UtcNow.ToString('o')
        installer = $InstallerPath; installerSha256 = $installerSha256
        installerFormat = $receipt.installerFormat; helper = $HelperPath; helperSha256 = $helperSha256
        version = $receipt.version; architecture = $architecture; installationDirectory = $installDirectory
        payloadFileCount = $payloadFiles.Count; preferencesBefore = $preferencesBefore; preferencesAfter = $preferencesAfter
        installerPreferencesBefore = $installerPreferencesBefore; installerPreferencesAfter = $installerPreferencesAfter
        desktopShortcutExpected = $desktopShortcutExpected
        compatibilityScope = 'Real local hardware only; preferences must remain byte-identical. No hardware simulation or automatic restoration.'
        validationScope = 'Silent installer execution only; no application, UI screenshot, Bluetooth, headphone or hardware rendering validation.'
        checks = $checks.ToArray(); runs = $runs.ToArray(); failure = $failure
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $resultPath -Encoding utf8
    Write-Host "Installer test output retained at $TestRoot"
}
