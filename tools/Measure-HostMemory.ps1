<#
.SYNOPSIS
Reads memory, CPU, context-switch and power-state counters of the running HeyMelody Host tray, its workers and the UI processes (Windows only).
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

CPU, scheduling and power fields (per process and per sample; null or unknown when a
value cannot be read, which never stops sampling):
- cpuUserMs, cpuKernelMs, cpuTotalMs: cumulative processor time from Get-Process
  (UserProcessorTime, PrivilegedProcessorTime, TotalProcessorTime) in milliseconds.
- cpuPercent: change of cpuTotalMs between two consecutive samples divided by the elapsed
  wall time and the logical processor count, so 100 means every logical processor was
  busy (the scale of the Task Manager Details CPU column). It is null in the first sample
  and for a process that was not present in the previous sample.
- contextSwitchesPerSec: sum of Win32_PerfFormattedData_PerfProc_Thread.ContextSwitchesPersec
  over the threads of the process (one CIM query for all sampled PIDs). It is a rate
  computed by WMI, so use several samples and compare averages or medians.
- priorityClass: Process.PriorityClass as text, null when access is denied.
- powerThrottling: process power throttling policy read with GetProcessInformation:
  "eco" (execution-speed throttling forced on, as Windows efficiency mode does),
  "default" (no policy set), "custom:<ControlMask>/<StateMask>" or "unknown:<Win32 error>".
  ignoreTimerResolution is true when the ignore-timer-resolution bit (0x4) is set in both
  masks. Reading it needs a small P/Invoke helper: Add-Type compiles it once per session
  (Windows PowerShell 5.1 writes the temporary compiler files to %TEMP%; PowerShell 7
  compiles in memory). If it cannot be compiled the value is "unknown:helper".
Per sample, byRole and the totals hold the sums over processes of cpuPercent and
contextSwitchesPerSec. In the summary, byRole and totalCpuPercent / totalContextSwitchesPerSec
give the average and maximum of those per-sample sums (the first sample is excluded from the
CPU percent figures).
Compare runs only on the same machine, build and connection state. For example, measure
with no HeyMelody window open for at least 15 seconds so efficiency mode can take effect,
then measure again with the main window open.
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

$powerProbeSource = @'
using System;
using System.Runtime.InteropServices;

namespace HeyMelodyMeasure
{
    public sealed class PowerState
    {
        public string Mode;
        public bool? IgnoreTimerResolution;
    }

    public static class ProcessPower
    {
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const int ProcessPowerThrottling = 4;
        private const uint ExecutionSpeedMask = 0x1;
        private const uint TimerResolutionMask = 0x4;

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_POWER_THROTTLING_STATE
        {
            public uint Version;
            public uint ControlMask;
            public uint StateMask;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetProcessInformation(IntPtr process, int informationClass, ref PROCESS_POWER_THROTTLING_STATE information, uint informationSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        public static PowerState Query(int processId)
        {
            PowerState result = new PowerState();
            IntPtr handle = IntPtr.Zero;
            try
            {
                handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)processId);
                if (handle == IntPtr.Zero)
                {
                    result.Mode = "unknown:" + Marshal.GetLastWin32Error();
                    return result;
                }
                PROCESS_POWER_THROTTLING_STATE state = new PROCESS_POWER_THROTTLING_STATE();
                state.Version = 1;
                if (!GetProcessInformation(handle, ProcessPowerThrottling, ref state, 12))
                {
                    result.Mode = "unknown:" + Marshal.GetLastWin32Error();
                    return result;
                }
                uint control = state.ControlMask;
                uint mask = state.StateMask;
                if ((control & ExecutionSpeedMask) != 0 && (mask & ExecutionSpeedMask) != 0) { result.Mode = "eco"; }
                else if (control == 0) { result.Mode = "default"; }
                else { result.Mode = "custom:" + control + "/" + mask; }
                result.IgnoreTimerResolution = (control & TimerResolutionMask) != 0 && (mask & TimerResolutionMask) != 0;
            }
            catch (EntryPointNotFoundException) { result.Mode = "unknown:unsupported"; }
            catch (Exception) { result.Mode = "unknown:exception"; }
            finally
            {
                if (handle != IntPtr.Zero) { CloseHandle(handle); }
            }
            return result;
        }
    }
}
'@

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

