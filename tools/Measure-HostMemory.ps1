<#
.SYNOPSIS
Reads memory counters of the running HeyMelody Host tray, its workers and the UI processes (Windows only).
.DESCRIPTION
Read-only. Finds HeyMelody.Host.exe processes of the current Windows session through
Win32_Process and tells them apart by the mode flag on the command line (tray,
--worker, --catalog-worker); HeyMelody.exe and the other UI executables are reported
as app roles. Only the mode flag is used: command lines, paths and worker input
(which is sent over stdin) are never printed or saved. Counters come from the
non-localized Win32_PerfFormattedData_PerfProc_Process class: WorkingSetPrivate
(about the Task Manager Details "Memory" column), WorkingSet, PrivateBytes, thread
and handle counts. Task Manager's Processes tab may group child processes under the
Host app, so compare per-process rows.
This script does not start, stop or signal any process, does not contact a headset,
and does not read or write HeyMelody settings or caches.
The optional JSON file holds the Windows version, CPU name/core count/L3 size, the
Host file version and the samples. It is created only when the destination is absent.
Supports Windows PowerShell 5.1 and PowerShell 7 (run with -NoProfile).
.EXAMPLE
pwsh -NoProfile -File tools/Measure-HostMemory.ps1 -Samples 12 -IntervalSeconds 5 -OutputPath artifacts/host-memory-before.json
#>
[CmdletBinding()]
param(
    [ValidateRange(1, 1000)][int]$Samples = 1,
    [ValidateRange(1, 3600)][int]$IntervalSeconds = 5,
    [string]$OutputPath
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Get-ProcessRole([string]$Name, [string]$CommandLine) {
    $isHost = $Name -ieq 'HeyMelody.Host.exe' -or
        ($Name -match '^dotnet(\.exe)?$' -and $CommandLine -and $CommandLine -match 'HeyMelody\.Host\.dll')
    if ($isHost) {
        if (-not $CommandLine) { return 'host-unknown' }
        if ($CommandLine -match '(?<![\w-])--catalog-worker(?![\w-])') { return 'catalog-worker' }
        if ($CommandLine -match '(?<![\w-])--worker(?![\w-])') { return 'worker' }
        return 'tray'
    }
    if ($Name -ieq 'HeyMelody.exe') { return 'app' }
    if ($Name -match '^dotnet(\.exe)?$' -and $CommandLine -and $CommandLine -match '[\\/]HeyMelody\.dll') { return 'app' }
    if ($Name -match '^HeyMelody\.(DeviceManager|FitTest|HearingSetup)\.exe$') { return 'app-window' }
    return $null
}

function Get-RoleOrder([string]$Role) {
    switch ($Role) { 'tray' { 0 } 'worker' { 1 } 'catalog-worker' { 2 } 'host-unknown' { 3 } 'app' { 4 } default { 5 } }
}

function ConvertTo-Mib($Bytes) {
    if ($null -eq $Bytes) { return $null }
    [math]::Round(([double]$Bytes) / 1MB, 1)
}

function Get-Median([double[]]$Values) {
    $sorted = @($Values | Sort-Object)
    if ($sorted.Count -eq 0) { return $null }
    $mid = [int][math]::Floor($sorted.Count / 2)
    if ($sorted.Count % 2 -eq 1) { return $sorted[$mid] }
    ($sorted[$mid - 1] + $sorted[$mid]) / 2
}

function Get-HostFileInfo($Process) {
    # Returns only version strings; the path stays inside this function.
    try {
        $path = $null
        if ($Process.Name -ieq 'HeyMelody.Host.exe') { $path = $Process.ExecutablePath }
        elseif ($Process.CommandLine -match '"([^"]*HeyMelody\.Host\.dll)"') { $path = $Matches[1] }
        elseif ($Process.CommandLine -match '(\S*HeyMelody\.Host\.dll)') { $path = $Matches[1] }
        if (-not $path -or -not (Test-Path -LiteralPath $path -PathType Leaf)) { return $null }
        $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($path)
        [ordered]@{ fileVersion = $info.FileVersion; productVersion = $info.ProductVersion }
    }
    catch { $null }
}

function Get-MemorySample([int]$SessionId) {
    $all = @(Get-CimInstance -ClassName Win32_Process)
    $selected = @(foreach ($process in $all) {
        if ([int]$process.SessionId -ne $SessionId) { continue }
        $role = Get-ProcessRole $process.Name $process.CommandLine
        if ($role) { [pscustomobject]@{ Role = $role; Process = $process } }
    })
    $perf = @{}
    if ($selected.Count -gt 0) {
        $filter = (@($selected | ForEach-Object { 'IDProcess=' + [int]$_.Process.ProcessId }) -join ' OR ')
        foreach ($counter in @(Get-CimInstance -ClassName Win32_PerfFormattedData_PerfProc_Process -Filter $filter)) {
            $perf[[int]$counter.IDProcess] = $counter
        }
    }
    $rows = @(foreach ($item in ($selected | Sort-Object { Get-RoleOrder $_.Role }, { [int]$_.Process.ProcessId })) {
        $counter = $perf[[int]$item.Process.ProcessId]
        $row = [ordered]@{
            role = $item.Role; pid = [int]$item.Process.ProcessId; parentPid = [int]$item.Process.ParentProcessId
            workingSetPrivateBytes = $null; workingSetBytes = $null; privateBytes = $null; threads = $null; handles = $null
        }
        if ($counter) {
            $row.workingSetPrivateBytes = [long]$counter.WorkingSetPrivate
            $row.workingSetBytes = [long]$counter.WorkingSet
            $row.privateBytes = [long]$counter.PrivateBytes
            $row.threads = [int]$counter.ThreadCount
            $row.handles = [int]$counter.HandleCount
        }
        [pscustomobject]$row
    })
    $roles = @($rows | ForEach-Object { $_.role } | Select-Object -Unique)
    $byRole = [ordered]@{}
    foreach ($role in $roles) {
        $group = @($rows | Where-Object { $_.role -eq $role })
        $byRole[$role] = [ordered]@{
            count = $group.Count
            workingSetPrivateBytes = [long](($group | Where-Object { $null -ne $_.workingSetPrivateBytes } | Measure-Object workingSetPrivateBytes -Sum).Sum)
        }
    }
    $total = [long](($rows | Where-Object { $null -ne $_.workingSetPrivateBytes } | Measure-Object workingSetPrivateBytes -Sum).Sum)
    [pscustomobject]@{
        Selected = $selected; Rows = $rows
        Record = [ordered]@{
            sampledAtUtc = [DateTime]::UtcNow.ToString('o'); processes = $rows; byRole = $byRole
            totalWorkingSetPrivateBytes = $total
        }
    }
}

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'Measure-HostMemory.ps1 only runs on Windows: it reads Win32_Process and Win32_PerfFormattedData_PerfProc_Process. It performs no device operation.'
}

