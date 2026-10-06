# Source repository

GitHub: https://github.com/CberYellowstone/heymelody-windows (private).

This directory is an independent Git repository. Source, tests, build scripts, app artwork and documentation are versioned. Local APK/decompilation inputs in `reference`, generated packages in `release`, device/capture evidence in `artifacts`, build outputs and Python caches remain local. Existing local files are preserved by `.gitignore`.

Build the Windows daily client with .NET SDK10:

```powershell
dotnet build src/HeyMelody.App/HeyMelody.App.csproj -c Release
dotnet build src/HeyMelody.Host/HeyMelody.Host.csproj -c Release
dotnet run --project tests/HeyMelody.Daily.Tests/HeyMelody.Daily.Tests.csproj
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Build-Client.ps1
```

The daily client, protocol, profiles and control modules build from this repository. The older driver/audio experiment module uses `../manager/src/LhdcManager.Core`; to run those optional experiments, clone the companion private repository into the sibling directory named `manager`:

```powershell
git clone https://github.com/CberYellowstone/lhdc-manager.git ..\manager
```

Research checks that explicitly compare APK or archive inputs need the original local reference material. Generated evidence and binary releases linked in the historical notes are not Git source files.