function ConvertTo-Rounded($Value, [int]$Digits) {
    if ($null -eq $Value) { return $null }
    [math]::Round([double]$Value, $Digits)
}

function ConvertTo-Text($Value) {
    if ($null -eq $Value) { return 'n/a' }
    [string]$Value
}

function Get-Median([double[]]$Values) {
    $sorted = @($Values | Sort-Object)
    if ($sorted.Count -eq 0) { return $null }
    $mid = [int][math]::Floor($sorted.Count / 2)
    if ($sorted.Count % 2 -eq 1) { return $sorted[$mid] }
    ($sorted[$mid - 1] + $sorted[$mid]) / 2
}

function Get-NullableSum($Items, [string]$Field) {
    # Sum of the non-null values of one row field; null when no row has a value.
    $values = @($Items | Where-Object { $null -ne $_.$Field } | ForEach-Object { [double]$_.$Field })
    if ($values.Count -eq 0) { return $null }
    [math]::Round(($values | Measure-Object -Sum).Sum, 3)
}

function Get-SeriesStat([double[]]$Values, [string]$Kind) {
    if ($Values.Count -eq 0) { return $null }
    $measure = $Values | Measure-Object -Average -Maximum
    if ($Kind -eq 'max') { return [math]::Round($measure.Maximum, 3) }
    [math]::Round($measure.Average, 3)
}

function Get-RoleSeries($Records, [string]$Role, [string]$Field, [bool]$SkipFirst) {
    # Per-sample role sums of one field. A role missing from a sample counts as 0; unavailable (null) values are skipped.
    $start = 0
    if ($SkipFirst) { $start = 1 }
    for ($i = $start; $i -lt $Records.Count; $i++) {
        $byRole = $Records[$i].byRole
        if (-not $byRole.Contains($Role)) { 0.0; continue }
        $value = $byRole[$Role][$Field]
        if ($null -ne $value) { [double]$value }
    }
}

