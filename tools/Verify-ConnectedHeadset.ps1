<#
.SYNOPSIS
Verifies one connected headset through an exact x64 Host worker, without setting commands.
.DESCRIPTION
Run in a fresh PowerShell 7 process on Windows with .NET 10. Close other copies of
HeyMelody first. This reads existing preferences/cache without contacting the cloud
or changing pairing, settings, whitelist, or cache. Bluetooth GET requests and
notification subscriptions are submitted; calibration workflows are never started.
The optional receipt is sanitized and created only when its destination is absent.
.EXAMPLE
pwsh -File tools/Verify-ConnectedHeadset.ps1 -HostPath release/HeyMelody_Windows_0.8.1/x64/backend/HeyMelody.Host.exe -Bundled -OutputPath artifacts/connected-x4.json
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$HostPath,
    [switch]$Bundled,
    [string]$TargetName = 'OPPO Enco X4',
    [string]$OutputPath
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Set-StrictMode -Version Latest

function Require([bool]$Condition, [string]$Message) {
    if (-not $Condition) {
        $failure = [InvalidOperationException]::new($Message)
        $failure.Data['ConnectedHeadsetVerificationReason'] = $Message
        throw $failure
    }
}
function Count-Values($Value) {
    if ($null -eq $Value) { return 0 }
    if ($Value -is [System.Collections.IDictionary]) { return $Value.Count }
    return @($Value).Count
}
function Artifact([string]$Path) {
    Require ([IO.File]::Exists($Path)) 'A required Host artifact is missing.'
    [ordered]@{ name = [IO.Path]::GetFileName($Path); version = [Diagnostics.FileVersionInfo]::GetVersionInfo($Path).FileVersion;
        sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
}
function Start-OwnedProcess([string]$Mode) {
    $start = [Diagnostics.ProcessStartInfo]::new($script:taskHost)
    $start.ArgumentList.Add($Mode)
    $start.WorkingDirectory = $script:taskHostDirectory
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardInputEncoding = [Text.UTF8Encoding]::new($false)
    $start.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    $start.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    Require ($process.Start()) 'The owned Host child did not start.'
    return $process
}
function Stop-OwnedProcess($Process, [datetime]$Deadline = [datetime]::MaxValue) {
    if ($null -eq $Process) { return }
    try {
        $Process.StandardInput.Close()
        $grace = [int][math]::Min(4000, [math]::Max(0, ($Deadline - [datetime]::UtcNow).TotalMilliseconds))
        if (-not $Process.WaitForExit($grace)) { $Process.Kill($true); [void]$Process.WaitForExit(4000) }
    } catch { }
    $Process.Dispose()
}
function Read-WorkerMessage([datetime]$Deadline) {
    if ($null -eq $script:pendingLine) { $script:pendingLine = $script:worker.StandardOutput.ReadLineAsync() }
    $remaining = $Deadline - [datetime]::UtcNow
    Require ($remaining.TotalMilliseconds -gt 0) 'The worker deadline elapsed.'
    $line = $script:pendingLine.WaitAsync($remaining).GetAwaiter().GetResult()
    $script:pendingLine = $null
    Require ($null -ne $line -and $line.Length -le 524288) 'The worker returned an invalid record.'
    $message = ConvertFrom-Json -InputObject $line -AsHashtable
    if ($message.kind -eq 'snapshot') {
        $script:snapshotUpdates++
        $script:lastSnapshot = $message.snapshot
    }
    return $message
}
function Send-WorkerRequest([hashtable]$Request) {
    Require ($Request.operation -in @('initialize', 'diagnostics', 'shutdown')) 'Only verification operations are permitted.'
    $line = ConvertTo-Json -InputObject $Request -Compress -Depth 6
    Require ($line.Length -le 524288) 'The initialization request exceeds the wire limit.'
    $remaining = $script:workerDeadline - [datetime]::UtcNow
    Require ($remaining.TotalMilliseconds -gt 0) 'The worker deadline elapsed before submission.'
    [void]$script:worker.StandardInput.WriteLineAsync($line).WaitAsync($remaining).GetAwaiter().GetResult()
    $remaining = $script:workerDeadline - [datetime]::UtcNow
    Require ($remaining.TotalMilliseconds -gt 0) 'The worker deadline elapsed before flush.'
    [void]$script:worker.StandardInput.FlushAsync().WaitAsync($remaining).GetAwaiter().GetResult()
}
function Await-WorkerResponse([long]$Id, [datetime]$Deadline) {
    while ($true) {
        $message = Read-WorkerMessage $Deadline
        if ($message.kind -eq 'response' -and $message.id -eq $Id) {
            Require ($message.success -eq $true) 'The worker did not complete the verification operation.'
            return $message.data
        }
    }
}

$script:worker = $null
$catalogProcess = $null
$catalogDeadline = [datetime]::MaxValue
$workerDeadline = [datetime]::MaxValue
$script:pendingLine = $null
$script:snapshotUpdates = 0
$script:lastSnapshot = $null
$phase = 'preflight'
$receipt = [ordered]@{
    schemaVersion = 1; success = $false; observedAtUtc = [datetime]::UtcNow.ToString('o'); architecture = 'x64';
    evidenceScope = 'exact-host-worker-initialization-and-passive-monitoring';
    fullGuiExecutionVerified = $false; arm64ExecutionVerified = $false; settingChangesRequested = $false
}
try {
    Require ($IsWindows -and [Environment]::Is64BitProcess -and [Environment]::Version.Major -ge 10) 'Windows x64 PowerShell with .NET 10 is required.'
    $script:taskHost = [IO.Path]::GetFullPath($HostPath)
    $script:taskHostDirectory = [IO.Path]::GetDirectoryName($script:taskHost)
    Require ([IO.Path]::GetFileName($script:taskHost) -eq 'HeyMelody.Host.exe') 'HostPath must identify the actual Host apphost.'
    $artifactNames = @('HeyMelody.Host.exe', 'HeyMelody.Host.deps.json', 'HeyMelody.Host.runtimeconfig.json') +
        @(Get-ChildItem -LiteralPath $script:taskHostDirectory -Filter 'HeyMelody*.dll' -File | Sort-Object Name | ForEach-Object Name)
    $receipt.artifacts = @($artifactNames | ForEach-Object { Artifact (Join-Path $script:taskHostDirectory $_) })
    $peStream = [IO.File]::OpenRead($script:taskHost)
    $peReader = [IO.BinaryReader]::new($peStream)
    try {
        Require ($peReader.ReadUInt16() -eq 0x5A4D) 'The Host apphost has no MZ signature.'
        $peStream.Position = 0x3C
        $peOffset = $peReader.ReadInt32()
        $peStream.Position = $peOffset
        Require ($peReader.ReadUInt32() -eq 0x4550 -and $peReader.ReadUInt16() -eq 0x8664) 'This verifier requires an x64 Host apphost.'
    } finally { $peReader.Dispose() }
    if ($OutputPath) {
        $OutputPath = [IO.Path]::GetFullPath($OutputPath)
        Require (-not [IO.File]::Exists($OutputPath)) 'The receipt destination already exists.'
        Require ([IO.Directory]::Exists([IO.Path]::GetDirectoryName($OutputPath))) 'The receipt parent directory must already exist.'
    }
    $existingBackend = $false
    $sessionId = [Diagnostics.Process]::GetCurrentProcess().SessionId
    foreach ($running in [Diagnostics.Process]::GetProcessesByName('HeyMelody.Host')) {
        try { if ($running.SessionId -eq $sessionId) { $existingBackend = $true } }
        finally { $running.Dispose() }
    }
    Require (-not $existingBackend) 'A HeyMelody backend is already running; close it before isolated worker verification to avoid connection contention.'

    $phase = 'resource-selection'
    $resourceAssembly = Join-Path $script:taskHostDirectory 'HeyMelody.Resources.dll'
    [void][Reflection.Assembly]::LoadFrom($resourceAssembly)
    # PowerShell cannot directly call the production Span-based cache/parser APIs.
    Add-Type -ReferencedAssemblies $resourceAssembly -TypeDefinition @'
using HeyMelodyWindows.Resources;
public static class ConnectedHeadsetCacheReader {
    public static ResourceCatalog Read(byte[] envelope, byte[] key) =>
        ResourceCatalog.Parse(ResourceCacheCodec.Decrypt(envelope, key), true);
}
'@
    $storage = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'HeyMelody/resources'
    $preferencesPath = Join-Path $storage 'preferences.json'
    $preferences = @{}
    if ([IO.File]::Exists($preferencesPath)) {
        Require (([IO.FileInfo]::new($preferencesPath)).Length -le 1048576) 'The preferences file exceeds its size limit.'
        try {
            $preferences = ConvertFrom-Json -InputObject ([IO.File]::ReadAllText($preferencesPath)) -AsHashtable
            Require ($preferences -is [System.Collections.IDictionary]) 'The preferences root is invalid.'
        }
        catch { $receipt.preferencesFallback = $true; $preferences = @{} }
    }
    $bundleStore = [HeyMelodyWindows.Resources.BundledResourceStore]::new((Join-Path $script:taskHostDirectory 'ResourceBundles'))
    $catalog = $bundleStore.GetCatalog()
    $invalidCaches = 0
    if (-not $Bundled -and $preferences.ContainsKey('CacheEncryptionKey')) {
        $cacheRoot = Join-Path $storage 'online/heymelody/config'
        if ([IO.Directory]::Exists($cacheRoot)) {
            foreach ($directory in [IO.Directory]::EnumerateDirectories($cacheRoot)) {
                $cacheHash = [IO.Path]::GetFileName($directory)
                if ($cacheHash -notmatch '^[0-9a-fA-F]{64}$') { continue }
                $key = $null
                try {
                    $cachePath = Join-Path $directory 'config.cache'
                    Require (([IO.FileInfo]::new($cachePath)).Length -le 9437184) 'The cache envelope exceeds its size limit.'
                    $key = [Convert]::FromBase64String($preferences.CacheEncryptionKey)
                    Require ($key.Length -eq 32) 'The cache key has an invalid length.'
                    $candidate = [ConnectedHeadsetCacheReader]::Read([IO.File]::ReadAllBytes($cachePath), $key)
                    if ($candidate.Sha256 -ne $cacheHash) { $invalidCaches++; continue }
                    if ($candidate.ContentVersion -gt $catalog.ContentVersion) { $catalog = $candidate }
                } catch { $invalidCaches++ }
                finally { if ($null -ne $key) { [Array]::Clear($key); $key = $null } }
            }
        }
    }
    $preferences = $null
    $receipt.resources = [ordered]@{ contentVersion = $catalog.ContentVersion; isOnline = $catalog.IsOnline;
        sha256 = $catalog.Sha256; bundledOnlyRequested = [bool]$Bundled; invalidCachedCandidates = $invalidCaches }
    $json = [Text.Encoding]::UTF8.GetBytes($catalog.Raw.GetRawText())
    $compressed = [IO.MemoryStream]::new()
    $gzip = [IO.Compression.GZipStream]::new($compressed, [IO.Compression.CompressionLevel]::Optimal, $true)
    try { $gzip.Write($json, 0, $json.Length) } finally { $gzip.Dispose() }
    $catalogGzip = [Convert]::ToBase64String($compressed.ToArray())
    $compressed.Dispose()

    $phase = 'cached-device-enumeration'
    $catalogProcess = Start-OwnedProcess '--catalog-worker'
    $catalogDeadline = [datetime]::UtcNow.AddSeconds(15)
    $catalogOutput = $catalogProcess.StandardOutput.ReadToEndAsync()
    $catalogErrors = $catalogProcess.StandardError.ReadToEndAsync()
    Require ($catalogProcess.WaitForExit(15000) -and $catalogProcess.ExitCode -eq 0) 'Cached Bluetooth enumeration failed or exceeded 15 seconds.'
    $rawCatalog = $catalogOutput.GetAwaiter().GetResult()
    Require ($rawCatalog.Length -le 1048576) 'The device catalog exceeds its size limit.'
    $deviceCandidates = @(ConvertFrom-Json -InputObject $rawCatalog -AsHashtable)
    $normalizedName = $TargetName -replace '\s', ''
    Require (-not [string]::IsNullOrWhiteSpace($normalizedName)) 'A target device name is required.'
    $matches = @($deviceCandidates | Where-Object { ($_.name -replace '\s', '') -eq $normalizedName -and $_.authenticated -and $_.remembered -and $_.connected })
    Require ($matches.Count -eq 1) 'A unique paired, remembered, connected target was not found.'
    $target = $matches[0]
    $address = [ulong]$target.address
    Require ($address -gt 0 -and $address -le 0xFFFFFFFFFFFF) 'The cached device address is invalid.'
    $stableBytes = [Text.Encoding]::ASCII.GetBytes('HeyMelody/BluetoothClassic/v1/' + $address.ToString('X12', [Globalization.CultureInfo]::InvariantCulture))
    $stableKey = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stableBytes)).ToLowerInvariant()
    Stop-OwnedProcess $catalogProcess $catalogDeadline
    $catalogProcess = $null

    $phase = 'worker-initialization'
    $script:worker = Start-OwnedProcess '--worker'
    $workerErrors = $script:worker.StandardError.ReadToEndAsync()
    $workerDeadline = [datetime]::UtcNow.AddSeconds(75)
    $appMarker = 17006004
    Send-WorkerRequest @{ id = 1; operation = 'initialize'; identity = [guid]::NewGuid().ToString('N'); name = $target.name;
        address = $address; catalogGzip = $catalogGzip; resourceRevision = 1; appMarker = $appMarker;
        stableKey = $stableKey; generation = 1 }
    $initial = Await-WorkerResponse 1 $workerDeadline
    $receipt.initialization = [ordered]@{ productId = $initial['productId']; connected = [bool]$initial['connected'];
        supportedModel = [bool]$initial['supportedModel']; controlReady = [bool]$initial['controlReady'] }
    Require ($initial.controlReady -and $initial.supportedModel -and $initial.connected) 'The worker did not initialize verified controls.'
    if ($normalizedName -eq 'OPPOEncoX4') { Require ($initial.productId -eq '06EC10') 'The peer did not report the known Enco X4 product identifier.' }
    $script:lastSnapshot = $initial

    $phase = 'passive-monitoring'
    $windowStart = [datetime]::UtcNow
    Require (($workerDeadline - $windowStart).TotalSeconds -ge 5) 'Insufficient time remains for passive monitoring.'
    $windowDeadline = $windowStart.AddSeconds(5)
    $beforeUpdates = $script:snapshotUpdates
    while ([datetime]::UtcNow -lt $windowDeadline) {
        if ($null -eq $script:pendingLine) { $script:pendingLine = $script:worker.StandardOutput.ReadLineAsync() }
        if ($script:pendingLine.IsCompleted) { [void](Read-WorkerMessage $workerDeadline) }
        else { Start-Sleep -Milliseconds 50 }
        Require (-not $script:worker.HasExited) 'The worker ended during passive monitoring.'
    }
    $receipt.passiveMonitoring = [ordered]@{ durationSeconds = [math]::Round(([datetime]::UtcNow - $windowStart).TotalSeconds, 2);
        receivedSnapshotUpdates = $script:snapshotUpdates - $beforeUpdates; notificationRequired = $false }

    $phase = 'diagnostics'
    Send-WorkerRequest @{ id = 2; operation = 'diagnostics' }
    $diagnostics = Await-WorkerResponse 2 $workerDeadline
    $receipt.applicationSubmissions = [ordered]@{ get = $diagnostics.applicationGetSubmissions; set = $diagnostics.applicationSetSubmissions;
        other = $diagnostics.applicationOtherSubmissions; otherIncludesNotificationCapabilityAndSubscriptions = $true;
        countsAreSubmissionAttempts = $true }
    Require ($diagnostics.applicationSetSubmissions -eq 0) 'A setting command submission was detected.'
    Require ($diagnostics.connected -and $diagnostics.profileVerified -and $script:lastSnapshot.controlReady) 'The verified control connection did not remain available.'
    Require ($diagnostics.applicationGetSubmissions -gt 0 -and $diagnostics.applicationOtherSubmissions -gt 0) 'Initialization did not submit queries and monitoring requests.'
    $receipt.headset = [ordered]@{ productId = $initial['productId']; firmware = $initial['firmware'];
        leftBattery = $script:lastSnapshot['leftBattery']; rightBattery = $script:lastSnapshot['rightBattery']; caseBattery = $script:lastSnapshot['caseBattery'];
        connected = [bool]$diagnostics['connected']; profileVerified = [bool]$diagnostics['profileVerified']; controlReady = [bool]$script:lastSnapshot['controlReady'];
        rawFeatureCount = Count-Values $diagnostics['features'];
        featureSectionCount = Count-Values $script:lastSnapshot['featureSections'];
        customEqualizerCount = Count-Values $diagnostics['customEqualizerIds'];
        canFitTest = [bool]$script:lastSnapshot['canFitTest']; canSetupHearing = [bool]$script:lastSnapshot['canSetupHearing'];
        warningCount = Count-Values $diagnostics['warnings']; receivedSnapshotUpdates = $script:snapshotUpdates }
    $phase = 'shutdown'
    Send-WorkerRequest @{ id = 3; operation = 'shutdown' }
    [void](Await-WorkerResponse 3 $workerDeadline)
    foreach ($artifact in $receipt.artifacts) {
        Require ((Artifact (Join-Path $script:taskHostDirectory $artifact.name)).sha256 -eq $artifact.sha256) 'A Host artifact changed during verification.'
    }
    $receipt.success = $true
} catch {
    $receipt.failurePhase = $phase
    $receipt.failureType = $_.Exception.GetType().Name
    if ($_.Exception.Data.Contains('ConnectedHeadsetVerificationReason')) {
        $receipt.failureReason = $_.Exception.Data['ConnectedHeadsetVerificationReason']
    }
} finally {
    Stop-OwnedProcess $catalogProcess $catalogDeadline
    Stop-OwnedProcess $script:worker $workerDeadline
}
$receipt.completedAtUtc = [datetime]::UtcNow.ToString('o')
$result = ConvertTo-Json -InputObject $receipt -Depth 10
if ($OutputPath -and -not [IO.File]::Exists($OutputPath)) {
    $receiptStream = [IO.FileStream]::new($OutputPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    try { $bytes = [Text.UTF8Encoding]::new($false).GetBytes($result + [Environment]::NewLine); $receiptStream.Write($bytes, 0, $bytes.Length) }
    finally { $receiptStream.Dispose() }
}
$result
if (-not $receipt.success) { exit 1 }
