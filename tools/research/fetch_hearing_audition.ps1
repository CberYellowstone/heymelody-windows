[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../../research/hearing-preview-20261010/audio'),
    [string]$Proxy = 'http://127.0.0.1:59991',
    [string]$Ffmpeg = 'ffmpeg.exe'
)
$ErrorActionPreference = 'Stop'
$output = [IO.Path]::GetFullPath($OutputDirectory)
$null = New-Item -ItemType Directory -Path $output -Force
$url = 'https://iot-heythings-cn.heytapimage.com/heythings-static/admin-resource/generalFiles/1688470726541.zip'
$zipHash = '6d5dfbd9e052cd176d34fc93b8554aea754c89e2242ae3cfe22e16840f22d2ca'
$mp3Hash = '79511df861ae29ef4e5ecb333a6112ff82fe0d71fe736fca62fa1257c82af7ce'
$zipPath = Join-Path $output 'android_all_all_all_2.zip'
$mp3Path = Join-Path $output 'hearing_enhancement_audition.mp3'
$wavPath = Join-Path $output 'hearing_enhancement_audition.wav'
function Assert-Hash([string]$Path, [string]$Expected) {
    if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $Expected) {
        throw "Source hash mismatch: $Path"
    }
}
$ranges = @()
if (-not (Test-Path -LiteralPath $zipPath)) {
    $handler = [Net.Http.HttpClientHandler]::new()
    if ($Proxy) { $handler.Proxy = [Net.WebProxy]::new($Proxy); $handler.UseProxy = $true }
    $client = [Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromSeconds(45)
    $archive = [IO.MemoryStream]::new()
    try {
        $length = 5019585
        for ($start = 0; $start -lt $length; $start += 262144) {
            $end = [Math]::Min($length - 1, $start + 262143)
            $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get, $url)
            $request.Headers.Range = [Net.Http.Headers.RangeHeaderValue]::new($start, $end)
            $response = $null
            try {
                $response = $client.SendAsync($request).GetAwaiter().GetResult()
                $response.EnsureSuccessStatusCode() | Out-Null
                $range = $response.Content.Headers.ContentRange
                $bytes = $response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
                if ([int]$response.StatusCode -ne 206 -or $range.From -ne $start -or $range.To -ne $end -or $range.Length -ne $length -or $bytes.Length -ne ($end - $start + 1)) {
                    throw "Unexpected HTTP range at $start"
                }
                $archive.Write($bytes, 0, $bytes.Length)
                $ranges += [ordered]@{ from = $start; to = $end; status = [int]$response.StatusCode; bytes = $bytes.Length }
                Write-Host "Fetched $($end + 1)/$length bytes"
            } finally { if ($response) { $response.Dispose() }; $request.Dispose() }
        }
        [IO.File]::WriteAllBytes($zipPath, $archive.ToArray())
    } finally { $archive.Dispose(); $client.Dispose(); $handler.Dispose() }
}
Assert-Hash $zipPath $zipHash
$archive = [IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    $entry = $archive.GetEntry('res/raw/hearing_enhancement_audition.mp3')
    if (-not $entry -or $entry.Length -ne 560134) { throw 'Official audition MP3 is absent or changed.' }
    if (-not (Test-Path -LiteralPath $mp3Path)) {
        $source = $entry.Open()
        $destination = [IO.File]::Open($mp3Path, [IO.FileMode]::CreateNew)
        try { $source.CopyTo($destination) } finally { $destination.Dispose(); $source.Dispose() }
    }
} finally { $archive.Dispose() }
Assert-Hash $mp3Path $mp3Hash
$ffmpegVersion = @(& $Ffmpeg -version)[0]
if ($LASTEXITCODE -ne 0) { throw 'ffmpeg is unavailable.' }
if (-not (Test-Path -LiteralPath $wavPath)) {
    & $Ffmpeg -nostdin -hide_banner -loglevel error -i $mp3Path -map_metadata -1 -ar 48000 -ac 2 -c:a pcm_s16le -fflags +bitexact -flags:a +bitexact $wavPath
    if ($LASTEXITCODE -ne 0) { throw 'Audition conversion failed.' }
}
$receiptPath = Join-Path $output ('acquisition-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '.json')
[ordered]@{
    schemaVersion = 1; obtainedAtUtc = [DateTime]::UtcNow.ToString('o')
    sourceUrl = $url; role = 'Official Android Golden Hearing module 2 audition music'
    archive = [ordered]@{ filename = [IO.Path]::GetFileName($zipPath); bytes = (Get-Item -LiteralPath $zipPath).Length; sha256 = $zipHash }
    original = [ordered]@{ member = 'res/raw/hearing_enhancement_audition.mp3'; bytes = 560134; sha256 = $mp3Hash }
    conversion = [ordered]@{ toolVersion = $ffmpegVersion; arguments = '-nostdin -hide_banner -loglevel error -i hearing_enhancement_audition.mp3 -map_metadata -1 -ar 48000 -ac 2 -c:a pcm_s16le -fflags +bitexact -flags:a +bitexact hearing_enhancement_audition.wav'; sampleRateHz = 48000; channels = 2; format = 'PCM16'; bytes = (Get-Item -LiteralPath $wavPath).Length; sha256 = (Get-FileHash -LiteralPath $wavPath -Algorithm SHA256).Hash.ToLowerInvariant() }
    httpRanges = @($ranges)
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $receiptPath -Encoding utf8
Write-Host $receiptPath
