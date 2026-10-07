# Source repository

GitHub: https://github.com/CberYellowstone/heymelody-windows (private).

This directory is an independent Git repository. Source, tests, build scripts, app artwork and documentation are versioned. Local APK/MSIX inputs and decompilations in `reference` and `research`, generated packages in `release`, device/capture evidence in `artifacts`, build outputs and Python caches remain local. Research conclusions are versioned under `docs/research`; the supplied binaries and Surface screenshots are not redistributed. The two user-supplied X4 PNGs are versioned under `src/HeyMelody.App/Assets/Devices/EncoX4` and embedded without alteration. The verified Enco Free4 GLB is also versioned under `src/HeyMelody.App/Assets/Devices/EncoFree4`; building and running the app does not require the local `research` directory.

Selected cloud research scripts, their minimal helper closure, READMEs and redacted static contracts are also versioned at their original `research` paths as explicit exceptions to the ignored research tree. Start future device work at the [device adaptation handoff](docs/research/device-adaptation-handoff.md). Its offline acceptance tool creates new reports and a SHA256 retention manifest without overwriting historical evidence. APKs, full vendor configurations, downloaded packages, the gallery and run receipts remain local; a Git clone alone cannot reproduce their contents. Keep or back up those inputs before cleaning ignored directories. The curated tools support acceptance and the two cloud interfaces, while rebuilding the whole legacy gallery still needs the retained local extraction/generation scripts.

Version 0.7.0 verification completed on 2026-10-07: all 338 default checks passed (Protocol 46, Devices 13, Control 19, Daily 62, Probe 23, Host 24, Client 96, App 47, DeviceManager 8). Windows ABI checks and separate native Win32 runs passed: 54 main-window checks and 8 shared-backend lifecycle checks. Published x64 and x86 private workers were executed with an uninitialized command and stdin EOF to verify redirected stdio and exit; no Bluetooth was invoked. All three self-contained architectures passed static verification, including 1759 ZIP paths/sizes, 466 critical hashes, independent backend notices and six embedded PNG matches. Repository cleanup also verifies that manager forced termination preserves the shared backend and that staged dropdown targets survive a control generation change. Logs and the aggregate source-test receipt are under `artifacts/release-070-20261007`; the package and static receipt are under `release/`. Real headset broadcasts, phone concurrency, sleep recovery, tray interaction and ARM64 hardware execution are not verified by these checks.

The current version is 0.7.1. The independently validated Free4 model and interaction have been migrated into `arch/modular-devices`; device control retains the 0.7.0 X4 architecture. A shared tray backend owns Bluetooth monitoring and pushes changes to the Avalonia interface. Closing the main UI leaves the backend running; its right-click menu opens the UI or exits after draft confirmation. There is no login autostart or popup component in this version. It retains the Avalonia and FluentAvalonia interface and a separate `HeyMelody.DeviceManager` executable for a persistent whitelist of paired Classic audio devices. An empty effective whitelist opens the manager before the main window; closing it without a valid selection exits the main UI while the tray stays available. The manager is also available from application settings. Its resizable portrait window uses Mica Alt on supported Windows versions, with an opaque fallback, and has a single Close button. The main window uses centered device cards with theme-colored selection borders. The cards and trailing Add button share a horizontal scrolling strip; overflowing selections are brought as close to the center as the natural content boundaries permit, and manual scrolling is retained. Add opens Windows Bluetooth settings.

