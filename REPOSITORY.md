# Source repository

GitHub: https://github.com/CberYellowstone/heymelody-windows (private).

This directory is an independent Git repository. Source, tests, build scripts, release-owned resource bundles and documentation are versioned. Original APK/MSIX inputs and decompilations in `reference` and `research`, generated packages in `release`, device/capture evidence in `artifacts`, build outputs and Python caches remain local. Research conclusions are versioned under `docs/research`; original APKs and Surface screenshots are not redistributed. Source-qualified catalog and artwork bundles under `src/HeyMelody.Resources/Bundles` are built from pinned local inputs by `tools/bundle_resources.py`; ordinary builds and runtime do not require `research`. The two supplied X4 PNGs and verified X3/Free4 models retain their own provenance and exact identity restrictions. Asset rights are described in `packaging/licenses/Extracted-Earphone-Resources-NOTICE.txt`.

Selected cloud research scripts, their minimal helper closure, READMEs and redacted static contracts are also versioned at their original `research` paths as explicit exceptions to the ignored research tree. Start further device work at the [device adaptation handoff](docs/research/device-adaptation-handoff.md). Its acceptance tool creates new reports without overwriting historical evidence. Rebuilding extraction output needs the retained APKs and research runs, while a Git clone contains the prepared runtime bundles. Keep or back up original inputs before cleaning ignored directories.

Version 0.7.0 verification completed on 2026-10-07: all 338 default checks passed (Protocol 46, Devices 13, Control 19, Daily 62, Probe 23, Host 24, Client 96, App 47, DeviceManager 8). Windows ABI checks and separate native Win32 runs passed: 54 main-window checks and 8 shared-backend lifecycle checks. Published x64 and x86 private workers were executed with an uninitialized command and stdin EOF to verify redirected stdio and exit; no Bluetooth was invoked. All three self-contained architectures passed static verification, including 1759 ZIP paths/sizes, 466 critical hashes, independent backend notices and six embedded PNG matches. Repository cleanup also verifies that manager forced termination preserves the shared backend and that staged dropdown targets survive a control generation change. Logs and the aggregate source-test receipt are under `artifacts/release-070-20261007`; the package and static receipt are under `release/`. Real headset broadcasts, phone concurrency, sleep recovery, tray interaction and ARM64 hardware execution are not verified by these checks.

The current version is 0.8.2. A shared tray Host owns Bluetooth and publishes immutable resource-driven snapshots to Avalonia/FluentAvalonia interfaces. Closing the main UI preserves the backend. DeviceManager, FitTest and HearingSetup are independent executables sharing that connection; calibration uses a per-device lease and revision-bound inputs. The selected resource source is strict: HeyMelody is the default, online resources default on, custom keys and the random cache key are local plaintext JSON, and downloaded newer cache remains available when online use is off. Builtin catalogs cover 85 HeyMelody and 141 Wireless Earphones earbud product IDs. These are catalog counts, not a claim of hardware-verified control for every entry; unknown codecs, absent SPP and external-only control routes are disabled.

The product view selects pictures, exact product/color model packages or Lottie animations from the selected source. X4 keeps its own PNG appearance, and verified X3/Free4 GLBs are not assigned to other models. Ava3DControl 12.1.0-preview.16 retains interaction and asset-specific material fixes; SkiaSharp.Skottie renders animations with package-local images. Installed resources are immutable; new online packages are validated and atomically published before retired online versions are deleted after active leases release. ANC uses round mode selectors and child options; EQ band count and capacity are device-specific. Public full-duplex named-pipe IPC is schema 3. The UI, resource and calibration layers target `net10.0`; Host targets `net10.0-windows10.0.19041.0` for Windows events and BLE, with native RFCOMM in `HeyMelody.Windows`. macOS/Linux backends and the 2.3 Mbps lossless link remain deferred. [Platform research](docs/research/cross-platform.md) is reference material, not a support commitment.