function Get-SafeValue([scriptblock]$Read) {
    try { & $Read } catch { $null }
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

function Get-LogicalProcessorCount {
    $count = 0
    try { $count = [int]((Get-CimInstance -ClassName Win32_Processor | Measure-Object NumberOfLogicalProcessors -Sum).Sum) }
    catch { $count = 0 }
    if ($count -ge 1) { return $count }
    [Environment]::ProcessorCount
}

function Initialize-PowerThrottlingProbe {
    # Compiles the small P/Invoke helper once per session; sampling continues without it.
    if ('HeyMelodyMeasure.ProcessPower' -as [type]) { return }
    try { Add-Type -TypeDefinition $powerProbeSource -Language CSharp }
    catch { Write-Warning 'The power-throttling helper could not be compiled; powerThrottling will be reported as unknown:helper.' }
}

function Get-PowerThrottlingState([int]$ProcessId) {
    # Never throws: a failed query yields an unknown mode.
    try {
        $probe = 'HeyMelodyMeasure.ProcessPower' -as [type]
        if (-not $probe) { return [pscustomobject]@{ Mode = 'unknown:helper'; IgnoreTimerResolution = $null } }
        $state = $probe::Query($ProcessId)
        [pscustomobject]@{ Mode = $state.Mode; IgnoreTimerResolution = $state.IgnoreTimerResolution }
    }
    catch { [pscustomobject]@{ Mode = 'unknown:exception'; IgnoreTimerResolution = $null } }
}

function Get-ProcessCpuReading([int]$ProcessId) {
    # Cumulative CPU times in milliseconds with a monotonic timestamp; null when the process is gone.
    $process = $null
    try {
        $process = Get-Process -Id $ProcessId -ErrorAction Stop
        $user = Get-SafeValue { $process.UserProcessorTime.TotalMilliseconds }
        $kernel = Get-SafeValue { $process.PrivilegedProcessorTime.TotalMilliseconds }
        $total = Get-SafeValue { $process.TotalProcessorTime.TotalMilliseconds }
        $ticks = [Diagnostics.Stopwatch]::GetTimestamp()
        [pscustomobject]@{
            UserMs = $user; KernelMs = $kernel; TotalMs = $total; Ticks = $ticks
            StartTicks = (Get-SafeValue { $process.StartTime.Ticks })
            Priority = (Get-SafeValue { $priority = $process.PriorityClass; if ($null -ne $priority) { $priority.ToString() } })
        }
    }
    catch { $null }
    finally { if ($process) { $process.Dispose() } }
}

function Get-MemorySample([int]$SessionId, [int]$LogicalProcessors, $PreviousCpu) {
    $all = @(Get-CimInstance -ClassName Win32_Process)
    $selected = @(foreach ($process in $all) {
        if ([int]$process.SessionId -ne $SessionId) { continue }
        $role = Get-ProcessRole $process.Name $process.CommandLine
        if ($role) { [pscustomobject]@{ Role = $role; Process = $process } }
    })
    $perf = @{}
    $switches = @{}
    if ($selected.Count -gt 0) {
        $filter = (@($selected | ForEach-Object { 'IDProcess=' + [int]$_.Process.ProcessId }) -join ' OR ')
        foreach ($counter in @(Get-CimInstance -ClassName Win32_PerfFormattedData_PerfProc_Process -Filter $filter)) {
            $perf[[int]$counter.IDProcess] = $counter
        }
        # The thread counters are optional: when they are unavailable contextSwitchesPerSec stays null.
        try {
            foreach ($thread in @(Get-CimInstance -ClassName Win32_PerfFormattedData_PerfProc_Thread -Filter $filter)) {
                $threadOwner = [int]$thread.IDProcess
                if (-not $switches.ContainsKey($threadOwner)) { $switches[$threadOwner] = 0L }
                $switches[$threadOwner] += [long]$thread.ContextSwitchesPersec
            }
        }
        catch { $switches = @{} }
    }
    $cpuState = @{}
    $rows = @(foreach ($item in ($selected | Sort-Object { Get-RoleOrder $_.Role }, { [int]$_.Process.ProcessId })) {
        $processId = [int]$item.Process.ProcessId
        $counter = $perf[$processId]
        $row = [ordered]@{
            role = $item.Role; pid = $processId; parentPid = [int]$item.Process.ParentProcessId
            workingSetPrivateBytes = $null; workingSetBytes = $null; privateBytes = $null; threads = $null; handles = $null
            cpuUserMs = $null; cpuKernelMs = $null; cpuTotalMs = $null; cpuPercent = $null
            contextSwitchesPerSec = $null; priorityClass = $null; powerThrottling = $null; ignoreTimerResolution = $null
        }
        if ($counter) {
            $row.workingSetPrivateBytes = [long]$counter.WorkingSetPrivate
            $row.workingSetBytes = [long]$counter.WorkingSet
            $row.privateBytes = [long]$counter.PrivateBytes
            $row.threads = [int]$counter.ThreadCount
            $row.handles = [int]$counter.HandleCount
        }
        if ($switches.ContainsKey($processId)) { $row.contextSwitchesPerSec = [long]$switches[$processId] }
        $reading = Get-ProcessCpuReading $processId
        if ($reading) {
            $cpuState[$processId] = $reading
            $row.cpuUserMs = ConvertTo-Rounded $reading.UserMs 1
            $row.cpuKernelMs = ConvertTo-Rounded $reading.KernelMs 1
            $row.cpuTotalMs = ConvertTo-Rounded $reading.TotalMs 1
            $row.priorityClass = $reading.Priority
            $previous = $null
            if ($null -ne $PreviousCpu) { $previous = $PreviousCpu[$processId] }
            if ($previous -and $null -ne $reading.TotalMs -and $null -ne $previous.TotalMs) {
                # A reused PID (different start time) or a counter that went backwards is not a valid interval.
                $sameProcess = ($null -eq $reading.StartTicks) -or ($null -eq $previous.StartTicks) -or ($reading.StartTicks -eq $previous.StartTicks)
                $elapsedMs = ($reading.Ticks - $previous.Ticks) * 1000.0 / [Diagnostics.Stopwatch]::Frequency
                $deltaMs = $reading.TotalMs - $previous.TotalMs
                if ($sameProcess -and $elapsedMs -gt 0 -and $deltaMs -ge 0) {
                    $row.cpuPercent = [math]::Round($deltaMs / ($elapsedMs * [math]::Max(1, $LogicalProcessors)) * 100.0, 3)
                }
            }
        }
        $power = Get-PowerThrottlingState $processId
        $row.powerThrottling = $power.Mode
        $row.ignoreTimerResolution = $power.IgnoreTimerResolution
        [pscustomobject]$row
    })
    $roles = @($rows | ForEach-Object { $_.role } | Select-Object -Unique)
    $byRole = [ordered]@{}
    foreach ($role in $roles) {
        $group = @($rows | Where-Object { $_.role -eq $role })
        $byRole[$role] = [ordered]@{
            count = $group.Count
            workingSetPrivateBytes = [long](($group | Where-Object { $null -ne $_.workingSetPrivateBytes } | Measure-Object workingSetPrivateBytes -Sum).Sum)
            cpuPercent = (Get-NullableSum $group 'cpuPercent')
            contextSwitchesPerSec = (Get-NullableSum $group 'contextSwitchesPerSec')
        }
    }
    $total = [long](($rows | Where-Object { $null -ne $_.workingSetPrivateBytes } | Measure-Object workingSetPrivateBytes -Sum).Sum)
    [pscustomobject]@{
        Selected = $selected; Rows = $rows; CpuState = $cpuState
        Record = [ordered]@{
            sampledAtUtc = [DateTime]::UtcNow.ToString('o'); processes = $rows; byRole = $byRole
            totalWorkingSetPrivateBytes = $total
            totalCpuPercent = (Get-NullableSum $rows 'cpuPercent')
            totalContextSwitchesPerSec = (Get-NullableSum $rows 'contextSwitchesPerSec')
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

Initialize-PowerThrottlingProbe
$logicalProcessors = Get-LogicalProcessorCount
$sessionId = [Diagnostics.Process]::GetCurrentProcess().SessionId
$records = New-Object System.Collections.Generic.List[object]
$hostInfo = $null
$cpuState = $null
for ($index = 1; $index -le $Samples; $index++) {
    if ($index -gt 1) { Start-Sleep -Seconds $IntervalSeconds }
    $sample = Get-MemorySample $sessionId $logicalProcessors $cpuState
    $cpuState = $sample.CpuState
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
    # A second table keeps each one narrow enough that Format-Table does not drop columns on an 80 to 120 column host.
    $cpuTable = $sample.Rows | ForEach-Object {
        [pscustomobject][ordered]@{
            Role = $_.role; PID = $_.pid; 'CPU(ms)' = ConvertTo-Rounded $_.cpuTotalMs 0; 'CPU%' = $_.cpuPercent
            'CtxSw/s' = $_.contextSwitchesPerSec; Priority = $_.priorityClass; Power = $_.powerThrottling
        }
    }
    if ($cpuTable) { Write-Output ((@($cpuTable) | Format-Table -AutoSize | Out-String -Width 200).TrimEnd()) }
    Write-Output ('Total PrivateWS(MiB) ' + (ConvertTo-Mib $sample.Record.totalWorkingSetPrivateBytes))
    Write-Output ('Total CPU% ' + (ConvertTo-Text $sample.Record.totalCpuPercent) + ' (of ' + $logicalProcessors + ' logical processors) / CtxSw/s ' + (ConvertTo-Text $sample.Record.totalContextSwitchesPerSec))
}

$totals = @($records | ForEach-Object { [double]$_.totalWorkingSetPrivateBytes })
$cpuTotals = @($records | ForEach-Object { $_.totalCpuPercent } | Where-Object { $null -ne $_ } | ForEach-Object { [double]$_ })
$switchTotals = @($records | ForEach-Object { $_.totalContextSwitchesPerSec } | Where-Object { $null -ne $_ } | ForEach-Object { [double]$_ })
$roleSummary = [ordered]@{}
foreach ($role in @($records | ForEach-Object { $_.byRole.Keys } | Select-Object -Unique)) {
    $sums = @($records | ForEach-Object { if ($_.byRole.Contains($role)) { [double]$_.byRole[$role].workingSetPrivateBytes } else { 0.0 } })
    $cpuSeries = @(Get-RoleSeries $records $role 'cpuPercent' $true)
    $switchSeries = @(Get-RoleSeries $records $role 'contextSwitchesPerSec' $false)
    $roleSummary[$role] = [ordered]@{
        medianWorkingSetPrivateBytes = [long](Get-Median $sums)
        maxWorkingSetPrivateBytes = [long]($sums | Measure-Object -Maximum).Maximum
        avgCpuPercent = (Get-SeriesStat $cpuSeries 'avg')
        maxCpuPercent = (Get-SeriesStat $cpuSeries 'max')
        avgContextSwitchesPerSec = (Get-SeriesStat $switchSeries 'avg')
        maxContextSwitchesPerSec = (Get-SeriesStat $switchSeries 'max')
    }
}
$summary = [ordered]@{
    samples = $records.Count
    totalWorkingSetPrivateBytes = [ordered]@{
        min = [long]($totals | Measure-Object -Minimum).Minimum
        median = [long](Get-Median $totals)
        max = [long]($totals | Measure-Object -Maximum).Maximum
    }
    totalCpuPercent = [ordered]@{ avg = (Get-SeriesStat $cpuTotals 'avg'); max = (Get-SeriesStat $cpuTotals 'max') }
    totalContextSwitchesPerSec = [ordered]@{ avg = (Get-SeriesStat $switchTotals 'avg'); max = (Get-SeriesStat $switchTotals 'max') }
    byRole = $roleSummary
}
if ($records.Count -gt 1) {
    Write-Output ('Summary over ' + $records.Count + ' samples: total PrivateWS(MiB) min ' + (ConvertTo-Mib $summary.totalWorkingSetPrivateBytes.min) +
        ' / median ' + (ConvertTo-Mib $summary.totalWorkingSetPrivateBytes.median) + ' / max ' + (ConvertTo-Mib $summary.totalWorkingSetPrivateBytes.max))
    Write-Output ('  total CPU% avg ' + (ConvertTo-Text $summary.totalCpuPercent.avg) + ' / max ' + (ConvertTo-Text $summary.totalCpuPercent.max) +
        '; CtxSw/s avg ' + (ConvertTo-Text $summary.totalContextSwitchesPerSec.avg) + ' / max ' + (ConvertTo-Text $summary.totalContextSwitchesPerSec.max))
    foreach ($role in $roleSummary.Keys) {
        Write-Output ('  ' + $role + ' (per-sample sum) median ' + (ConvertTo-Mib $roleSummary[$role].medianWorkingSetPrivateBytes) + ' / max ' + (ConvertTo-Mib $roleSummary[$role].maxWorkingSetPrivateBytes) + ' MiB')
        Write-Output ('  ' + $role + ' (per-sample sum) CPU% avg ' + (ConvertTo-Text $roleSummary[$role].avgCpuPercent) + ' / max ' + (ConvertTo-Text $roleSummary[$role].maxCpuPercent) +
            '; CtxSw/s avg ' + (ConvertTo-Text $roleSummary[$role].avgContextSwitchesPerSec) + ' / max ' + (ConvertTo-Text $roleSummary[$role].maxContextSwitchesPerSec))
    }
}

if ($receipt) {
    $os = Get-CimInstance -ClassName Win32_OperatingSystem
    $cpus = @(Get-CimInstance -ClassName Win32_Processor)
    $result = [ordered]@{
        schemaVersion = 1; tool = 'Measure-HostMemory.ps1'; observedAtUtc = [DateTime]::UtcNow.ToString('o')
        scope = 'read-only process memory, CPU, context-switch and power-throttling counters; no device operation'
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
            'cpuUserMs, cpuKernelMs and cpuTotalMs are cumulative Get-Process processor times in milliseconds.',
            'cpuPercent is the change of cpuTotalMs between consecutive samples divided by elapsed wall time and the logical processor count (100 = all logical processors busy); it is null for the first sample.',
            'contextSwitchesPerSec sums Win32_PerfFormattedData_PerfProc_Thread.ContextSwitchesPersec over the threads of the process; null means the thread counters were unavailable.',
            'powerThrottling is read with GetProcessInformation(ProcessPowerThrottling): eco = execution-speed throttling forced on (Windows efficiency mode), default = no policy,',
            'custom:<ControlMask>/<StateMask> = any other policy, unknown:<Win32 error> = the query failed. priorityClass is null when access is denied.',
            'Compare runs only on the same machine and connection state, for example with no window open for at least 15 seconds (efficiency mode) and again with the main window open.',
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
