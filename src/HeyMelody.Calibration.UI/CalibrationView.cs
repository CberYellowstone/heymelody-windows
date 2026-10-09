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
    private Button? _next, _retryLevel, _save, _resume;
    private readonly List<(Button Button, string Mode)> _previewButtons = [];
    private TextBlock? _levelLabel, _previewLabel, _difference;
    private readonly List<Button> _answerButtons = [];
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
        model.PropertyChanged += (_, _) =>
        { if (Dispatcher.UIThread.CheckAccess()) Update(); else Dispatcher.UIThread.Post(Update); };
        Update();
    }
    private void Update()
    {
        var state = _model.State;
        _error.Text = _model.Error; _error.IsVisible = _error.Text.Length > 0;
        _progress.IsVisible = _model.Busy || _model.LevelPending || state.Stage is "CheckingWear" or "Starting" or "Measuring" or "Scanning" or "LoadingCurves" or "LoadingFilters" or "Saving" or "Cancelling";
        _body.IsEnabled = !_model.Busy && state.Stage is not ("Cancelled" or "Cancelling");
        _body.IsVisible = !_model.CancellationRequested;
        _cancel.IsEnabled = !_model.CancellationPending || _model.CanCloseLocally;
        _cancel.Content = _model.CanCloseLocally ? "关闭" : _model.CancellationFailed ? "重试停止"
            : state.Stage is "Completed" or "Cancelled" ? "关闭" : "取消";
        _message.Text = state.Message.Length > 0 ? state.Message : state.Stage switch
        {
            "Ready" or "Idle" => _model.Kind == "fit" ? "戴好两只耳机，选择合适的耳塞。测试期间请保持安静。" : "戴好两只耳机，并选择安静的环境。随后将分别测试左耳和右耳。",
            "CheckingWear" => "请佩戴好两只耳机。",
            "Scanning" => "正在扫描耳道，请保持耳机位置稳定。",
            "ScanCompleted" => "耳道扫描已完成，接下来开始听感测试。",
            "Starting" => "正在准备测试…",
            "Measuring" or "AwaitingAnswer" => $"{(state.Side == 1 ? "左耳" : "右耳")} · {(state.FrequencyHz is { } hz ? $"{hz} Hz" : $"频段 {state.FrequencyType}")} · 第 {Math.Clamp(state.StepIndex + 1, 1, state.StepCount)} / {state.StepCount} 步",
            "Paused" or "Interrupted" => "测试已暂停。请确认佩戴正确，并结束通话或其他占用耳机的活动。",
            "LoadingCurves" or "LoadingFilters" => "正在准备听感结果，请稍候。",
            "Results" or "ResultReady" or "Previewing" => state.FiltersReady ? "测试已完成，请试听并为这份听感配置命名。" : "正在准备听感结果，请稍候。",
            "Completed" => "测试已完成。",
            "Failed" => "测试未能完成，请检查佩戴和连接后重试。",
            "Cancelled" => "测试已取消。",
            _ => "正在处理…"
        };
        if (state.NoiseWarning) _message.Text += "\n环境较吵，请保持安静后继续。";
        var resultPage = state.Stage is "Results" or "ResultReady" or "Previewing";
        var key = (resultPage ? "Results" : state.Stage) + "/" + state.HearingVersion + "/" + (state.StepIndex > 0)
            + "/" + state.FiltersReady + "/" + state.CanPreview + "/" + _model.ResourceSource;
        if (_bodyKey != key)
        {
            _bodyKey = key; _body.Children.Clear(); _level = null; _next = _retryLevel = _save = _resume = null;
            _levelLabel = _previewLabel = _difference = null; _previewButtons.Clear(); _answerButtons.Clear();
            BuildBody();
        }
        if (_level is not null)
        {
            _updatingLevel = true; _level.Value = _model.SelectedLevelIndex; _updatingLevel = false;
            _level.IsEnabled = _model.CanAdjustLevel;
        }
        if (_levelLabel is not null) _levelLabel.Text = _model.LevelPending ? "正在调整音量…"
            : state.ToneAcknowledged && _model.SelectedLevelIndex == state.LevelIndex ? $"音量：第 {_model.SelectedLevelIndex + 1} / 12 档" : "测试音尚未确认，请重试播放。";
        if (_next is not null)
        { _next.IsEnabled = _model.CanContinue; _next.Content = state.StepIndex == state.StepCount - 1 ? "完成测听" : "继续"; }
        if (_retryLevel is not null) _retryLevel.IsEnabled = _model.CanAdjustLevel && !_model.LevelPending;
        if (_save is not null) _save.IsEnabled = _model.CanSave;
        if (_resume is not null) _resume.IsEnabled = state.CanResume && !_model.Busy;
        if (_previewLabel is not null) _previewLabel.Text = state.PreviewMode switch { "Original" => "当前：原音", "Enhanced" => "当前：黄金听感", _ => "选择一种效果开始比较。" };
        foreach (var (button, mode) in _previewButtons) button.IsEnabled = _model.CanPreview && mode != state.PreviewMode;
        foreach (var button in _answerButtons) button.IsEnabled = !_model.Busy && state.Stage == "AwaitingAnswer" && state.ToneAcknowledged;
        if (_difference is not null) _difference.IsVisible = state.HasInterEarDifference;
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
        if (_model.Kind == "hearing" && state.Stage is "Paused" or "Interrupted")
        {
            _body.Children.Add(new TextBlock { Text = "恢复后将继续当前频段，不会自动记录答案。", TextWrapping = TextWrapping.Wrap });
            _resume = AddButton("继续测试", "resume"); return;
        }
        if (_model.Kind == "hearing" && state.Stage is "AwaitingAnswer" or "Measuring")
        {
            if (state.HearingVersion == "V2")
            {
                _body.Children.Add(new TextBlock { Text = "向右调大、向左调小，找到声音刚好消失的位置，再继续。", TextWrapping = TextWrapping.Wrap });
                var slider = new Slider { Minimum = 0, Maximum = 11, TickFrequency = 1, IsSnapToTickEnabled = true, Value = _model.SelectedLevelIndex };
                _level = slider;
                AutomationProperties.SetName(slider, "听感测试音量");
                slider.ValueChanged += async (_, _) =>
                {
                    if (_updatingLevel || !ReferenceEquals(_level, slider)) return;
                    await _model.AdjustLevelAsync((int)Math.Round(slider.Value), false);
                };
                slider.AddHandler(PointerReleasedEvent, async (_, _) =>
                { if (ReferenceEquals(_level, slider)) await _model.AdjustLevelAsync((int)Math.Round(slider.Value), true); }, Avalonia.Interactivity.RoutingStrategies.Bubble, true);
                slider.AddHandler(KeyUpEvent, async (_, e) =>
                { if (ReferenceEquals(_level, slider) && e.Key is Avalonia.Input.Key.Left or Avalonia.Input.Key.Right or Avalonia.Input.Key.Home or Avalonia.Input.Key.End or Avalonia.Input.Key.Up or Avalonia.Input.Key.Down)
                    await _model.AdjustLevelAsync((int)Math.Round(slider.Value), true); }, Avalonia.Interactivity.RoutingStrategies.Bubble, true);
                _body.Children.Add(slider);
                _levelLabel = new TextBlock { TextWrapping = TextWrapping.Wrap }; _body.Children.Add(_levelLabel);
                _next = AddButton(state.StepIndex == state.StepCount - 1 ? "完成测听" : "继续", "next");
                _retryLevel = new Button { Content = "重试播放" };
                _retryLevel.Click += async (_, _) => await _model.AdjustLevelAsync(_model.SelectedLevelIndex, true);
                _body.Children.Add(_retryLevel);
                if (state.StepIndex > 0) AddButton("上一步", "previous");
            }
            else
            {
                _body.Children.Add(new TextBlock { Text = "你能听到测试声音吗？", FontSize = 18 });
                var answers = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
                var heard = new Button { Content = "听到了" }; var unheard = new Button { Content = "没听到" };
                _answerButtons.Add(heard); _answerButtons.Add(unheard);
                heard.IsEnabled = unheard.IsEnabled = state.Stage == "AwaitingAnswer" && state.ToneAcknowledged;
                heard.Click += async (_, _) => await _model.SendAsync("answer", heard: true);
                unheard.Click += async (_, _) => await _model.SendAsync("answer", heard: false);
                answers.Children.Add(heard); answers.Children.Add(unheard); _body.Children.Add(answers);
            }
            return;
        }
        if (_model.Kind == "hearing" && state.Stage is "Results" or "ResultReady" or "Previewing")
        {
            if (!state.FiltersReady) return;
            _difference = new TextBlock { Text = "左右耳在部分频段的选择差异较大，请根据实际听感确认结果。", TextWrapping = TextWrapping.Wrap };
            _body.Children.Add(_difference);
            if (state.CanPreview)
            {
                _body.Children.Add(new TextBlock { Text = "播放你熟悉的音乐，在原音与黄金听感之间切换，比较听音效果。", TextWrapping = TextWrapping.Wrap });
                var previews = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
                foreach (var (text, action, mode) in new[] { ("原音", "preview-original", "Original"), ("黄金听感", "preview-enhanced", "Enhanced"), ("结束试听", "preview-stop", "Stopped") })
                {
                    var button = new Button { Content = text };
                    button.Click += async (_, _) => await _model.SendAsync(action);
                    previews.Children.Add(button); _previewButtons.Add((button, mode));
                }
                _body.Children.Add(previews); _previewLabel = new TextBlock(); _body.Children.Add(_previewLabel);
            }
            var name = new TextBox { Text = _model.Name, PlaceholderText = "配置名称", MaxLength = 40 };
            AutomationProperties.SetName(name, "黄金听感配置名称");
            name.TextChanged += (_, _) => _model.Name = name.Text ?? "";
            _body.Children.Add(name);
            var save = new Button { Content = "保存并启用" };
            _save = save;
            save.Click += async (_, _) =>
            {
                if (_model.ValidateName() is { } error) { _error.Text = error; _error.IsVisible = true; return; }
                var replacement = false;
                if ((_model.NeedsReplacement || _model.ReplacesCurrent) && TopLevel.GetTopLevel(this) is { } owner)
                {
                    var dialog = new FAContentDialog { Title = "保存黄金听感", Content = _model.NeedsReplacement
                            ? "新方案将替换耳机中的当前黄金听感。只有已保存到本机历史的配置可再次选择。若本机已有 10 份配置，保存时会替换最早且未在使用的配置。"
                            : "新方案将替换耳机中的当前黄金听感。只有已保存到本机历史的配置可再次选择。",
                        PrimaryButtonText = "保存", CloseButtonText = "取消", DefaultButton = FAContentDialogButton.Close };
                    if (await dialog.ShowAsync(owner) != FAContentDialogResult.Primary) return;
                    replacement = true;
                }
                await _model.SendAsync("save", allowReplacement: replacement);
            };
            _body.Children.Add(save);
        }
        if (state.Stage == "Failed")
        {
            if (_model.Kind == "hearing" && state.HearingLevels.Count == state.StepCount)
                AddButton("重新获取结果", "retry-curves");
            else AddButton("重试", "start");
        }
    }
    private Button AddButton(string text, string action)
    { var button = new Button { Content = text }; button.Click += async (_, _) => await _model.SendAsync(action); _body.Children.Add(button); return button; }
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
