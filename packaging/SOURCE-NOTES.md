# Source and provenance

Client source is in `src`, with offline checks in `tests` and reproducible packaging in `Build-Client.ps1`. The client uses Avalonia, FluentAvalonia and CommunityToolkit.Mvvm. The Windows Bluetooth host stays separate from the cross-platform interface. The earbud icon was created for this client by `tools/create_app_icon.py`. Surface App screenshots and its package informed layout research; Surface artwork, fonts and package binaries are not included.

This release migrates the existing Windows Enco X4 interface only. Other headphone models, macOS/Linux Bluetooth backends and the OPPO 2.3 Mbps lossless link are deferred; retained research notes are not release support claims.

Protocol fields, capability numbers and X4 model data were checked against the locally supplied HeyMelody APK and firmware evidence bundle. The exact embedded X4 asset is pinned by SHA256 in `HeyMelody.Devices/X4Profile.cs`. Details and source locations are documented in `phase5-protocol-evidence.md` and `daily-notes.md`.

The locally supplied OppoPodsManager reference (GPL-3.0-or-later) informed the research. Current parsing and workflows were implemented in the project's own protocol/control layers and independently checked against the Android source; this is not a clean-room research claim. This package does not contain the reference application's assemblies, artwork, fonts or APK.

The self-contained .NET runtimes include `LICENSE.txt` and `THIRD-PARTY-NOTICES.txt` in each architecture directory and its separate `backend` directory, copied from the exact official runtime packages used for publishing. The Avalonia interface no longer depends on the Windows Desktop runtime.

Each interface directory contains `PACKAGE-NOTICES.json` and `licenses/`. Packaging reads the resolved NuGet assets, records exact package IDs and versions, preserves NuGet license expressions/URLs and copies available license and notice files plus the package metadata. This includes native rendering dependencies such as SkiaSharp, HarfBuzzSharp and Avalonia.Native when present in the resolved dependency graph. The manifest is generated from installed packages, rather than a handwritten list that may miss an updated dependency. Build-only transitive package metadata can also appear in this notice inventory. Package restoration uses the official nuget.org feed.

Some MIT packages only include a license expression in NuGet metadata. Supplemental full upstream license texts for Avalonia, FluentAvalonia, MicroCom and Tmds.DBus, plus the Inter font's SIL Open Font License, are retained in `licenses/upstream/`. Their source revisions/URLs are recorded in `SOURCES.json`; native Skia/HarfBuzz and ANGLE notices are copied from the resolved packages.

The new release does not inherit 0.5.0 hardware validation. Optional execution receipts must match the version and SHA256 of the packaged executables. Static PE, dependency, self-contained runtime, icon and notice checks prove packaging properties; they do not prove Bluetooth behavior on a particular machine.
