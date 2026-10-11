using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Styling;

namespace HeyMelodyWindows.App;

/// <summary>Hosts product artwork without choosing a device or rendering technology.</summary>
public partial class ProductArtwork : UserControl
{
    public static readonly StyledProperty<object?> VisualProperty =
        AvaloniaProperty.Register<ProductArtwork, object?>(nameof(Visual), DeviceVisuals.GenericProduct);

    public static readonly StyledProperty<IDataTemplate?> VisualTemplateProperty =
        AvaloniaProperty.Register<ProductArtwork, IDataTemplate?>(nameof(VisualTemplate), DeviceVisuals.DefaultTemplate);

    /// <summary>
    /// Fades new artwork in when the device changes. Headless previews and tests turn this off: their animation clock
    /// advances only when ticked, which would leave screenshots mid-fade.
    /// </summary>
    internal static bool TransitionsEnabled { get; set; } = true;

    private static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(150);
    private CancellationTokenSource? _fade;
    private bool _attached;

    public object? Visual { get => GetValue(VisualProperty); set => SetValue(VisualProperty, value); }
    public IDataTemplate? VisualTemplate { get => GetValue(VisualTemplateProperty); set => SetValue(VisualTemplateProperty, value); }

    public ProductArtwork() => InitializeComponent();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        _fade?.Cancel();
        VisualPresenter.Opacity = 1;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == VisualProperty && _attached && TransitionsEnabled && !Equals(change.OldValue, change.NewValue)) FadeIn();
    }

    // Opacity only: the previous artwork leaves at once, so two 3D views never render together.
    private async void FadeIn()
    {
        _fade?.Cancel();
        var fade = _fade = new CancellationTokenSource();
        var animation = new Animation
        {
            Duration = FadeDuration, Easing = new CubicEaseOut(), FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(OpacityProperty, 0d) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(OpacityProperty, 1d) } }
            }
        };
        try { await animation.RunAsync(VisualPresenter, fade.Token); }
        catch (OperationCanceledException) { }
        finally { if (ReferenceEquals(_fade, fade)) VisualPresenter.Opacity = 1; }
    }
}