The main product view now uses the Free4 GLB through Ava3DControl 12.1.0-preview.16, with dragging, damped release inertia and steady horizontal rotation at 20 degrees per second. List icons retain the X4 PNG. Exact X4 known-name hints select this Free4 appearance before connection; an identity-matched Product ID takes precedence, and UI hints never authorize device control. This change adds no Free4 device-control support. Model loading remains in the App presentation layer. The UI subscribes to RFCOMM notifications and BLE observations through schema-2 full-duplex named pipes. Initialization, reconnect, manual refresh and setter verification still use read-only queries. Per-device revisions and generation checks preserve newer pushes and local EQ edits. `HeyMelody.App`, `HeyMelody.DeviceManager`, `HeyMelody.Client` and `HeyMelody.Contracts` target `net10.0`; the tray host targets `net10.0-windows10.0.19041.0` for Windows event and BLE APIs. Native RFCOMM transport remains in `HeyMelody.Windows`. Protocol, device metadata, control and daily workflows already target `net10.0`. Support for other headphones, macOS/Linux backends and the 2.3 Mbps lossless link is deferred. Previously completed [platform research](docs/research/cross-platform.md) is retained as reference, not a current implementation or support commitment.

Run `Run-3D-Demo.ps1` or double-click `Run-3D-Demo.cmd` from this repository's root to build and open the Free4 model demo with simulated X4 controls. This development entry requires the .NET 10 SDK. A published package's `Start-3D-Demo.cmd` provides the same demo with its bundled runtime; `Start-HeyMelody.cmd` is the normal client entry. The [Free4 record](docs/research/enco-free4-0.7.1.md) distinguishes the pre-migration 259 native checks, 338 offline checks and x64 package from subsequent repository validation.

Build with .NET SDK 10 and restore from the official NuGet source configured in `NuGet.Config`:

```powershell
dotnet build src/HeyMelody.App/HeyMelody.App.csproj -c Release
dotnet build src/HeyMelody.DeviceManager/HeyMelody.DeviceManager.csproj -c Release
dotnet build src/HeyMelody.Host/HeyMelody.Host.csproj -c Release
dotnet run --project tests/HeyMelody.Protocol.Tests/HeyMelody.Protocol.Tests.csproj
dotnet run --project tests/HeyMelody.Devices.Tests/HeyMelody.Devices.Tests.csproj
dotnet run --project tests/HeyMelody.Control.Tests/HeyMelody.Control.Tests.csproj
dotnet run --project tests/HeyMelody.Daily.Tests/HeyMelody.Daily.Tests.csproj
dotnet run --project tests/HeyMelody.Probe.Tests/HeyMelody.Probe.Tests.csproj
dotnet run --project tests/HeyMelody.Host.Tests/HeyMelody.Host.Tests.csproj
dotnet run --project tests/HeyMelody.Client.Tests/HeyMelody.Client.Tests.csproj
dotnet run --project tests/HeyMelody.App.Tests/HeyMelody.App.Tests.csproj
dotnet run --project tests/HeyMelody.DeviceManager.Tests/HeyMelody.DeviceManager.Tests.csproj
dotnet run --project tests/HeyMelody.App.Tests/HeyMelody.App.Tests.csproj -c Release -- --native-smoke
dotnet run --project tests/HeyMelody.App.Tests/HeyMelody.App.Tests.csproj -c Release -- --native-backend-smoke
dotnet run --project tests/HeyMelody.DeviceManager.Tests/HeyMelody.DeviceManager.Tests.csproj -c Release -- --native-smoke
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Build-Client.ps1 -Version 0.7.1
```

`Build-Client.ps1` and the versioned application projects default to 0.7.1; `-Version` can select a future release version. The explicit publish version also determines the assembly version shown in application settings; the former status/version footer has been removed. The script runs nine test executables, then publishes self-contained Windows x64, x86 and ARM64 directories and a combined ZIP by default. Use `-Architectures x64` for an x64-only package named `HeyMelody_Windows_0.7.1_x64.zip`; the combined archive retains the `_AllArchitectures.zip` suffix. App and DeviceManager share each architecture's GUI directory and dependencies; Host stays in its `backend` directory. The interfaces include Avalonia/Skia native assets and package license notices instead of the Windows Desktop runtime. Ava3D's unmodified binary and required license notices are distributed with the App; unused PDBs are excluded from portable packages. An existing output directory or ZIP is rejected: use a new version for another release. `-SkipTests` skips the nine test executables; `-FinalizeOnly` packages an already published directory without testing or publishing and still requires that the ZIP does not exist. The script does not run native window smoke tests, model preview tests or static package verification; run those separately. It never treats an old 0.5.0 hardware receipt as verification of the new release.

