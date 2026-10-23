<#
.SYNOPSIS
Formats an existing Measure-ModelLoading batch and optional full-window memory recording.
.DESCRIPTION
Read-only by default. Rechecks summary.json against every per-process timing and
requires a consistent application/runtime identity. Does not start an app, contact a headset,
rebuild, or alter the recorded measurements. Markdown contains no absolute paths.
OutputPath, when supplied, must not already exist.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Directory,
    [string]$PhaseName = '基线',
    [string]$MemoryPath,
    [int]$MemoryProcessId = 0,
    [ValidateSet('markdown', 'json')][string]$Format = 'markdown',
    [string]$OutputPath
)
$ErrorActionPreference = 'Stop'
$culture = [Globalization.CultureInfo]::InvariantCulture
function Get-PositiveProcessId($Value, [string]$Context) {
    $parsedProcessId = 0
    if ($null -eq $Value -or ([string]$Value) -notmatch '^[1-9][0-9]*$' `
        -or ![int]::TryParse([string]$Value, [ref]$parsedProcessId)) {
        throw "Missing or invalid process ID in $Context."
    }
    $parsedProcessId
}
function Get-ProcessStart($Value, [string]$Context) {
    if ($Value -is [DateTimeOffset]) { return $Value }
    if ($Value -is [DateTime] -and $Value.Kind -ne [DateTimeKind]::Unspecified) { return [DateTimeOffset]$Value }
    $parsedStart = [DateTimeOffset]::MinValue
    if ($Value -isnot [string] -or $Value -notmatch 'T.*(?:Z|[+-][0-9]{2}:[0-9]{2})$' `
        -or ![DateTimeOffset]::TryParse($Value, $culture, [Globalization.DateTimeStyles]::None, [ref]$parsedStart)) {
        throw "Missing or invalid startedAtUtc in $Context."
    }
    $parsedStart
}
$root = (Resolve-Path -LiteralPath $Directory).Path
$session = Get-Content -LiteralPath (Join-Path $root 'session.json') -Raw | ConvertFrom-Json
$summary = @(Get-Content -LiteralPath (Join-Path $root 'summary.json') -Raw | ConvertFrom-Json)
$expectedGroups = @($session.backends).Count * @($session.models).Count * 2
if ($summary.Count -ne $expectedGroups -or @($summary | Group-Object backend, model, kind | Where-Object Count -ne 1).Count) {
    throw 'The summary must have exactly one cold and one hot group for each backend/model pair.'
}
$identities = [Collections.Generic.List[object]]::new()
$readings = [Collections.Generic.List[object]]::new()
$processIdentities = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$metrics = @('AttachToLoadStartMilliseconds', 'RetirementWaitMilliseconds', 'GateWaitMilliseconds',
    'ReadVerifyMilliseconds', 'RuntimePreparationMilliseconds', 'ProbeWaitMilliseconds', 'EngineCreateMilliseconds',
    'ModelLoadMilliseconds', 'FirstFrameMilliseconds', 'TotalMilliseconds')
foreach ($backend in $session.backends) {
    foreach ($model in $session.models) {
        foreach ($kind in @('cold', 'hot')) {
            $group = @($summary | Where-Object { $_.backend -eq $backend -and $_.model -eq $model -and $_.kind -eq $kind })
            $expectedCount = if ($kind -eq 'cold') { [int]$session.coldRuns } else { [int]$session.coldRuns * [int]$session.hotRunsPerProcess }
            if ($group.Count -ne 1 -or $group[0].count -ne $expectedCount) { throw "Unexpected sample count for $backend/$model/$kind." }
        }
        for ($index = 1; $index -le $session.coldRuns; $index++) {
            $report = Get-Content -LiteralPath (Join-Path $root "$backend/$model-$index/model-loading.json") -Raw | ConvertFrom-Json
            $expectedModel = if ($model -ieq 'online') { 'file' } else { $model }
            if (!$report.success -or $report.processName -ne 'HeyMelody' -or $report.backend -ine $backend `
                -or $report.Model -ine $expectedModel -or $report.runs.Count -ne ($session.hotRunsPerProcess + 1)) {
                throw "Incomplete or mismatched process receipt: $backend/$model-$index."
            }
            $processId = Get-PositiveProcessId $report.processId "$backend/$model-$index"
            $process = Get-Content -LiteralPath (Join-Path $root "$backend/$model-$index/process.json") -Raw | ConvertFrom-Json
            $recordedProcessId = Get-PositiveProcessId $process.processId "$backend/$model-$index/process.json"
            if ($recordedProcessId -ne $processId) { throw "Process metadata disagrees with its receipt: $backend/$model-$index." }
            $startedAt = Get-ProcessStart $process.startedAtUtc "$backend/$model-$index/process.json"
            $processIdentity = '{0}:{1}' -f $processId, $startedAt.UtcDateTime.Ticks
            if (!$processIdentities.Add($processIdentity)) { throw "The batch repeats process identity $processIdentity; cold receipts must come from distinct starts." }
            if (@($report.runs | Where-Object kind -eq 'cold').Count -ne 1 -or @($report.runs | Where-Object kind -eq 'hot').Count -ne $session.hotRunsPerProcess) {
                throw "Incorrect cold/hot split: $backend/$model-$index."
            }
            foreach ($hash in @($report.appAssemblySha256, $report.nativeLibrarySha256, $report.modelSha256)) {
                if ($hash -notmatch '^[0-9a-fA-F]{64}$') { throw "Missing build or model identity: $backend/$model-$index." }
            }
            $sampleIndex = 0
            foreach ($run in $report.runs) {
                $expectedKind = if ($sampleIndex -eq 0) { 'cold' } else { 'hot' }
                if ($null -eq $run.sample -or ([string]$run.sample) -notmatch '^[0-9]+$' `
                    -or [int]$run.sample -ne $sampleIndex -or $run.kind -ine $expectedKind) {
                    throw "Missing or out-of-order sample in $backend/$model-$index."
                }
                foreach ($metric in $metrics) {
                    $value = $run.timings.$metric
                    if ($null -eq $value -or ![double]::IsFinite([double]$value) -or [double]$value -lt 0) {
                        throw "Missing or invalid $metric in $backend/$model-$index."
                    }
                }
                $readings.Add([pscustomobject]@{ backend = $backend; model = $model; kind = $run.kind; timings = $run.timings })
                $sampleIndex++
            }
            $identities.Add([pscustomobject]@{ model = $model; app = $report.appAssemblySha256; native = $report.nativeLibrarySha256; asset = $report.modelSha256 })
        }
    }
}
if (@($identities.app | Sort-Object -Unique).Count -ne 1 -or @($identities.native | Sort-Object -Unique).Count -ne 1) {
    throw 'The batch mixes application or native-runtime binaries; do not combine its timings.'
}
foreach ($group in ($identities | Group-Object model)) {
    if (@($group.Group.asset | Sort-Object -Unique).Count -ne 1) { throw "The batch mixes model bytes for $($group.Name)." }
}

