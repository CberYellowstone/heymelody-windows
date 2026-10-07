using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;

namespace HeyMelodyWindows.App;

public sealed class EqualizerChoiceView : UserControl
{
    private MainViewModel? _model;
    private MainViewModel? _builtModel;
    private IReadOnlyList<UiOption> _builtPresets = [];
    private IReadOnlyList<(int Id, string Name)> _builtEntries = [];
    private readonly Dictionary<int, RadioButton> _presets = [], _custom = [];
    private Button? _create;
    public EqualizerChoiceView() => DataContextChanged += (_, _) =>
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
    {
        if (e.PropertyName is nameof(MainViewModel.PresetOptions) or nameof(MainViewModel.SelectedPreset) or nameof(MainViewModel.EqEntries)
            or nameof(MainViewModel.SelectedEqEntry) or nameof(MainViewModel.CanCreateEq) or nameof(MainViewModel.CanEditEq)
            or nameof(MainViewModel.CanChoosePreset) or null) Build();
    }
    private void Build()
    {
        if (_model is not { } model) { Content = null; return; }
        var entries = model.EqEntries.Select(entry => (entry.Id, entry.Name)).ToArray();
        if (ReferenceEquals(_builtModel, model) && _builtPresets.SequenceEqual(model.PresetOptions) && _builtEntries.SequenceEqual(entries))
        { RefreshSelection(model); return; }
        _builtModel = model; _builtPresets = model.PresetOptions.ToArray(); _builtEntries = entries;
        _presets.Clear(); _custom.Clear();
        var panel = new StackPanel { Spacing = 9 };
        var group = "eq-" + GetHashCode();
        foreach (var option in model.PresetOptions)
        {
            var radio = new RadioButton { Content = option.Name, GroupName = group, IsEnabled = model.CanChoosePreset,
                IsChecked = option.Key == model.SelectedPreset?.Key };
            radio.Click += async (_, _) => await model.SelectPresetAsync(option);
            _presets[option.Key] = radio;
            panel.Children.Add(radio);
        }
        var line = new Border { Height = 1, Margin = new Thickness(0, 4) };
        line.Bind(Border.BackgroundProperty, new DynamicResourceExtension("ControlStrokeColorDefaultBrush"));
        panel.Children.Add(line);
        panel.Children.Add(new TextBlock { Text = "自定义均衡器" });
        foreach (var entry in model.EqEntries)
        {
            var radio = new RadioButton { Content = entry.Name, GroupName = group, IsEnabled = model.CanEditEq,
                IsChecked = model.SelectedPreset is null && entry.Selected };
            radio.Click += async (_, _) =>
            { if (model.EqEntries.FirstOrDefault(current => current.Id == entry.Id) is { } current) await model.ActivateEqEntryAsync(current); };
            _custom[entry.Id] = radio;
            panel.Children.Add(radio);
        }
        _create = new Button { Content = "新建均衡器", Command = model.NewEqCommand,
            IsVisible = model.CanCreateEq, IsEnabled = model.CanEditEq, HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(_create);
        Content = panel;
    }
    private void RefreshSelection(MainViewModel model)
    {
        foreach (var option in model.PresetOptions)
            if (_presets.TryGetValue(option.Key, out var radio))
            { radio.IsEnabled = model.CanChoosePreset; radio.IsChecked = option.Key == model.SelectedPreset?.Key; }
        foreach (var entry in model.EqEntries)
            if (_custom.TryGetValue(entry.Id, out var radio))
            { radio.IsEnabled = model.CanEditEq; radio.IsChecked = model.SelectedPreset is null && entry.Selected; }
        if (_create is not null) { _create.IsVisible = model.CanCreateEq; _create.IsEnabled = model.CanEditEq; }
    }
}
