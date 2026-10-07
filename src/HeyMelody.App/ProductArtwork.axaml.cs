using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;

namespace HeyMelodyWindows.App;

/// <summary>Hosts product artwork without choosing a device or rendering technology.</summary>
public partial class ProductArtwork : UserControl
{
    public static readonly StyledProperty<object?> VisualProperty =
        AvaloniaProperty.Register<ProductArtwork, object?>(nameof(Visual), DeviceVisuals.GenericProduct);

    public static readonly StyledProperty<IDataTemplate?> VisualTemplateProperty =
        AvaloniaProperty.Register<ProductArtwork, IDataTemplate?>(nameof(VisualTemplate), DeviceVisuals.DefaultTemplate);

    public object? Visual { get => GetValue(VisualProperty); set => SetValue(VisualProperty, value); }
    public IDataTemplate? VisualTemplate { get => GetValue(VisualTemplateProperty); set => SetValue(VisualTemplateProperty, value); }

    public ProductArtwork() => InitializeComponent();
}
