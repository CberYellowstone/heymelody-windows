[CmdletBinding()]
param([switch]$Rebuild)

$ErrorActionPreference = 'Stop'
$demoRoot = $PSScriptRoot
$demoProject = Join-Path $demoRoot 'src/HeyMelody.App/HeyMelody.App.csproj'
$demoExecutable = Join-Path $demoRoot 'src/HeyMelody.App/bin/Debug/net10.0/win-x64/HeyMelody.exe'

try {
    if ($env:OS -ne 'Windows_NT') {
        throw 'This launcher requires Windows.'
    }

    $dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -eq $dotnetCommand) {
        throw 'Install the .NET 10 SDK to build the demo, then run this script again.'
    }

    # Always check the current sources before launching an existing executable.
    $buildArguments = @('build', $demoProject, '--configuration', 'Debug', '--runtime', 'win-x64',
        '-p:SelfContained=false', '-p:CopyDebugSymbolFilesFromPackages=false',
        '-p:CreateHardLinksForCopyLocalIfPossible=true',
        '-p:CreateHardLinksForCopyFilesToOutputDirectoryIfPossible=true', '--nologo')
    $demoPackageCache = Join-Path $demoRoot 'artifacts/nuget-cache'
    $buildArguments += '-p:RestorePackagesPath=' + $demoPackageCache
    $existingPackageCache = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget/packages'
    if (Test-Path -LiteralPath $existingPackageCache -PathType Container) {
        $buildArguments += '-p:RestoreFallbackFolders=' + $existingPackageCache
    }
    $restoreConfig = Join-Path $demoRoot 'NuGet.Config'
    if (Test-Path -LiteralPath $restoreConfig -PathType Leaf) {
        $buildArguments += '-p:RestoreConfigFile=' + $restoreConfig
    }
    if ($Rebuild) { $buildArguments += '--no-incremental' }
    & $dotnetCommand.Source @buildArguments
    if ($LASTEXITCODE -ne 0) { throw 'Demo build failed. See the build output above.' }

    if (-not (Test-Path -LiteralPath $demoExecutable -PathType Leaf)) {
        throw 'The build did not produce HeyMelody.exe.'
    }
    $demoProcess = Start-Process -FilePath $demoExecutable -ArgumentList '--3d-demo' `
        -WorkingDirectory $demoRoot -WindowStyle Normal -PassThru
    Write-Host ('Layout demo started (PID ' + $demoProcess.Id + '). Enco Free4 Dynaudio artwork and all controls use session-only simulated state.')
} catch {
    Write-Error -Message $_.Exception.Message -ErrorAction Continue
    exit 1
}
