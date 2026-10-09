using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Styling;
using FluentAvalonia.UI.Controls;

namespace HeyMelodyWindows.App;

/// <summary>Accessible radio choices with a shared, mode-dependent fine adjustment.</summary>
public sealed class AncModeSelector : UserControl
{
    private MainViewModel? _model;
    private MainViewModel? _builtModel;
    private IReadOnlyList<UiAncMode> _builtModes = [];
    private string _builtSource = "";
    private readonly Dictionary<int, (RadioButton Radio, Border Circle)> _radios = [];
    private ComboBox? _combo;
    public AncModeSelector() => DataContextChanged += (_, _) =>
    {
        if (_model is not null) _model.PropertyChanged -= Changed;
        _model = DataContext as MainViewModel;
        if (_model is not null) _model.PropertyChanged += Changed;
        Build();
    };
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    { if (_model is not null) _model.PropertyChanged -= Changed; base.OnDetachedFromVisualTree(e); }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    { base.OnAttachedToVisualTree(e); if (_model is not null) { _model.PropertyChanged -= Changed; _model.PropertyChanged += Changed; } }
    private void Changed(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName is nameof(MainViewModel.AncModes) or nameof(MainViewModel.SelectedAncMode) or nameof(MainViewModel.SelectedAncSubmode) or nameof(MainViewModel.CanSetAnc) or nameof(MainViewModel.CanSetAncMode) or nameof(MainViewModel.ResourceSource) or null) Build(); }
    private void Build()
    {
        if (_model is not { } model) { Content = null; return; }
        if (ReferenceEquals(_builtModel, model) && _builtSource == model.ResourceSource
            && _builtModes.Count == model.AncModes.Count && _builtModes.Zip(model.AncModes).All(pair =>
                pair.First.Key == pair.Second.Key && pair.First.Name == pair.Second.Name && pair.First.IconKey == pair.Second.IconKey
                && pair.First.Options.SequenceEqual(pair.Second.Options)))
        { RefreshSelection(model); return; }
        _builtModel = model; _builtSource = model.ResourceSource; _builtModes = model.AncModes.ToArray(); _radios.Clear();
        var panel = new StackPanel { Spacing = 16, Margin = new Thickness(20, 16) };
        var modes = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var mode in model.AncModes)
        {
            var icon = DeviceVisuals.CreateAncGlyph(model.ResourceSource, mode.IconKey, mode.Key == model.SelectedAncMode?.Key
                ? "TextOnAccentFillColorPrimaryBrush" : "TextFillColorSecondaryBrush");
            var circle = new Border { Width = 52, Height = 52, CornerRadius = new CornerRadius(26), Child = icon,
                BorderThickness = new Thickness(1), Tag = mode.Key == model.SelectedAncMode?.Key };
            circle.Bind(Border.BorderBrushProperty, new DynamicResourceExtension(mode.Key == model.SelectedAncMode?.Key
                ? "AccentFillColorDefaultBrush" : "ControlStrokeColorDefaultBrush"));
            circle.Bind(Border.BackgroundProperty, new DynamicResourceExtension(mode.Key == model.SelectedAncMode?.Key
                ? "AccentFillColorDefaultBrush" : "ControlFillColorSecondaryBrush"));
            var radio = new RadioButton { GroupName = "anc-" + GetHashCode(), IsChecked = mode.Key == model.SelectedAncMode?.Key,
                IsEnabled = model.CanSetAncMode, Content = new StackPanel { Spacing = 7, HorizontalAlignment = HorizontalAlignment.Center,
                    Children = { circle, new TextBlock { Text = mode.Name, HorizontalAlignment = HorizontalAlignment.Center } } } };
            radio.Theme = CircleTheme;
            AutomationProperties.SetName(radio, mode.Name);
            radio.Click += async (_, _) => { if (mode.Key != model.SelectedAncMode?.Key) await model.SelectAncModeAsync(mode); };
            _radios[mode.Key] = (radio, circle);
            modes.Children.Add(radio);
        }
        if (model.AncModes.Count != 0) panel.Children.Add(modes);
        else
        {
            // Keep the always-visible card from looking broken before modes are read.
            var empty = new TextBlock { Text = "暂无可用的降噪模式", HorizontalAlignment = HorizontalAlignment.Center };
            empty.Classes.Add("caption");
            panel.Children.Add(empty);
        }
        var combo = new ComboBox { Name = "AncCombo", ItemsSource = model.SelectedAncMode?.Options ?? [],
            SelectedItem = model.SelectedAncSubmode, MinWidth = 200, HorizontalAlignment = HorizontalAlignment.Center,
            IsEnabled = model.CanSetAnc, IsVisible = model.HasAncSubmodes, PlaceholderText = "档位未确认",
            ItemTemplate = new FuncDataTemplate<UiOption>((option, _) => new TextBlock { Text = option?.Name }) };
        AutomationProperties.SetName(combo, "降噪细分选项");
        UserInputCommit.Combo(combo, async () => { if (combo.SelectedItem is UiOption option) await model.ApplyAncSubmodeAsync(option); });
        panel.Children.Add(combo);
        _combo = combo;
        Content = panel;
    }
    private void RefreshSelection(MainViewModel model)
    {
        foreach (var mode in model.AncModes)
        {
            if (!_radios.TryGetValue(mode.Key, out var item)) continue;
            bool selected = mode.Key == model.SelectedAncMode?.Key;
            item.Radio.IsEnabled = model.CanSetAncMode;
            item.Radio.IsChecked = selected;
            if (Equals(item.Circle.Tag, selected)) continue;
            item.Circle.Tag = selected;
            item.Circle.Child = DeviceVisuals.CreateAncGlyph(model.ResourceSource, mode.IconKey,
                selected ? "TextOnAccentFillColorPrimaryBrush" : "TextFillColorSecondaryBrush");
            item.Circle.Bind(Border.BackgroundProperty, new DynamicResourceExtension(selected ? "AccentFillColorDefaultBrush" : "ControlFillColorSecondaryBrush"));
            item.Circle.Bind(Border.BorderBrushProperty, new DynamicResourceExtension(selected ? "AccentFillColorDefaultBrush" : "ControlStrokeColorDefaultBrush"));
        }
        if (_combo is null) return;
        var options = model.SelectedAncMode?.Options ?? [];
        if (!(_combo.ItemsSource?.Cast<UiOption>() ?? []).SequenceEqual(options)) _combo.ItemsSource = options;
        if (!Equals(_combo.SelectedItem, model.SelectedAncSubmode)) _combo.SelectedItem = model.SelectedAncSubmode;
        _combo.IsVisible = model.HasAncSubmodes;
        _combo.IsEnabled = model.CanSetAnc;
    }

    private static ControlTheme CircleTheme => new(typeof(RadioButton))
    {
        Setters =
        {
            new Setter(TemplatedControl.TemplateProperty, new FuncControlTemplate<RadioButton>((_, scope) =>
                new Border { Name = "focus", Padding = new Thickness(5), CornerRadius = new CornerRadius(6),
                    BorderThickness = new Thickness(2), BorderBrush = Brushes.Transparent,
                    Child = new ContentPresenter { Name = "PART_ContentPresenter",
                        [!ContentPresenter.ContentProperty] = new Avalonia.Data.TemplateBinding(ContentControl.ContentProperty) } })),
            new Setter(TemplatedControl.IsTemplateFocusTargetProperty, true)
        },
        Children =
        {
            new Style(x => x.Nesting().Class(":focus-visible").Template().OfType<Border>().Name("focus"))
            { Setters = { new Setter(Border.BorderBrushProperty, new DynamicResourceExtension("TextFillColorPrimaryBrush")), new Setter(Border.BorderThicknessProperty, new Thickness(2)) } },
            new Style(x => x.Nesting().Class(":disabled")) { Setters = { new Setter(OpacityProperty, .5) } }
        }
    };
}
