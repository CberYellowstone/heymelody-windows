using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using FluentAvalonia.UI.Controls;

namespace HeyMelodyWindows.App;

/// <summary>Windows presentation for the small, explicitly supported value-control vocabulary.</summary>
/// <remarks>Each control is itself a settings row, so its section lays it out like any native expander item.</remarks>
public sealed class FeatureControlView : FASettingsExpanderItem
{
    public FeatureControlView() => DataContextChanged += (_, _) => Build();
    protected override Type StyleKeyOverride => typeof(FASettingsExpanderItem);

    private void Build()
    {
        if (DataContext is not FeatureControlViewModel model) { Hide(); return; }
        Control editor;
        switch (model.Kind)
        {
            case "Switch":
                // The template keeps an empty header margin and a 12px column for the on/off text;
                // offset both so the knob is centred and aligns with other right-hand editors.
                var toggle = new ToggleSwitch { OnContent = "", OffContent = "", Margin = new Thickness(0, -4, -12, 0) };
                toggle.Bind(ToggleSwitch.IsCheckedProperty, new Binding(nameof(model.BooleanValue)) { Mode = BindingMode.OneWay });
                toggle.Click += async (_, _) => { model.BooleanValue = toggle.IsChecked == true; await model.SubmitAsync(); };
                editor = toggle;
                break;
            case "Slider":
                var slider = new Slider { Width = 190, Minimum = model.Minimum, Maximum = model.Maximum,
                    TickFrequency = model.Step, IsSnapToTickEnabled = model.Step > 0 };
                slider.Bind(Slider.ValueProperty, new Binding(nameof(model.NumberValue)) { Mode = BindingMode.TwoWay });
                UserInputCommit.Slider(slider, model.SubmitAsync);
                editor = slider;
                break;
            case "Radio":
                var radios = new StackPanel { Spacing = 6, MaxWidth = 250 };
                var group = "feature-" + model.Id + "-" + Guid.NewGuid().ToString("N");
                foreach (var option in model.Options)
                {
                    var radio = new RadioButton { Content = option.Name, GroupName = group,
                        IsChecked = model.SelectedOption?.Key == option.Key };
                    radio.DataContext = model;
                    radio.Bind(RadioButton.IsCheckedProperty, new MultiBinding { Converter = new OptionSelectedConverter(), Bindings =
                    { new Binding(nameof(model.SelectedOption)), new Binding { Source = option } } });
                    radio.Click += async (_, _) => { model.SelectedOption = option; await model.SubmitAsync(); };
                    radios.Children.Add(radio);
                }
                editor = radios;
                break;
            case "Text":
                var text = new TextBox { Width = 190 };
                text.Bind(TextBox.TextProperty, new Binding(nameof(model.TextValue)) { Mode = BindingMode.TwoWay });
                var textDirty = false;
                text.TextInput += (_, _) => textDirty = true;
                text.PastingFromClipboard += (_, _) => textDirty = true;
                text.CuttingToClipboard += (_, _) => textDirty = true;
                text.KeyDown += (_, e) => { if (e.Key is Avalonia.Input.Key.Back or Avalonia.Input.Key.Delete) textDirty = true; };
                text.KeyUp += async (_, e) => { if (e.Key == Avalonia.Input.Key.Enter && textDirty) { textDirty = false; await model.SubmitAsync(); } };
                text.LostFocus += async (_, _) => { if (text.IsEnabled && textDirty) { textDirty = false; await model.SubmitAsync(); } };
                editor = text;
                break;
            case "Combo":
                var combo = new ComboBox { ItemsSource = model.Options, MinWidth = 150, MaxWidth = 230,
                    ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<UiOption>((option, _) => new TextBlock { Text = option?.Name }) };
                combo.Bind(SelectingItemsControl.SelectedItemProperty, new Binding(nameof(model.SelectedOption)) { Mode = BindingMode.TwoWay });
                UserInputCommit.Combo(combo, model.SubmitAsync);
                editor = combo;
                break;
            default:
                Hide();
                return;
        }
        editor.DataContext = model;
        editor.Bind(IsEnabledProperty, new Binding(nameof(model.CanEdit)));
        Avalonia.Automation.AutomationProperties.SetName(editor, model.Label);
        editor.HorizontalAlignment = HorizontalAlignment.Right;
        Content = model.Label;
        // An empty description still reserves a caption line and lifts the label off-centre.
        Description = string.IsNullOrEmpty(model.Description) ? null : model.Description;
        Footer = editor;
        IsVisible = true;
    }

    private void Hide()
    {
        Content = null; Description = null; Footer = null;
        IsVisible = false;
    }
}