function Get-Statistics($Values) {
    $sorted = @($Values | Sort-Object)
    if (!$sorted.Count) { return $null }
    $middle = [int][Math]::Floor($sorted.Count / 2)
    $median = if ($sorted.Count % 2) { $sorted[$middle] } else { ($sorted[$middle - 1] + $sorted[$middle]) / 2 }
    [ordered]@{ count = $sorted.Count; median = $median; maximum = $sorted[-1] }
}
function Format-Pair($Value, [int]$Digits = 1) {
    if ($null -eq $Value) { return '—' }
    ([double]$Value.median).ToString("F$Digits", $culture) + '/' + ([double]$Value.maximum).ToString("F$Digits", $culture)
}
foreach ($row in $summary) {
    $samples = @($readings | Where-Object { $_.backend -eq $row.backend -and $_.model -eq $row.model -and $_.kind -eq $row.kind })
    foreach ($metric in $metrics) {
        $statistics = Get-Statistics -Values @($samples | ForEach-Object { [double]$_.timings.$metric })
        $recorded = $row.stages.$metric
        if ($null -eq $recorded.median -or $null -eq $recorded.maximum `
            -or ![double]::IsFinite([double]$recorded.median) -or ![double]::IsFinite([double]$recorded.maximum) `
            -or $statistics.count -ne $row.count `
            -or [Math]::Abs([double]$recorded.median - $statistics.median) -gt 0.000001 `
            -or [Math]::Abs([double]$recorded.maximum - $statistics.maximum) -gt 0.000001) {
            throw "The recorded summary disagrees with raw $metric samples for $($row.backend)/$($row.model)/$($row.kind)."
        }
    }
}
$memorySummary = $null
if ($MemoryPath) {
    $memory = Get-Content -LiteralPath $MemoryPath -Raw | ConvertFrom-Json
    $appPids = @($memory.samples | ForEach-Object {
        $_.processes | Where-Object role -eq 'app' | ForEach-Object { Get-PositiveProcessId $_.pid 'memory app sample' }
    } | Sort-Object -Unique)
    if (!$MemoryProcessId) {
        if ($appPids.Count -ne 1) { throw 'Specify MemoryProcessId when the memory recording has multiple or no application PIDs.' }
        $MemoryProcessId = $appPids[0]
    }
    if ($MemoryProcessId -notin $appPids) { throw 'MemoryProcessId must identify an app process in the memory recording.' }
    $readings = @($memory.samples | ForEach-Object {
        $at = $_.sampledAtUtc
        $_.processes | Where-Object { $_.pid -eq $MemoryProcessId -and $_.role -eq 'app' } `
            | ForEach-Object { [pscustomobject]@{ at = $at; memory = $_.workingSetPrivateBytes; cpu = $_.cpuPercent } }
    })
    if (!$readings.Count) { throw 'The selected application PID has no samples.' }
    foreach ($reading in $readings) {
        foreach ($field in @('memory', 'cpu')) {
            $value = $reading.$field
            if ($null -ne $value -and (![double]::IsFinite([double]$value) -or [double]$value -lt 0)) {
                throw "Invalid $field reading for app PID $MemoryProcessId."
            }
        }
    }
    $workingSet = Get-Statistics @($readings | Where-Object { $null -ne $_.memory } | ForEach-Object { [double]$_.memory })
    $cpu = Get-Statistics @($readings | Where-Object { $null -ne $_.cpu } | ForEach-Object { [double]$_.cpu })
    if ($null -eq $workingSet) { throw 'The selected process has no usable private-working-set readings.' }
    $memorySummary = [ordered]@{
        scope = 'Full-window recording supplied separately; process ID and actual timestamps select its rows. This does not establish a headset/RPC or tray-host measurement.'
        processId = $MemoryProcessId; firstSampleUtc = $readings[0].at; lastSampleUtc = $readings[-1].at
        actualSpanSeconds = ([DateTimeOffset]$readings[-1].at - [DateTimeOffset]$readings[0].at).TotalSeconds
        workingSetPrivateBytes = $workingSet; workingSetPrivateMiB = [ordered]@{ median = $workingSet.median / 1MB; maximum = $workingSet.maximum / 1MB }
        normalizedCpuPercent = $cpu
    }
}
$result = [ordered]@{
    schemaVersion = 1; phase = $PhaseName; sourceCommit = $session.sourceCommit
    appAssemblySha256 = $identities[0].app; nativeLibrarySha256 = $identities[0].native
    processCount = $identities.Count; sampleCount = ($summary | Measure-Object count -Sum).Sum
    groups = $summary; fullWindowMemory = $memorySummary
}
if ($Format -eq 'json') { $text = $result | ConvertTo-Json -Depth 8 }
else {
    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add("## $PhaseName")
    $lines.Add('')
    $lines.Add('单位 ms；每格为中位数/最大值。热样本来自多个进程，每个进程执行同样次数的重附加。')
    $modelNames = @{ free4 = 'Free4'; dynaudio = 'Free4 Dynaudio'; online = '在线 Free4' }
    foreach ($backend in $session.backends) {
        $lines.Add(''); $lines.Add("### $backend"); $lines.Add('')
        $lines.Add('| 来源 | 加载 | n | 总计 | 读取校验 | 探测 | 引擎创建 | 模型装载 | 首帧 |')
        $lines.Add('| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |')
        foreach ($model in $session.models) {
            foreach ($kind in @('cold', 'hot')) {
                $row = $summary | Where-Object { $_.backend -eq $backend -and $_.model -eq $model -and $_.kind -eq $kind }
                $label = if ($kind -eq 'cold') { '冷' } else { '热' }
                $pairs = foreach ($metric in @('TotalMilliseconds', 'ReadVerifyMilliseconds', 'ProbeWaitMilliseconds', 'EngineCreateMilliseconds', 'ModelLoadMilliseconds', 'FirstFrameMilliseconds')) { Format-Pair $row.stages.$metric }
                $lines.Add('| ' + $modelNames[$model] + ' | ' + $label + ' | ' + $row.count + ' | ' + ($pairs -join ' | ') + ' |')
            }
        }
    }
    $lines.Add(''); $lines.Add('### 其余阶段'); $lines.Add('')
    $lines.Add('| 后端 | 来源/加载 | 附加至开始 | 等待退役 | 等待 gate | 运行库准备 |')
    $lines.Add('| --- | --- | ---: | ---: | ---: | ---: |')
    foreach ($row in $summary) {
        $label = if ($row.kind -eq 'cold') { '冷' } else { '热' }
        $pairs = foreach ($metric in @('AttachToLoadStartMilliseconds', 'RetirementWaitMilliseconds', 'GateWaitMilliseconds', 'RuntimePreparationMilliseconds')) { Format-Pair $row.stages.$metric 3 }
        $lines.Add('| ' + $row.backend + ' | ' + $modelNames[$row.model] + '/' + $label + ' | ' + ($pairs -join ' | ') + ' |')
    }
    if ($memorySummary) {
        $lines.Add(''); $lines.Add('### 独立完整窗口记录'); $lines.Add('')
        $lines.Add('| 内存点数 | 私有工作集 MiB | 有效 CPU 点数 | CPU % | 实际覆盖秒数 |')
        $lines.Add('| ---: | ---: | ---: | ---: | ---: |')
        $lines.Add('| ' + $workingSet.count + ' | ' + (Format-Pair $memorySummary.workingSetPrivateMiB 3) + ' | ' + $cpu.count + ' | ' + (Format-Pair $cpu 3) + ' | ' + ([double]$memorySummary.actualSpanSeconds).ToString('F3', $culture) + ' |')
    }
    $text = $lines -join [Environment]::NewLine
}
if ($OutputPath) {
    $outputStream = [IO.FileStream]::new($OutputPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    $outputWriter = $null
    try {
        $outputWriter = [IO.StreamWriter]::new($outputStream, [Text.UTF8Encoding]::new($false))
        $outputWriter.WriteLine($text)
    }
    finally {
        try { if ($null -ne $outputWriter) { $outputWriter.Dispose() } }
        finally { $outputStream.Dispose() }
    }
}
else { Write-Output $text }
