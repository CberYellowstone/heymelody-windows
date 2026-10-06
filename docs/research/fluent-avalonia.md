# Avalonia / FluentAvalonia selection

Checked on 2026-10-06. This branch prioritizes migration of the existing Windows Enco X4 client. Other operating systems and models remain background research.

## Package decision

Use .NET 10, Avalonia **12.1.3**, FluentAvaloniaUI **3.1.0**, and CommunityToolkit.Mvvm **8.4.2**. Keep all directly referenced Avalonia packages at the same patch version.

The [official FA 3.1.0 NuGet package](https://www.nuget.org/packages/FluentAvaloniaUI/3.1.0) targets `net10.0` and depends on Avalonia >= 12.1.0. The [Avalonia.Desktop 12.1.3 package](https://www.nuget.org/packages/Avalonia.Desktop/12.1.3) was published on 2026-09-22. Both are stable MIT packages. FA's README minimum-version table is older than these packages; do not use its 2.x entries to select a new branch's dependencies.

The FA 3.1.0 nupkg was downloaded from NuGet and its `FluentAvalonia.xml` API documentation and nuspec inspected. Its repository commit is `215ee0481e8b0e6d396cbe2a0f33dc791c5646ab` on `FA3.1`; the interfaces below were verified against that actual package, rather than inferred solely from master.

## Controls and theme

| Purpose | FA 3.1.0 interface |
| --- | --- |
| Theme | `FluentAvalonia.Styling.FluentAvaloniaTheme` |
| Window | `FluentAvalonia.UI.Windowing.FAAppWindow` |
| Expandable settings group | `FASettingsExpander` |
| Setting row | `FASettingsExpanderItem` |
| Asynchronous dialog | `FAContentDialog.ShowAsync(Window/TopLevel)` |
| Message banner | `FAInfoBar` |
| Symbol icons | `FASymbolIcon`, `FASymbolIconSource` |

Controls are in `FluentAvalonia.UI.Controls` unless a different namespace is listed. The v3 controls have an `FA` prefix; the theme retains its original name. `SettingsCard` / `FASettingsCard` is not present. An expander without child items can form a setting card; item rows can host ordinary themed ComboBox, ToggleSwitch, Slider, Button, and TextBox controls. A clickable expander (`IsClickEnabled=true`) cannot also contain child items.

Load `FluentAvaloniaTheme` as the application's complete theme, rather than layering the official `FluentTheme` on it. For the default system appearance enable `PreferSystemTheme` and `PreferUserAccentColor`. `UseSystemFontOnWindows` defaults to true: Windows 10 uses Segoe UI and Windows 11 uses Segoe UI Variable Text. FA embeds its symbol font, so symbol icons do not require redistributing Windows icon fonts. Other platforms use their default text font, with FA's text alignment adjustments. See the [theme implementation](https://github.com/amwx/FluentAvalonia/blob/master/src/FluentAvalonia/Styling/Core/FluentAvaloniaTheme.axaml.cs) and [symbol icon implementation](https://github.com/amwx/FluentAvalonia/blob/master/src/FluentAvalonia/UI/Controls/IconElement/FASymbolIcon.cs).

FA was selected because its settings groups, dialogs, banners, icons, and modern window shell fit the supplied Surface screenshots. [Avalonia's official FluentTheme](https://docs.avaloniaui.net/docs/styling/themes) has official maintenance and fewer dependencies, but primarily supplies basic control themes. Reproducing these screens with it would require more local composition and styles. FA is actively released and used by desktop applications; its documentation and limited maintainer time make package pinning and regression checks appropriate.

Avalonia draws the application controls itself. This produces a WinUI-like appearance across platforms; it does not host WinUI's native control implementation. FAAppWindow adds Windows-specific titlebar integration and falls back on platform window behavior elsewhere. On Windows customize `TitleBar.ExtendsContentIntoTitleBar` / `Height`; modifying `ExtendClientAreaToDecorationsHint` on FAAppWindow is rejected. See [FAAppWindow](https://github.com/amwx/FluentAvalonia/blob/master/src/FluentAvalonia/UI/Windowing/AppWindow/FAAppWindow.cs).

## Windows packaging and verification

The resolved SkiaSharp.NativeAssets.Win32 **3.119.4** and Avalonia.Angle.Windows.Natives **2.1.27548.20260419** nupkgs were inspected and each contains native DLLs for `win-x86`, `win-x64`, and `win-arm64`. Preserve the existing three Windows packages. Upstream Avalonia 12 support tiers list Windows 11 24H2 x64/ARM64 as Tier 1; Windows 11 22H2 x64/ARM64 and Windows 10 22H2 x64 as Tier 2. x86/older versions are Tier 3, so x86 availability should not be described as full upstream support. See [supported platforms](https://docs.avaloniaui.net/docs/supported-platforms).

For automated rendering, put the real page UserControl in a plain Avalonia Window and use the same FA theme. `.UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })` enables pixels; `CaptureRenderedFrame()` can then save a PNG. The default headless drawing backend does not generate pixels. Pin theme/accent and use deterministic fake devices. Caption buttons, drag/Snap behavior, DPI movement, and Windows materials need a real-window check separately; headless images cannot establish those native behaviors. Avalonia 12's Headless.XUnit integration uses xUnit v3, so existing custom executable tests need not be converted. See [headless testing](https://docs.avaloniaui.net/docs/testing/setting-up-the-headless-platform).

Keep an executable test runner outside the session's UI consumer thread. In 12.1.3, dispatch completes a TaskCompletionSource without forcing asynchronous continuations; a final await continuation can consequently run on the consumer task. Synchronous `Dispose()` then waits for that same task and cannot finish. An asynchronous runner can use `await using`; this branch's runner instead synchronously waits for each dispatch from its original caller thread and disposes there. This was identified from the [exact 12.1.3 implementation](https://github.com/AvaloniaUI/Avalonia/blob/12.1.3/src/Headless/Avalonia.Headless/HeadlessUnitTestSession.cs), after screenshot assertions had passed but the test process remained running.

The same API has `Action`, `Func<TResult>`, and `Func<Task<TResult>>` dispatch overloads, but no `Func<Task>` overload. Every asynchronous test delegate must return a result and preferably select `Dispatch<bool>` explicitly. An `async` lambda with no result can select `Action`, creating `async void`; its failures then escape the dispatch Task and can produce a false pass. The UI runner checks deliberate asynchronous exception propagation and requires all expected cases to finish before writing a successful validation receipt.

## Supplied Surface reference

The bundle `Microsoft.SurfaceHub_76.9122.134.0` was inspected as nested ZIPs without installing or executing it. Its x64 manifest lists WindowsAppRuntime 1.8 and `Windows.FullTrustApplication`, making this particular reference a packaged WinUI 3 desktop application. It contains accessory hero images and device-specific assets. The supplied screenshots establish the layout; Surface product assets and executable code are not included in HeyMelody.

The selected MIT dependencies require retaining their notices in distributions. FA's [license](https://github.com/amwx/FluentAvalonia/blob/master/LICENSE) covers its package; use its included icons and the application's own/reference-derived OPPO product illustration.
