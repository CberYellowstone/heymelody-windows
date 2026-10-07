using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;
using HeyMelodyWindows.Artwork;
using HeyMelodyWindows.Resources;

namespace HeyMelodyWindows.Calibration.UI;

/// <summary>One reusable workflow view shared by the two independently launched components.</summary>
public sealed class CalibrationView : UserControl
{
    private readonly CalibrationViewModel _model;
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap, FontSize = 15 };
    private readonly StackPanel _body = new() { Spacing = 16 };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar _progress = new() { IsIndeterminate = true, Height = 4 };
    private readonly Button _cancel = new() { Content = "取消", HorizontalAlignment = HorizontalAlignment.Right };
    public event Func<Task>? CloseRequested;
    private string _bodyKey = "";
    private Slider? _level;
    private bool _updatingLevel;
    private DateTime _lastLevelCommit;
    private static readonly Lazy<BundledResourceStore> Artwork = new(() => new());

    public CalibrationView(CalibrationViewModel model)
    {
        _model = model;
        var content = new StackPanel { Spacing = 20, Margin = new Thickness(28) };
        content.Children.Add(new TextBlock { Text = model.Kind == "fit" ? "耳塞贴合度测试" : "设置黄金听感", FontSize = 26, FontWeight = FontWeight.SemiBold });
        content.Children.Add(_message); content.Children.Add(_progress); content.Children.Add(_body); content.Children.Add(_error); content.Children.Add(_cancel);
        Content = new ScrollViewer { Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        _cancel.Click += async (_, _) =>
        {
            if (CloseRequested is { } close) await close();
            else await _model.CancelAsync();
        };
        model.PropertyChanged += (_, _) => Dispatcher.UIThread.Post(Update);
        Update();
    }
    private void Update()
    {
        var state = _model.State;
        _error.Text = _model.Error; _error.IsVisible = _error.Text.Length > 0;
        _progress.IsVisible = state.Stage is "CheckingWear" or "Starting" or "Measuring" or "Scanning" or "Saving" or "Cancelling";
        _body.IsEnabled = !_model.Busy && state.Stage is not ("Cancelled" or "Cancelling");
        _cancel.Content = state.Stage is "Completed" or "Cancelled" ? "关闭" : "取消";
        _message.Text = state.Message.Length > 0 ? state.Message : state.Stage switch
        {
            "Ready" or "Idle" => _model.Kind == "fit" ? "戴好两只耳机，选择合适的耳塞。测试期间请保持安静。" : "戴好两只耳机，并选择安静的环境。随后将分别测试左耳和右耳。",
            "CheckingWear" => "请佩戴好两只耳机。",
            "Scanning" => "正在扫描耳道，请保持耳机位置稳定。",
            "ScanCompleted" => "耳道扫描已完成，接下来开始听感测试。",
            "Starting" => "正在准备测试…",
            "Measuring" or "AwaitingAnswer" or "Paused" => $"{(state.Side == 1 ? "左耳" : "右耳")} · 第 {Math.Clamp(state.StepIndex + 1, 1, state.StepCount)} / {state.StepCount} 步",
            "Results" => "测试已完成，请为这份听感配置命名。",
            "Completed" => "测试已完成。",
            "Failed" => "测试未能完成，请检查佩戴和连接后重试。",
            "Cancelled" => "测试已取消。",
            _ => "正在处理…"
        };
        var key = state.Stage + "/" + state.HearingVersion + "/" + (state.StepIndex > 0) + "/" + _model.ResourceSource;
        if (_bodyKey != key)
        {
            _bodyKey = key; _body.Children.Clear(); _level = null;
            BuildBody();
        }
        if (_level is not null)
        { _updatingLevel = true; _level.Value = state.LevelIndex; _updatingLevel = false; }
    }
    private void BuildBody()
    {
        var state = _model.State;
        if (state.Stage is "Ready" or "Idle")
        {
            if (_model.Kind == "hearing") AddHearingGuide();
            if (_model.Kind == "hearing" && state.SupportsEarScan)
                _body.Children.Add(new TextBlock { Text = "此耳机支持耳道扫描，向导将先完成扫描再测试听感。", TextWrapping = TextWrapping.Wrap });
            AddButton("开始", "start"); return;
        }
        if (state.Stage == "ScanCompleted") { AddButton("继续", "next"); return; }
        if (_model.Kind == "fit" && state.Stage == "Completed")
        {
            foreach (var result in state.FitResults)
                _body.Children.Add(new FASettingsExpanderItem { Content = result.Component == 1 ? "左耳" : "右耳",
                    Description = result.Status switch { 1 => "贴合良好", 6 => "贴合较差，请调整佩戴或更换耳塞", 0 => "贴合一般", _ => "未获得结果" } });
            AddButton("重新测试", "start"); return;
        }
        if (_model.Kind == "hearing" && state.Stage is "AwaitingAnswer" or "Measuring" or "Paused")
        {
            if (state.HearingVersion == "V2")
            {
                _body.Children.Add(new TextBlock { Text = "调整到刚好能听见的音量，然后继续。", TextWrapping = TextWrapping.Wrap });
                _level = new Slider { Minimum = 0, Maximum = 11, TickFrequency = 1, IsSnapToTickEnabled = true, Value = state.LevelIndex };
                AutomationProperties.SetName(_level, "听感测试音量");
                _level.ValueChanged += async (_, _) =>
                {
                    if (_updatingLevel || _level is null || DateTime.UtcNow - _lastLevelCommit < TimeSpan.FromMilliseconds(300)) return;
                    _lastLevelCommit = DateTime.UtcNow;
                    await _model.AdjustLevelAsync((int)_level.Value, false);
                };
                _level.AddHandler(PointerReleasedEvent, async (_, _) =>
                { if (_level is not null) await _model.AdjustLevelAsync((int)_level.Value, true); }, Avalonia.Interactivity.RoutingStrategies.Bubble, true);
                _level.AddHandler(KeyUpEvent, async (_, e) =>
                { if (_level is not null && e.Key is Avalonia.Input.Key.Left or Avalonia.Input.Key.Right or Avalonia.Input.Key.Home or Avalonia.Input.Key.End)
                    await _model.AdjustLevelAsync((int)_level.Value, true); }, Avalonia.Interactivity.RoutingStrategies.Bubble, true);
                _body.Children.Add(_level); AddButton("继续", "next");
                if (state.StepIndex > 0) AddButton("上一步", "previous");
            }
            else
            {
                _body.Children.Add(new TextBlock { Text = "你能听到测试声音吗？", FontSize = 18 });
                var answers = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
                var heard = new Button { Content = "听到了" }; var unheard = new Button { Content = "没听到" };
                heard.Click += async (_, _) => await _model.SendAsync("answer", heard: true);
                unheard.Click += async (_, _) => await _model.SendAsync("answer", heard: false);
                answers.Children.Add(heard); answers.Children.Add(unheard); _body.Children.Add(answers);
            }
            return;
        }
        if (_model.Kind == "hearing" && state.Stage == "Results")
        {
            var name = new TextBox { Text = _model.Name, PlaceholderText = "配置名称", MaxLength = 40 };
            AutomationProperties.SetName(name, "黄金听感配置名称");
            name.TextChanged += (_, _) => _model.Name = name.Text ?? "";
            _body.Children.Add(name);
            var save = new Button { Content = "保存并启用" };
            save.Click += async (_, _) =>
            {
                if (_model.ValidateName() is { } error) { _error.Text = error; _error.IsVisible = true; return; }
                var replacement = false;
                if ((_model.NeedsReplacement || _model.ReplacesCurrent) && TopLevel.GetTopLevel(this) is { } owner)
                {
                    var dialog = new FAContentDialog { Title = "保存黄金听感", Content = _model.NeedsReplacement
                            ? "最多保留 10 份配置。保存后将替换最早且未在使用的配置，保留当前配置，并在耳机上启用这份新配置。"
                            : "保存后将在耳机上启用新配置，替换当前黄金听感。已有配置会保留在本地历史中。",
                        PrimaryButtonText = "保存", CloseButtonText = "取消", DefaultButton = FAContentDialogButton.Close };
                    if (await dialog.ShowAsync(owner) != FAContentDialogResult.Primary) return;
                    replacement = true;
                }
                await _model.SendAsync("save", allowReplacement: replacement);
            };
            _body.Children.Add(save);
        }
        if (state.Stage == "Failed") AddButton("重试", "start");
    }
    private void AddButton(string text, string action)
    { var button = new Button { Content = text }; button.Click += async (_, _) => await _model.SendAsync(action); _body.Children.Add(button); }
    private void AddHearingGuide()
    {
        try
        {
            var source = _model.ResourceSource == "WirelessEarphones" ? ResourceSource.WirelessEarphones : ResourceSource.HeyMelody;
            var asset = Artwork.Value.FindNamedAsset(source, "melody_ui_gold_hearing_detecting_guide", ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark);
            if (asset is null || asset.Kind != "lottie") return;
            var path = Artwork.Value.FullPath(asset);
            _body.Children.Add(new LottieView(new FileLottieSource(new Uri(path), Path.GetDirectoryName(path)!, asset.Sha256,
                () => Artwork.Value.Acquire(asset) ?? throw new FileNotFoundException("Hearing guide asset is unavailable."))) { Height = 160, HorizontalAlignment = HorizontalAlignment.Stretch });
        }
        catch (IOException) { }
    }
}
