using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;

namespace HeyMelodyWindows.App;

/// <summary>Keeps a section's expansion preference across refreshed resource definitions.</summary>
public sealed class FeatureSectionView : UserControl
{
    private FeatureSectionViewModel? _section;
    private FASettingsExpander? _expander;
    private MainView? _owner;
    private bool _restoring;
    public FeatureSectionView() => DataContextChanged += (_, _) => Build();
    private void Build()
    {
        _section = DataContext as FeatureSectionViewModel;
        if (_section is not { } section) { Content = null; return; }
        _owner = this.FindAncestorOfType<MainView>();
        var expander = new FASettingsExpander { Header = section.Header, Description = section.Description,
            IsExpanded = _owner?.SectionExpanded(section.Id) ?? false };
        _expander = expander;
        expander.Items.Add(new ItemsControl { ItemsSource = section.Controls,
            ItemTemplate = new FuncDataTemplate<FeatureControlViewModel>((control, _) => new FeatureControlView { DataContext = control }) });
        expander.PropertyChanged += (_, e) =>
        {
            if (!_restoring && e.Property == FASettingsExpander.IsExpandedProperty)
                _owner?.RememberSectionExpansion(section.Id, expander.IsExpanded);
        };
        Content = expander;
    }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _owner = this.FindAncestorOfType<MainView>();
        if (_section is { } section && _expander is { } expander)
        {
            _restoring = true;
            expander.IsExpanded = _owner?.SectionExpanded(section.Id) ?? false;
            _restoring = false;
        }
    }
}