The test harness is implemented by executable projects under `tests` and verification scripts under `tools`; there is no separate `harness/` directory. The 0.7.1 default suite contains 338 checks, including shared-backend, notification, BLE, lifecycle and binding coverage:

| Harness | Checks | Actual scope |
| --- | ---: | --- |
| Protocol.Tests | 46 | Packets, capabilities, correlation, sequence allocation, notification layouts and synthetic OPPO advertisements |
| Devices.Tests | 13 | Pinned embedded X4 profile and capability/state projection |
| Control.Tests | 19 | Hi-Res workflow with simulated transport |
| Daily.Tests | 62 | Scoped reads, detached cache, single-attempt writes, continuous receive, subscriptions and notification/GET ordering with simulated transport |
| Probe.Tests | 23 | Read-only query allowlist and session behavior with simulated transport |
| Host.Tests | 24 | Whitelist persistence/filtering, shared pipe subscriptions, isolated workers, generations, pause/resume, BLE fusion and lid events |
| Client.Tests | 96 | Shared pipe reconnection/receipts, push-driven VM edits and lifecycle; historical schema-1 fake-worker failure fixtures retained separately |
| App.Tests | 47 | 20 Skia-rendered screenshots, push bindings, interactions, device-strip/artwork behavior and whitelist startup using in-memory services |
| DeviceManager.Tests | 8 | Selection, refresh/save, catalog pushes, close/cancel, action-bar layout and settings entry using in-memory services |

`HeyMelody.ModelPreview.Tests` is a separate model harness. Its default mode checks motion without opening a window; `--native-smoke` additionally opens real Windows/Ava3D windows to exercise the model, drag direction, inertia, rotation, lifecycle and occlusion. `--native-occlusion` selects the fixed-camera occlusion checks. It writes a new report below the repository's `artifacts` directory and does not connect Bluetooth. These checks are separate from the nine default packaging runners:

```powershell
dotnet run --project tests/HeyMelody.ModelPreview.Tests -r win-x64 -p:SelfContained=false -p:CopyDebugSymbolFilesFromPackages=false
dotnet run --project tests/HeyMelody.ModelPreview.Tests -r win-x64 -p:SelfContained=false -p:CopyDebugSymbolFilesFromPackages=false -- --native-smoke
```

`App.Tests --native-smoke` is a separate Windows run. The 0.6.5 run passed 54 checks of a real Win32 window, geometry, themes, dialogs, image/icon rendering, an eight-device overflowing strip and lifecycle using an in-memory service. It does not start the Bluetooth backend, measure DWM pixels or exercise actual earbud settings. The default App harness writes PNGs and `validation.json` to a new `artifacts/avalonia-ui-*` directory; `--output-dir <path>` selects the directory. Separate manager native modes inspect its DWM backdrop, caption hit regions, resizing, themes and close behavior with mocked devices; the 0.6.3 Close-button fix passed 75 checks each in `--native-smoke` and `--native-alt-f4-smoke`. Those historical runs do not establish execution of every later packaged binary. Script success, simulated transport tests and GUI previews do not establish hardware execution or phone/headset concurrency behavior.

The headless App report is schema 1 and derives its version from the App assembly; its explicit source/mock verification scope does not make it a package execution receipt. Historical reports retain their original fixed version label. Public IPC requests, responses and events use schema 2, separately from report and release manifest schemas. Internal worker messages stay private; the schema-1 stdio adapter is confined to Client.Tests for historical isolated failure fixtures. Read scopes are explicit in shared-backend calls. See [backend architecture](docs/backend-architecture.md) and [Bluetooth research](docs/research/bluetooth-events.md).

Static verification does not connect Bluetooth. Use PowerShell 7 to verify the current package and the embedded X4 images:

```powershell
pwsh -NoProfile -File .\tools\Verify-Release.ps1 -Version 0.7.1 -VerifyDeviceArtwork
```

