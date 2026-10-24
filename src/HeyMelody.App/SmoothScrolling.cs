using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Styling;
using FluentAvalonia.Core;
using SmoothScroll.Avalonia.Controls;

namespace HeyMelodyWindows.Ui;

/// <summary>Opt-in smooth wheel scrolling for the non-virtualized page viewers shared by the GUI entry points.</summary>
internal static class SmoothScrolling
{
    private static readonly Lazy<ControlTheme> Theme = new(() =>
        (ControlTheme)new ScrollViewerSmoothTheme().Resources[typeof(ScrollViewer)]!);

    /// <returns>Whether the library is active; the device strip keeps its immediate wheel fallback otherwise.</returns>
    internal static bool Attach(ScrollViewer viewer, bool horizontal = false)
    {
        // Keep the standard presenter when animations are disabled at application startup.
        if (!FAUISettings.AreAnimationsEnabled()) return false;

        ScrollPresenter? currentPresenter = null;
        viewer.TemplateApplied += (_, e) =>
        {
            currentPresenter = e.NameScope.Find<ScrollPresenter>("PART_ContentPresenter");
            if (horizontal && currentPresenter is { } presenter)
            {
                presenter.ContentOrientation = ScrollContentOrientation.Horizontal;
                presenter.HorizontalAlignment = HorizontalAlignment.Center;
                presenter.VerticalScrollMode = ScrollMode.Disabled;
                presenter.HorizontalScrollMode = ScrollMode.Auto;
            }
        };
        // 12.0.0.14 arranges before coercing Offset after a geometry change. Re-arrange once
        // with the corrected offset; stable geometry and nested viewers do not request another pass.
        viewer.ScrollChanged += (_, e) =>
        {
            if (ReferenceEquals(e.Source, viewer) && (e.ExtentDelta != default || e.ViewportDelta != default)
                && currentPresenter is { } presenter && ReferenceEquals(presenter.TemplatedParent, viewer)
                && TopLevel.GetTopLevel(presenter) is not null)
                presenter.InvalidateArrange();
        };
        viewer.Theme = Theme.Value;
        return true;
    }
}
