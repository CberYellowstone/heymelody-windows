#requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('Enable','DriverStatus','Rate','Play')][string]$Action='DriverStatus',
    [ValidateSet(96000,192000)][int]$SampleRate=192000,
    [string]$OutputPath
)
$ErrorActionPreference='Stop'
$project=Join-Path $PSScriptRoot 'src\HeyMelody.Experiments\HeyMelody.Experiments.csproj'
$dotnet=(Get-Command dotnet -CommandType Application -ErrorAction Stop|Select-Object -First 1).Source
$operation=@{Enable='enable-hires';DriverStatus='driver-read';Rate='rate';Play='play'}[$Action]
if([string]::IsNullOrWhiteSpace($OutputPath)){
    $folder=Join-Path $PSScriptRoot 'artifacts'
    $stamp=[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff')
    $OutputPath=Join-Path $folder ("$operation-$stamp.json")
}
$arguments=@('run','--project',$project,'--configuration','Release','--','--operation',$operation,'--output',$OutputPath)
if($Action -in @('Rate','Play')){$arguments+=@('--rate',[string]$SampleRate)}
& $dotnet @arguments
exit $LASTEXITCODE