This checks all nine App/DeviceManager/Host entry assemblies across x64, x86 and ARM64, their versions and executable/managed DLL hashes, runtime dependencies and notices, and the ZIP's paths, sizes and critical content hashes. `-VerifyDeviceArtwork` adds six byte comparisons between the two checked-in PNGs and their embedded resources in each GUI assembly. It uses the tracked App assets, so a fresh clone needs no original `research` files. For 0.6.5, the static report verified 1513 archive paths/sizes and 316 critical content hashes; all nine entry assemblies were version 0.6.5.0, and all six PNG comparisons passed. The local report is `release/HeyMelody_Windows_0.6.5_STATIC-VALIDATION.json`; the build and native logs are under `artifacts/release-065`. Packages and generated reports remain ignored and are not committed.

The Python 3 verifier checks GUI/DeviceManager/Host entry architecture and manifest hashes, runtime/native assets, package notices, the GUI icon and the declared Ava3D dependency when present. Select the architectures actually published; omitting `--architectures` requires all three:

```powershell
python tools/verify_release.py --package release/HeyMelody_Windows_0.7.1 --output artifacts/avalonia-0.7.1-release-static-python.json --architectures x64
```

The Python harness creates a new report and refuses an existing output file. It does not check every managed DLL, embedded artwork or ZIP integrity, and does not run the package. Use the PowerShell verifier for combined three-architecture package checks. The pre-migration x64 preview's separate ZIP/GLB integrity and launch evidence is linked in the Free4 record. Neither static verifier establishes hardware execution or native ARM64 execution.

An optional `--evidence <json>` for the Python verifier must be a schema-2 successful receipt tied to this release version and exact GUI/Host executable hashes; without it the report explicitly records no hardware execution verification. `Build-Client.ps1 -ValidationPath <json>` additionally requires matching DeviceManager executable hashes for every selected architecture. It only validates and copies the supplied receipt; it does not collect new evidence. The PowerShell verifier's static report is not an execution receipt. ARM64 cross-publishing alone does not prove native Bluetooth execution.

When supplied, read-only Bluetooth evidence is checked for product `06EC10`, positive GET submissions and zero SET/other submissions. Live-preview and native-hardware flags come from that receipt; the verifier does not independently run those operations or inspect a screenshot. `tools/build_validation_summary.py` summarizes nine previously recorded stage 3/4 JSON files without accessing a device. Its schema-1 output has no release-version or package-hash binding and cannot serve as current schema-2 release evidence. `tools/phase5_reference_checks.py` performs 12 local APK-resource/profile assertions and records source hashes, rather than executing the client or re-reviewing its behavior; it requires the local `reference` inputs and overwrites its fixed historical report path. These optional research tools, Audio tests and Windows native checks are not part of the nine default packaging runners.

Normal profile tests require only checked-in JSON and the embedded resource; both hashes are verified. Original archive provenance is an explicit additional check:

```powershell
dotnet run --project tests/HeyMelody.Devices.Tests/HeyMelody.Devices.Tests.csproj -- --verify-source reference/OPPO_Enco_X4_v116_LHDC_HiRes_Evidence.zip
```

The daily client, protocol, profiles and control modules build from this repository. The older driver/audio experiment module uses `../manager/src/LhdcManager.Core`; to run those optional experiments, clone the companion private repository into the sibling directory named `manager`:

```powershell
git clone https://github.com/CberYellowstone/lhdc-manager.git ..\manager
```

Research checks that explicitly compare APK or archive inputs need the original local reference material. Generated evidence and binary releases linked in the historical notes are not Git source files. The Surface App is a layout reference; this project does not ship its package, artwork or font resources. High-bitrate/lossless audio negotiation is separate from the earbud control protocol and is not implied by the UI migration.

Current architecture, UI/protocol notes and historical evidence are indexed in [docs/README.md](docs/README.md). Historical reports preserve their original source paths and dates; they do not describe the current file layout or validate later releases.