Hold the main device-strip `+` button for two seconds to add a session-only layout demo; short clicks still open Windows Bluetooth settings. The demo uses the verified Free4 Dynaudio model (`06C010`, request color 3) and all implemented main-page controls, with changes confined to local simulated state. Closing the main window removes it even when the tray Host remains running. The embedded original GLB SHA256 is `7f85c66d397ee45163246ff745c37e52a70059e739be0af2283a30bfb689c2f6`, with 20 meshes and 94,927 triangles; its exact verified material uses in-memory opaque/depth-write rendering. Acquisition and identity evidence are in the [Dynaudio model report](docs/research/enco-free4-dynaudio-model.md).

Run `Run-3D-Demo.ps1` or double-click `Run-3D-Demo.cmd` from this repository's root to build and open the same layout demo without a real-device whitelist. This development entry requires the .NET 10 SDK. A published package's `Start-3D-Demo.cmd` provides the same demo with its bundled runtime; `Start-HeyMelody.cmd` is the normal client entry and retains its existing whitelist startup requirements. The [Free4 record](docs/research/enco-free4-0.7.1.md) distinguishes the pre-migration 259 native checks, 338 offline checks and x64 package from subsequent repository validation.

Build with .NET SDK 10 and restore from the official NuGet source configured in `NuGet.Config`:

```powershell
dotnet build src/HeyMelody.App/HeyMelody.App.csproj -c Release
dotnet build src/HeyMelody.DeviceManager/HeyMelody.DeviceManager.csproj -c Release
dotnet build src/HeyMelody.FitTest/HeyMelody.FitTest.csproj -c Release
dotnet build src/HeyMelody.HearingSetup/HeyMelody.HearingSetup.csproj -c Release
dotnet build src/HeyMelody.Host/HeyMelody.Host.csproj -c Release
dotnet run --project tests/HeyMelody.Protocol.Tests/HeyMelody.Protocol.Tests.csproj
dotnet run --project tests/HeyMelody.Resources.Tests/HeyMelody.Resources.Tests.csproj
dotnet run --project tests/HeyMelody.Calibration.Tests/HeyMelody.Calibration.Tests.csproj
dotnet run --project tests/HeyMelody.Calibration.UI.Tests/HeyMelody.Calibration.UI.Tests.csproj
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
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Build-Client.ps1 -Version 0.8.2
```

`Build-Client.ps1` defaults to 0.8.2; `-Version` selects the assembly and package version. It runs thirteen test executables, including Windows.OfflineChecks, Resources, Calibration and Calibration.UI, then publishes self-contained Windows x64, x86 and ARM64 directories and a combined ZIP. `-Architectures x64` produces `HeyMelody_Windows_0.8.2_x64.zip`; the combined suffix is `_AllArchitectures.zip`. App, DeviceManager, FitTest and HearingSetup share each GUI directory, exact runtime and dependencies; Host stays in `backend`. Both directories include ResourceBundles, resource assemblies and independent notices. Ava3D and Skottie packages and extracted-resource notices are preserved. PDBs and Windows Desktop runtime dependencies are excluded. Existing output is rejected. `-SkipTests` omits the thirteen runners; `-FinalizeOnly` packages already published output without testing or rebuilding. Native window/model checks and static verification run separately; prior hardware receipts are not inherited.

The native `HeyMelody.Windows.OfflineChecks` runner also checks that every eligible profile in both bundled catalogs uses one of the two full OPPO control UUIDs accepted by the native transport. These checks call input validation and the bounded SDP parser without native Bluetooth calls.

The test harness consists of executable projects under `tests` and scripts under `tools`. Resources.Tests validates both complete catalog bundles, source/version selection, authentication, plaintext preferences, encrypted-cache rejection, offline reuse and leased art cleanup using a fake HTTP handler. Calibration.Tests covers protocol layouts and calibration workflows with simulated transport. The following counts describe the historical 0.7.1 baseline; current runners report their own expanded totals:

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

`HeyMelody.ModelPreview.Tests` is a separate model harness. Its default mode checks motion without opening a window; `--native-smoke` additionally opens real Windows/Ava3D windows to exercise the model, drag direction, inertia, rotation, lifecycle and occlusion. `--native-occlusion` selects the fixed-camera occlusion checks. It writes a new report below the repository's `artifacts` directory and does not connect Bluetooth. These checks are separate from the thirteen default packaging runners:

```powershell
dotnet run --project tests/HeyMelody.ModelPreview.Tests -r win-x64 -p:SelfContained=false -p:CopyDebugSymbolFilesFromPackages=false
dotnet run --project tests/HeyMelody.ModelPreview.Tests -r win-x64 -p:SelfContained=false -p:CopyDebugSymbolFilesFromPackages=false -- --native-smoke
```

`App.Tests --native-smoke` is a separate Windows run. The 0.6.5 run passed 54 checks of a real Win32 window, geometry, themes, dialogs, image/icon rendering, an eight-device overflowing strip and lifecycle using an in-memory service. It does not start the Bluetooth backend, measure DWM pixels or exercise actual earbud settings. The default App harness writes PNGs and `validation.json` to a new `artifacts/avalonia-ui-*` directory; `--output-dir <path>` selects the directory. Separate manager native modes inspect its DWM backdrop, caption hit regions, resizing, themes and close behavior with mocked devices; the 0.6.3 Close-button fix passed 75 checks each in `--native-smoke` and `--native-alt-f4-smoke`. Those historical runs do not establish execution of every later packaged binary. Script success, simulated transport tests and GUI previews do not establish hardware execution or phone/headset concurrency behavior.

The headless App report is schema 1 and derives its version from the App assembly; source/mock verification is not a package execution receipt. Historical reports retain their original labels. Public IPC is schema 3, separately from report and release manifest schemas; internal worker messages remain private. Read scopes are explicit. See [backend architecture](docs/backend-architecture.md) and [Bluetooth research](docs/research/bluetooth-events.md).

Static verification does not connect Bluetooth. Use PowerShell 7 to verify the current package and the embedded X4 images:

```powershell
pwsh -NoProfile -File .\tools\Verify-Release.ps1 -Version 0.8.2 -VerifyDeviceArtwork
```

This checks fifteen App/DeviceManager/FitTest/HearingSetup/Host entry assemblies across three architectures, versions and hashes, exact shared runtimes, dependencies, Skottie and extracted-resource notices, all indexed offline assets, and ZIP paths/sizes/critical hashes. `-VerifyDeviceArtwork` compares both X4 source PNGs and, from 0.8.2, the independently verified Dynaudio GLB against every embedded GUI resource, for nine complete-byte comparisons. A fresh clone needs no original research inputs. For comparison, the historical 0.6.5 receipt checked 1513 paths/sizes and 316 critical hashes for its nine entry assemblies; it does not validate 0.8.0. Generated packages and reports remain ignored.

The Python 3 verifier checks GUI/DeviceManager/Host entry architecture and manifest hashes, runtime/native assets, package notices, the GUI icon and the declared Ava3D dependency when present. Select the architectures actually published; omitting `--architectures` requires all three:

```powershell
python tools/verify_release.py --package release/HeyMelody_Windows_0.8.2 --output artifacts/avalonia-0.8.2-release-static-python.json --architectures x64
```

The Python harness creates a new report and refuses an existing output file. It does not check every managed DLL, embedded artwork or ZIP integrity, and does not run the package. Use the PowerShell verifier for combined three-architecture package checks. The pre-migration x64 preview's separate ZIP/GLB integrity and launch evidence is linked in the Free4 record. Neither static verifier establishes hardware execution or native ARM64 execution.

Optional schema-2 successful execution receipts must match this release version and the exact GUI, DeviceManager, FitTest, HearingSetup and Host executable hashes for each asserted architecture. `Build-Client.ps1 -ValidationPath` validates and copies receipts; it does not collect evidence. Static reports are not execution receipts, and ARM64 cross-publishing does not prove native Bluetooth execution.

When supplied, historical read-only Bluetooth evidence is checked for product `06EC10`, positive GET submissions and zero SET/other submissions. Receipt flags do not establish execution of new calibration or other models. `tools/build_validation_summary.py` summarizes nine historical stage 3/4 files; its unbound schema-1 output cannot validate the current package. `tools/phase5_reference_checks.py` performs local APK/profile assertions, requires original reference inputs and writes its historical report. These optional research tools, Audio tests and Windows native checks are separate from the thirteen packaging runners.

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
