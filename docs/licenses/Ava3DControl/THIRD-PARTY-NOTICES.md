# Third-party notices

Ava3DControl builds on the components below. Their licences govern them and are not superseded by
`LICENSE.md`; the licence identifiers here are the ones each package declares in its own metadata.

Nothing in this list restricts commercial use, and none of it requires you to attribute anything in your own
application's user interface.

## Used by the library

| Component | Version | Licence |
| --- | --- | --- |
| [Avalonia](https://github.com/AvaloniaUI/Avalonia) | 12.1.0 | MIT |
| [Avalonia.Skia](https://github.com/AvaloniaUI/Avalonia) | 12.1.0 | MIT |
| [SkiaSharp](https://github.com/mono/SkiaSharp) | 3.119.4 | MIT |

The glTF reader is the library's own and brings in nothing; it parses JSON through `System.Text.Json`,
which is part of .NET.

SkiaSharp is a managed wrapper around [Skia](https://skia.org/), which is distributed by Google under the
BSD 3-Clause licence. The Skia notice travels inside the SkiaSharp package.

## Shipped in the optional MoltenVK package

`Ava3DControl.MoltenVK` is a separate package containing no code of ours at all. It exists so that the
Vulkan renderer can be selected on a Mac without installing anything by hand, and it is optional: the
library loads MoltenVK by name at run time and never links against it, so a system-wide install works
just as well.

| Component | Version | Licence |
| --- | --- | --- |
| [MoltenVK](https://github.com/KhronosGroup/MoltenVK) | 1.4.2 | Apache-2.0 |

The full Apache 2.0 text travels inside that package as `MOLTENVK-LICENSE.txt`. MoltenVK is a product of
the Khronos Group; nothing in its licence requires attribution in your application's user interface.

## Used by the demo application only

These are not dependencies of the library. They are here because the demo's source is included and builds
against them.

| Component | Version | Licence |
| --- | --- | --- |
| [Avalonia.Desktop / .Browser / .iOS / .Android](https://github.com/AvaloniaUI/Avalonia) | 12.1.0 | MIT |
| [Avalonia.Themes.Fluent](https://github.com/AvaloniaUI/Avalonia) | 12.1.0 | MIT |
| [Avalonia.Fonts.Inter](https://github.com/AvaloniaUI/Avalonia) | 12.1.0 | MIT |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | 8.4.2 | MIT |
| [AvaloniaUI.DiagnosticsSupport](https://github.com/AvaloniaUI/Avalonia) | 2.2.3 | MIT |
| [Xamarin.AndroidX.Core.SplashScreen](https://github.com/xamarin/AndroidX) | 1.0.1.15 | MIT |

The Inter typeface bundled by `Avalonia.Fonts.Inter` is by Rasmus Andersson under the
[SIL Open Font Licence 1.1](https://github.com/rsms/inter/blob/master/LICENSE.txt).

## Used only to build the documentation

| Component | Version | Licence |
| --- | --- | --- |
| [System.Reflection.MetadataLoadContext](https://github.com/dotnet/runtime) | 9.0.0 | MIT |

## Used by the documentation site

| Component | Version | Licence |
| --- | --- | --- |
| [highlight.js](https://github.com/highlightjs/highlight.js) | 11.12.0 | BSD-3-Clause |

The browser scripts and complete licence are included in `api/assets/highlight/` on the generated site.
They are not dependencies of the Ava3DControl library or demo.

## Art and 3D assets

**There are none.** Every mesh, texture and glTF file the demo displays is generated in code at startup —
see `demo/Ava3D.Demo/Textures/Procedural.cs` and `demo/Ava3D.Demo/Scenes/GltfScene.cs` — and every screenshot
on the documentation site was produced by that demo.

This is a deliberate choice rather than an accident of scope. The well-known glTF sample models are variously
CC BY-NC or carry an attribution requirement, and neither mixes cleanly with a product licensed for
commercial use whose demo source is offered for reuse without attribution. Generating the assets removes the
question instead of answering it in a footnote.
