[CmdletBinding()]
param(
    [switch]$List,
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src\HeyMelody.Probe\HeyMelody.Probe.csproj'
$dotnet = (Get-Command dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$arguments = @('run', '--project', $project, '--configuration', 'Release', '--')
if ($List) {
    $arguments += '--list'
} else {
    $arguments += '--probe'
    if ([string]::IsNullOrWhiteSpace($OutputPath)) {
        $folder = Join-Path $PSScriptRoot 'artifacts'
        $stamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff')
        $OutputPath = Join-Path $folder ("x4-readonly-$stamp.json")
    }
    $arguments += @('--output', $OutputPath)
}

& $dotnet @arguments
exit $LASTEXITCODE

