using Avalonia;
using Avalonia.Controls;
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
        var expander = new FASettingsExpander { Header = section.Header, IconSource = AppIcons.Source(IconFor(section.Id)),
            Description = string.IsNullOrWhiteSpace(section.Description) ? DescriptionFor(section.Id) : section.Description,
            IsExpanded = _owner?.SectionExpanded(section.Id) ?? false };
        _expander = expander;
        foreach (var control in section.Controls) expander.Items.Add(new FeatureControlView { DataContext = control });
        expander.PropertyChanged += (_, e) =>
        {
            if (!_restoring && e.Property == FASettingsExpander.IsExpandedProperty)
                _owner?.RememberSectionExpansion(section.Id, expander.IsExpanded);
        };
        Content = expander;
    }
    // Device resources define these groups without icons or captions.
    internal static AppIcon IconFor(string id) => id switch
    {
        "audio-features" or "audio" => AppIcon.Sound,
        "device-features" => AppIcon.Earbuds,
        "gesture-features" => AppIcon.Gestures,
        _ => AppIcon.Settings
    };
    internal static string? DescriptionFor(string id) => id switch
    {
        "audio-features" or "audio" => "音效、低延迟与音质",
        "device-features" => "连接、佩戴与提示音",
        "gesture-features" => "左右耳手势",
        _ => null
    };
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
