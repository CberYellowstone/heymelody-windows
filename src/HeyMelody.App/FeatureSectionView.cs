using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.ComponentModel;
using FluentAvalonia.UI.Controls;

namespace HeyMelodyWindows.App;

/// <summary>A stable list entry for one resource section; refreshed definitions replace only <see cref="Section"/>.</summary>
/// <remarks>
/// The view model replaces a section whenever any of its values change. Replacing the list item would rebuild the
/// whole expander (a layout spike and lost focus); swapping the section in a stable slot updates the rows in place.
/// </remarks>
public sealed class FeatureSectionSlot(FeatureSectionViewModel section) : ObservableObject
{
    private FeatureSectionViewModel _section = section;
    public string Id { get; } = section.Id;
    public FeatureSectionViewModel Section { get => _section; set => SetProperty(ref _section, value); }
}

/// <summary>Keeps a section's expansion preference across refreshed resource definitions.</summary>
public sealed class FeatureSectionView : UserControl
{
    private FeatureSectionSlot? _slot;
    private FASettingsExpander? _expander;
    private string? _sectionId;
    private MainView? _owner;
    private bool _restoring;

    public FeatureSectionView() => DataContextChanged += (_, _) => Attach(DataContext as FeatureSectionSlot);

    /// <summary>The section currently shown.</summary>
    public FeatureSectionViewModel? Section => _slot?.Section;

    private void Attach(FeatureSectionSlot? slot)
    {
        if (ReferenceEquals(slot, _slot)) return;
        if (_slot is not null) _slot.PropertyChanged -= SlotChanged;
        _slot = slot;
        if (_slot is not null) _slot.PropertyChanged += SlotChanged;
        Present();
    }

    private void SlotChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or nameof(FeatureSectionSlot.Section)) Present();
    }

    private void Present()
    {
        if (_slot?.Section is not { } section) { Content = null; _expander = null; _sectionId = null; return; }
        _owner ??= this.FindAncestorOfType<MainView>();
        if (_expander is null || _sectionId != section.Id)
        {
            var expander = new FASettingsExpander { IconSource = AppIcons.Source(IconFor(section.Id)),
                IsExpanded = _owner?.SectionExpanded(section.Id) ?? false };
            var id = section.Id;
            expander.PropertyChanged += (_, e) =>
            {
                if (!_restoring && e.Property == FASettingsExpander.IsExpandedProperty)
                    _owner?.RememberSectionExpansion(id, expander.IsExpanded);
            };
            _expander = expander;
            _sectionId = section.Id;
            Content = expander;
        }
        _expander.Header = section.Header;
        _expander.Description = string.IsNullOrWhiteSpace(section.Description) ? DescriptionFor(section.Id) : section.Description;
        // The remembered choice belongs to the current session (real device or demo), which may have just changed.
        _restoring = true;
        _expander.IsExpanded = _owner?.SectionExpanded(section.Id) ?? false;
        _restoring = false;
        FeatureRows.Reconcile(_expander.Items, section.Controls);
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
    /// <summary>Sound sections join ANC and the equalizer; the rest belong to the device group.</summary>
    internal static bool IsSoundSection(string id) => id is "audio-features" or "audio";

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _owner = this.FindAncestorOfType<MainView>();
        if (_sectionId is { } id && _expander is { } expander)
        {
            _restoring = true;
            expander.IsExpanded = _owner?.SectionExpanded(id) ?? false;
            _restoring = false;
        }
    }
}

/// <summary>Reuses feature rows by control id so a refreshed definition never rebuilds an unchanged editor.</summary>
/// <remarks>
/// Rows are direct expander items, never nested in another item: a nested <see cref="FASettingsExpanderItem"/> draws its
/// own card background and is too narrow to keep its editor beside the label. Items before <c>start</c> are fixed content.
/// </remarks>
internal static class FeatureRows
{
    public static void Reconcile(ItemCollection rows, IReadOnlyList<FeatureControlViewModel> controls, int start = 0)
    {
        var existing = rows.Cast<object?>().Skip(start).OfType<FeatureControlView>().ToList();
        var byId = new Dictionary<string, FeatureControlView>(StringComparer.Ordinal);
        foreach (var row in existing) if (row.Model?.Id is { } id) byId.TryAdd(id, row);
        var next = new List<FeatureControlView>(controls.Count);
        foreach (var control in controls)
        {
            if (!byId.Remove(control.Id, out var row)) row = new FeatureControlView();
            row.DataContext = control;
            next.Add(row);
        }
        // Touch the collection only when membership or order changed; re-adding rows would re-template them.
        if (rows.Count - start == next.Count && existing.Count == next.Count
            && existing.Zip(next).All(pair => ReferenceEquals(pair.First, pair.Second))) return;
        while (rows.Count > start) rows.RemoveAt(rows.Count - 1);
        foreach (var row in next) rows.Add(row);
    }
}
