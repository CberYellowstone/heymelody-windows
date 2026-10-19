param(
    [string]$OutputDirectory,
    [Parameter(Mandatory)][string]$RuntimeNodeModules,
    [Parameter(Mandatory)][string]$Python,
    [string]$AppScreenshots,
    [string]$CalibrationScreenshots,
    [string]$ManagerScreenshots,
    [string]$VerificationReceipt,
    [string]$PreviousWorkbook,
    [string]$PreviousData
)
$ErrorActionPreference = 'Stop'
$auditRepo = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$auditRun = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0,8)
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $auditRepo "outputs/ui-text-audit-$auditRun" }
$auditOutput = [System.IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $auditOutput) { throw '输出目录已存在。请使用新目录，保留已审阅工作簿。' }
if (([bool]$PreviousWorkbook) -ne ([bool]$PreviousData)) { throw 'PreviousWorkbook 与 PreviousData 必须同时提供。' }
if (-not (Test-Path -LiteralPath (Join-Path $RuntimeNodeModules '@oai/artifact-tool/package.json'))) { throw '缺少 artifact-tool 运行时依赖。' }
if (-not (Test-Path -LiteralPath $Python)) { throw 'Python 路径不存在。' }
New-Item -ItemType Directory -Path $auditOutput | Out-Null
$auditArchive = Join-Path $auditRepo "research/ui-text-audit/$auditRun"
New-Item -ItemType Directory -Path $auditArchive | Out-Null
function Invoke-AuditStep([string]$Name, [scriptblock]$Action) {
    $auditLog = Join-Path $auditArchive "$Name.log"
    & $Action *> $auditLog
    if ($LASTEXITCODE -ne 0) { throw "$Name 失败。完整日志：$auditLog" }
    Write-Host "$Name 完成"
}
Push-Location $auditRepo
try {
    $auditBaseline = Join-Path $auditArchive 'baseline.json'
    Invoke-AuditStep 'extract' { dotnet run --project (Join-Path $PSScriptRoot 'Extractor/Extractor.csproj') -c Release -- $auditRepo $auditBaseline }
    $auditPrevious = ''
    if ($PreviousWorkbook) {
        $auditPrevious = Join-Path $auditArchive 'previous-reviews.json'
        Invoke-AuditStep 'previous-reviews' { & $Python (Join-Path $PSScriptRoot 'workbook_io.py') reviews $PreviousWorkbook $PreviousData $auditPrevious }
    }
    Invoke-AuditStep 'prepare' { node (Join-Path $PSScriptRoot 'prepare.mjs') $auditBaseline $auditOutput $AppScreenshots $CalibrationScreenshots $ManagerScreenshots $auditPrevious $VerificationReceipt }
    Invoke-AuditStep 'workbook' { node (Join-Path $PSScriptRoot 'build.mjs') $auditOutput $RuntimeNodeModules }
    Invoke-AuditStep 'validate' { & $Python (Join-Path $PSScriptRoot 'workbook_io.py') finalize $auditOutput }
    Copy-Item -LiteralPath (Join-Path $auditOutput 'audit-data.json'), (Join-Path $auditOutput 'validation.json') -Destination $auditArchive
    foreach ($auditSource in Get-ChildItem -LiteralPath $PSScriptRoot -File -Recurse | Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }) {
        $auditRelative = [System.IO.Path]::GetRelativePath($PSScriptRoot, $auditSource.FullName)
        $auditTarget = Join-Path $auditArchive (Join-Path 'tool-source' $auditRelative)
        New-Item -ItemType Directory -Path (Split-Path -Parent $auditTarget) -Force | Out-Null
        Copy-Item -LiteralPath $auditSource.FullName -Destination $auditTarget
    }
    Write-Host (Join-Path $auditOutput '界面文本审计.xlsx')
}
finally { Pop-Location }
