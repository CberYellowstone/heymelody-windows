using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;

namespace HeyMelodyWindows.App;

/// <summary>Hosts a device icon with a replaceable template and fixed external layout.</summary>
public partial class DeviceIconView : UserControl
{
    public static readonly StyledProperty<object?> VisualProperty =
        AvaloniaProperty.Register<DeviceIconView, object?>(nameof(Visual), DeviceVisuals.GenericIcon);

    public static readonly StyledProperty<IDataTemplate?> VisualTemplateProperty =
        AvaloniaProperty.Register<DeviceIconView, IDataTemplate?>(nameof(VisualTemplate), DeviceVisuals.DefaultTemplate);

    public object? Visual { get => GetValue(VisualProperty); set => SetValue(VisualProperty, value); }
    public IDataTemplate? VisualTemplate { get => GetValue(VisualTemplateProperty); set => SetValue(VisualTemplateProperty, value); }

    public DeviceIconView() => InitializeComponent();
}
