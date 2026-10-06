# Source repository

GitHub: https://github.com/CberYellowstone/heymelody-windows (private).

This directory is an independent Git repository. Source, tests, build scripts, app artwork and documentation are versioned. Local APK/MSIX inputs and decompilations in `reference` and `research`, generated packages in `release`, device/capture evidence in `artifacts`, build outputs and Python caches remain local. Research conclusions are versioned under `docs/research`; the supplied binaries and Surface screenshots are not redistributed.

The 0.6.0 scope is the migration of existing OPPO Enco X4 controls on Windows to Avalonia and FluentAvalonia. `HeyMelody.App`, `HeyMelody.Client` and `HeyMelody.Contracts` target `net10.0`; the Bluetooth host remains `net10.0-windows`. Protocol, device metadata, control and daily workflows already target `net10.0`. Support for other headphones, macOS/Linux backends and the 2.3 Mbps lossless link is deferred. Previously completed [platform research](docs/research/cross-platform.md) is retained as reference, not a current implementation or support commitment.

Build with .NET SDK 10 and restore from the official NuGet source configured in `NuGet.Config`:

```powershell
dotnet build src/HeyMelody.App/HeyMelody.App.csproj -c Release
dotnet build src/HeyMelody.Host/HeyMelody.Host.csproj -c Release
dotnet run --project tests/HeyMelody.Protocol.Tests/HeyMelody.Protocol.Tests.csproj
dotnet run --project tests/HeyMelody.Devices.Tests/HeyMelody.Devices.Tests.csproj
dotnet run --project tests/HeyMelody.Control.Tests/HeyMelody.Control.Tests.csproj
dotnet run --project tests/HeyMelody.Daily.Tests/HeyMelody.Daily.Tests.csproj
dotnet run --project tests/HeyMelody.Probe.Tests/HeyMelody.Probe.Tests.csproj
dotnet run --project tests/HeyMelody.Client.Tests/HeyMelody.Client.Tests.csproj
dotnet run --project tests/HeyMelody.App.Tests/HeyMelody.App.Tests.csproj
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Build-Client.ps1
```

`Build-Client.ps1` defaults to 0.6.0 and publishes self-contained Windows x64, x86 and ARM64 directories. App and host stay in separate directories; the app includes Avalonia/Skia native assets and package license notices instead of the Windows Desktop runtime. Use `-Version 0.6.1` for a distinct output version. The script never treats an old 0.5.0 hardware receipt as verification of the new release.

Static verification needs Python 3 and does not connect Bluetooth:

```powershell
python tools/verify_release.py --package release/HeyMelody_Windows_0.6.0 --output artifacts/avalonia-release-static.json
```

An optional `--evidence <json>` must be a newly recorded receipt tied to this release version and exact executable hashes; without it the report explicitly records no hardware execution verification. ARM64 cross-publishing alone does not prove native Bluetooth execution.

Normal profile tests require only checked-in JSON and the embedded resource; both hashes are verified. Original archive provenance is an explicit additional check:

```powershell
dotnet run --project tests/HeyMelody.Devices.Tests/HeyMelody.Devices.Tests.csproj -- --verify-source reference/OPPO_Enco_X4_v116_LHDC_HiRes_Evidence.zip
```

The daily client, protocol, profiles and control modules build from this repository. The older driver/audio experiment module uses `../manager/src/LhdcManager.Core`; to run those optional experiments, clone the companion private repository into the sibling directory named `manager`:

```powershell
git clone https://github.com/CberYellowstone/lhdc-manager.git ..\manager
```

Research checks that explicitly compare APK or archive inputs need the original local reference material. Generated evidence and binary releases linked in the historical notes are not Git source files. The Surface App is a layout reference; this project does not ship its package, artwork or font resources. High-bitrate/lossless audio negotiation is separate from the earbud control protocol and is not implied by the UI migration.