$receipt = $null
if ($OutputPath) {
    $receipt = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
    if (Test-Path -LiteralPath $receipt) { throw 'The output file already exists; use a new OutputPath.' }
    $receiptDirectory = Split-Path -Parent $receipt
    if (-not (Test-Path -LiteralPath $receiptDirectory -PathType Container)) { throw ('The output directory does not exist: ' + $receiptDirectory) }
}

$sessionId = [Diagnostics.Process]::GetCurrentProcess().SessionId
$records = New-Object System.Collections.Generic.List[object]
$hostInfo = $null
for ($index = 1; $index -le $Samples; $index++) {
    if ($index -gt 1) { Start-Sleep -Seconds $IntervalSeconds }
    $sample = Get-MemorySample $sessionId
    if ($index -eq 1) {
        if ($sample.Selected.Count -eq 0) { throw 'No HeyMelody process was found in the current Windows session. Start the tray first.' }
        $firstHost = $sample.Selected | Where-Object { $_.Role -in @('tray', 'worker', 'catalog-worker', 'host-unknown') } | Select-Object -First 1
        if ($firstHost) { $hostInfo = Get-HostFileInfo $firstHost.Process }
    }
    $records.Add($sample.Record)
    Write-Output ('Sample ' + $index + '/' + $Samples + ' ' + $sample.Record.sampledAtUtc)
    $table = $sample.Rows | ForEach-Object {
        [pscustomobject][ordered]@{
            Role = $_.role; PID = $_.pid; 'PrivateWS(MiB)' = ConvertTo-Mib $_.workingSetPrivateBytes
            'WS(MiB)' = ConvertTo-Mib $_.workingSetBytes; 'PrivateBytes(MiB)' = ConvertTo-Mib $_.privateBytes
            Threads = $_.threads; Handles = $_.handles
        }
    }
    if ($table) { Write-Output ((@($table) | Format-Table -AutoSize | Out-String -Width 200).TrimEnd()) }
    Write-Output ('Total PrivateWS(MiB) ' + (ConvertTo-Mib $sample.Record.totalWorkingSetPrivateBytes))
}

