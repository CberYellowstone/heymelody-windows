using Avalonia.Controls;
using Avalonia.VisualTree;
using HeyMelodyWindows.App;

internal static class FeatureTestData
{
    public static UiSnapshot Decorate(UiSnapshot snapshot)
    {
        snapshot.FeatureSections = [new("experience", "连接与体验", true,
        [new("dual", "双设备连接", "Switch", snapshot.DualConnection is { } dual ? dual ? 1 : 0 : null, CanWrite: snapshot.CanSetDualConnection),
         new("game", "游戏模式", "Switch", snapshot.GameMode is { } game ? game ? 1 : 0 : null, CanWrite: snapshot.CanSetGameMode),
         new("hires", "Hi-Res 高清音频", "Switch", snapshot.HiRes is { } hires ? hires ? 1 : 0 : null, CanWrite: snapshot.CanSetHiRes)])];
        return snapshot;
    }
    public static ToggleSwitch Switch(Control root, string id) => root.GetVisualDescendants().OfType<ToggleSwitch>()
        .Single(control => control.DataContext is FeatureControlViewModel model && model.Id == id);
    public static ComboBox Anc(Control root) => root.GetVisualDescendants().OfType<ComboBox>().Single(combo => combo.Name == "AncCombo");
}
