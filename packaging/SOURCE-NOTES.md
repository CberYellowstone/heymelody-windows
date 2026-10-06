# Source and provenance

Client source is in `HeyMelodyWindows/src`, with offline checks in `tests` and reproducible packaging in `Build-Client.ps1`. The client uses BCL and WPF; fonts come from Windows. The earbud icon was created for this client by `tools/create_app_icon.py`.

Protocol fields, capability numbers and X4 model data were checked against the locally supplied HeyMelody APK and firmware evidence bundle. The exact embedded X4 asset is pinned by SHA256 in `HeyMelody.Devices/X4Profile.cs`. Details and source locations are documented in `phase5-protocol-evidence.md` and `daily-notes.md`.

The locally supplied OppoPodsManager reference (GPL-3.0-or-later) informed the research. Current parsing and workflows were implemented in the project's own protocol/control layers and independently checked against the Android source; this is not a clean-room research claim. This package does not contain the reference application's assemblies, artwork, fonts or APK.

The self-contained .NET runtimes include `LICENSE.txt` and `THIRD-PARTY-NOTICES.txt` in each architecture directory and its separate `backend` directory, copied from the exact official runtime packages used for publishing. `LICENSE-WINDOWSDESKTOP.txt` preserves the Desktop runtime notice. WPF and console runtime files are not merged.