$totals = @($records | ForEach-Object { [double]$_.totalWorkingSetPrivateBytes })
$roleSummary = [ordered]@{}
foreach ($role in @($records | ForEach-Object { $_.byRole.Keys } | Select-Object -Unique)) {
    $sums = @($records | ForEach-Object { if ($_.byRole.Contains($role)) { [double]$_.byRole[$role].workingSetPrivateBytes } else { 0.0 } })
    $roleSummary[$role] = [ordered]@{
        medianWorkingSetPrivateBytes = [long](Get-Median $sums)
        maxWorkingSetPrivateBytes = [long]($sums | Measure-Object -Maximum).Maximum
    }
}
$summary = [ordered]@{
    samples = $records.Count
    totalWorkingSetPrivateBytes = [ordered]@{
        min = [long]($totals | Measure-Object -Minimum).Minimum
        median = [long](Get-Median $totals)
        max = [long]($totals | Measure-Object -Maximum).Maximum
    }
    byRole = $roleSummary
}
if ($records.Count -gt 1) {
    Write-Output ('Summary over ' + $records.Count + ' samples: total PrivateWS(MiB) min ' + (ConvertTo-Mib $summary.totalWorkingSetPrivateBytes.min) +
        ' / median ' + (ConvertTo-Mib $summary.totalWorkingSetPrivateBytes.median) + ' / max ' + (ConvertTo-Mib $summary.totalWorkingSetPrivateBytes.max))
    foreach ($role in $roleSummary.Keys) {
        Write-Output ('  ' + $role + ' (per-sample sum) median ' + (ConvertTo-Mib $roleSummary[$role].medianWorkingSetPrivateBytes) + ' / max ' + (ConvertTo-Mib $roleSummary[$role].maxWorkingSetPrivateBytes) + ' MiB')
    }
}

if ($receipt) {
    $os = Get-CimInstance -ClassName Win32_OperatingSystem
    $cpus = @(Get-CimInstance -ClassName Win32_Processor)
    $result = [ordered]@{
        schemaVersion = 1; tool = 'Measure-HostMemory.ps1'; observedAtUtc = [DateTime]::UtcNow.ToString('o')
        scope = 'read-only process memory counters; no device operation'
        powerShellVersion = $PSVersionTable.PSVersion.ToString()
        windows = [ordered]@{ caption = $os.Caption; version = $os.Version; build = $os.BuildNumber; architecture = $os.OSArchitecture }
        cpu = [ordered]@{
            name = ([string]$cpus[0].Name).Trim()
            cores = [int](($cpus | Measure-Object NumberOfCores -Sum).Sum)
            logicalProcessors = [int](($cpus | Measure-Object NumberOfLogicalProcessors -Sum).Sum)
            l3CacheKB = [int](($cpus | Measure-Object L3CacheSize -Maximum).Maximum)
        }
        host = $hostInfo
        samples = $records.ToArray()
        summary = $summary
        notes = @('workingSetPrivateBytes comes from Win32_PerfFormattedData_PerfProc_Process.WorkingSetPrivate, about the Task Manager Details Memory column.',
            'Process lists, command lines, paths and Bluetooth addresses are not recorded; roles come from the mode flag only.',
            'The result describes this machine, build and moment only; it is not a device-behavior verification.')
    }
    $stream = [IO.File]::Open($receipt, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $writer = New-Object IO.StreamWriter($stream, (New-Object Text.UTF8Encoding($false)))
        try { $writer.Write((ConvertTo-Json -InputObject $result -Depth 8)) } finally { $writer.Dispose() }
    }
    finally { $stream.Dispose() }
    Write-Output ('MEMORY_RECEIPT ' + $receipt)
}
