# Source repository

GitHub: https://github.com/CberYellowstone/heymelody-windows (private).

This directory is an independent Git repository. Source, tests, build scripts, app artwork and documentation are versioned. Local APK/MSIX inputs and decompilations in `reference` and `research`, generated packages in `release`, device/capture evidence in `artifacts`, build outputs and Python caches remain local. Research conclusions are versioned under `docs/research`; the supplied binaries and Surface screenshots are not redistributed.

The current release is 0.6.1. It retains the Avalonia and FluentAvalonia migration introduced in 0.6.0 and improves refresh interaction: segmented read-only polling yields between sections to queued user submissions, while local choices and EQ editing remain available. `HeyMelody.App`, `HeyMelody.Client` and `HeyMelody.Contracts` target `net10.0`; the Bluetooth host remains `net10.0-windows`. Protocol, device metadata, control and daily workflows already target `net10.0`. Support for other headphones, macOS/Linux backends and the 2.3 Mbps lossless link is deferred. Previously completed [platform research](docs/research/cross-platform.md) is retained as reference, not a current implementation or support commitment.

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
dotnet run --project tests/HeyMelody.App.Tests/HeyMelody.App.Tests.csproj -c Release -- --native-smoke
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Build-Client.ps1 -Version 0.6.1
```

`Build-Client.ps1` and the project files still default to 0.6.0; pass `-Version 0.6.1` to publish the current release. The explicit publish version also determines the assembly version shown in the footer and application settings. The script runs seven test executables, then publishes self-contained Windows x64, x86 and ARM64 directories and a combined ZIP. App and host stay in separate directories; the app includes Avalonia/Skia native assets and package license notices instead of the Windows Desktop runtime. An existing output directory or ZIP is rejected: use a new version for another release. `-SkipTests` skips the seven test executables; `-FinalizeOnly` packages an already published directory without testing or publishing and still requires that the ZIP does not exist. The script does not run native window smoke tests or static package verification; run those separately. It never treats an old 0.5.0 hardware receipt as verification of the new release.

The test harness is implemented by executable projects under `tests` and verification scripts under `tools`; there is no separate `harness/` directory. The current release passed all 228 checks in the seven default test executables:

| Harness | Checks | Actual scope |
| --- | ---: | --- |
| Protocol.Tests | 37 | Packet parsing, capability maps, response correlation and sequence allocation |
| Devices.Tests | 13 | Pinned embedded X4 profile and capability/state projection |
| Control.Tests | 19 | Hi-Res workflow with simulated transport |
| Daily.Tests | 52 | Segmented reads, detached cache, single-attempt backend writes and typed results with simulated transport |
| Probe.Tests | 23 | Read-only query allowlist and session behavior with simulated transport |
| Client.Tests | 60 | Real child-process fake RPC workers, VM queues, edit versions, retry/alert/cancellation and dialog ownership |
| App.Tests | 24 | 20 Skia-rendered state/theme/size screenshots and four interaction cases using in-memory services |

`App.Tests --native-smoke` is a separate Windows run with 38 checks of a real Win32 window, geometry, themes, dialogs and lifecycle using an in-memory service. It does not start the Bluetooth backend, measure DWM pixels or exercise actual earbud settings. The default App harness writes PNGs and `validation.json` to a new `artifacts/avalonia-ui-*` directory; `--output-dir <path>` selects the directory. Script success, simulated transport tests and GUI previews do not establish hardware execution or phone/headset concurrency behavior.

The headless App report is schema 1 and still writes a fixed `version: "0.6.0"`; it is a source-test report, not a package execution receipt. IPC requests/responses also use schema 1, but are a separate format from this report and schema-2 release evidence. `RefreshScope` is an additive IPC field; omitting it keeps a full refresh.

Static verification needs Python 3 and does not connect Bluetooth:

```powershell
python tools/verify_release.py --package release/HeyMelody_Windows_0.6.1 --output artifacts/avalonia-0.6.1-release-static.json
```

The static harness checks each architecture's GUI/Host PE headers, runtime and native rendering assets, self-contained dependencies, license notices, icon payloads and manifest EXE hashes. It creates a new report and refuses an existing output file. It does not run the package or check ZIP CRCs. Its EXE hashes do not bind the contents of every managed DLL, so this report alone cannot prove that all business assemblies contain the latest source changes; inspect the actual published assemblies and verify the archive separately. The 0.6.1 package was additionally checked for ZIP CRCs, matching file lists and matching critical managed assemblies/documentation, and its x64/x86 executables rendered offline previews showing 0.6.1.

An optional `--evidence <json>` must be a schema-2 successful receipt tied to this release version and exact GUI/Host executable hashes; without it the report explicitly records no hardware execution verification. `Build-Client.ps1 -ValidationPath <json>` only validates and copies a supplied receipt; it does not collect new evidence. ARM64 cross-publishing alone does not prove native Bluetooth execution.

When supplied, read-only Bluetooth evidence is checked for product `06EC10`, positive GET submissions and zero SET/other submissions. Live-preview and native-hardware flags come from that receipt; the verifier does not independently run those operations or inspect a screenshot. `tools/build_validation_summary.py` summarizes nine previously recorded stage 3/4 JSON files without accessing a device. Its schema-1 output has no release-version or package-hash binding and cannot serve as current schema-2 release evidence. `tools/phase5_reference_checks.py` performs 12 local APK-resource/profile assertions and records source hashes, rather than executing the client or re-reviewing its behavior; it requires the local `reference` inputs and overwrites its fixed historical report path. These optional research tools, Audio tests and Windows offline checks are not part of the seven default packaging runners.

Normal profile tests require only checked-in JSON and the embedded resource; both hashes are verified. Original archive provenance is an explicit additional check:

```powershell
dotnet run --project tests/HeyMelody.Devices.Tests/HeyMelody.Devices.Tests.csproj -- --verify-source reference/OPPO_Enco_X4_v116_LHDC_HiRes_Evidence.zip
```

The daily client, protocol, profiles and control modules build from this repository. The older driver/audio experiment module uses `../manager/src/LhdcManager.Core`; to run those optional experiments, clone the companion private repository into the sibling directory named `manager`:

```powershell
git clone https://github.com/CberYellowstone/lhdc-manager.git ..\manager
```

Research checks that explicitly compare APK or archive inputs need the original local reference material. Generated evidence and binary releases linked in the historical notes are not Git source files. The Surface App is a layout reference; this project does not ship its package, artwork or font resources. High-bitrate/lossless audio negotiation is separate from the earbud control protocol and is not implied by the UI migration.
