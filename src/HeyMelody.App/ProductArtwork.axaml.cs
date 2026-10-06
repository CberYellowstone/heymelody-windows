using Avalonia;
using Avalonia.Controls;

namespace HeyMelodyWindows.App;

public partial class ProductArtwork : UserControl
{
    public static readonly StyledProperty<bool> ShowX4Property = AvaloniaProperty.Register<ProductArtwork, bool>(nameof(ShowX4));
    public bool ShowX4 { get => GetValue(ShowX4Property); set => SetValue(ShowX4Property, value); }
    public ProductArtwork() => InitializeComponent();
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ShowX4Property)
        { X4Drawing.IsVisible = ShowX4; GenericDrawing.IsVisible = !ShowX4; }
    }
}
