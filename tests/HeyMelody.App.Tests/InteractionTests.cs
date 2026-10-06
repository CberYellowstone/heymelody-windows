using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using HeyMelodyWindows.App;

internal static class InteractionTests
{
    public static int Run(HeadlessUnitTestSession session)
    {
        Run("background reads preserve editing and queued mouse actions", BackgroundInteractionAsync);
        Run("settings waits for the shared dialog owner and cancels cleanly", SettingsSerializationAsync);
        Run("retry and alert dialogs use the required buttons", DialogButtonsAsync);
        return 3;

        void Run(string name, Func<Task> test)
        {
            session.Dispatch<bool>(async () => { await test(); return true; }, CancellationToken.None)
                .GetAwaiter().GetResult();
            Console.WriteLine("PASS " + name);
        }
    }

    private static async Task BackgroundInteractionAsync()
    {
        var service = new BlockingService();
        await using var vm = new MainViewModel(service, new NoConfirm());
        await vm.InitializeAsync();
        await vm.ConnectCommand.ExecuteAsync(null);
        var view = new MainView { DataContext = vm };
        var window = CreateWindow(view);
        try
        {
            window.Show(); view.ExpandAllSettings(); Dispatcher.UIThread.RunJobs();
            service.BlockNextRead = true;
            var read = vm.PollAsync();
            await service.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Dispatcher.UIThread.RunJobs();
            Check(vm.IsRefreshing && !vm.Busy, "Background read has independent activity state.");
            var anc = view.FindControl<ComboBox>("AncCombo")!;
            var options = anc.ItemsSource;
            Check(anc.IsEnabled && view.FindControl<ItemsControl>("BandEditor")!.IsEnabled,
                "Selectors and EQ editor remain available during a read.");
            Check(view.FindControl<ToggleSwitch>("GameSwitch")!.IsEnabled
                && view.FindControl<Button>("AppSettingsButton")!.IsEnabled,
                "Switches and settings remain available during a read.");
            var settings = view.OpenSettingsDialogAsync();
            await Task.Delay(20); Dispatcher.UIThread.RunJobs();
            Check(!settings.IsCompleted && window.GetVisualDescendants().OfType<FAContentDialog>()
                .Any(dialog => Equals(dialog.Title, "应用设置")), "Settings opens during the blocked background read.");
            await view.DismissSettingsDialogAsync(); await settings;
            view.ResumeSettingsDialogs();
            anc.SelectedItem = vm.AncOptions.Single(option => option.Key == 0x0003);
            view.FindControl<TextBox>("EqNameEditor")!.Text = "读取期间的草稿";
            view.FindControl<ItemsControl>("BandEditor")!.GetVisualDescendants().OfType<Slider>().First().Value = 4;
            Dispatcher.UIThread.RunJobs();
            var apply = view.GetVisualDescendants().OfType<Button>()
                .Single(button => ReferenceEquals(button.Command, vm.ApplyAncCommand));
            Click(window, apply);
            Check(service.Actions.Count == 0 && vm.ApplyAncCommand.IsRunning,
                "Click is admitted while the current read owns the wire.");
            var action = vm.ApplyAncCommand.ExecutionTask!;
            anc.SelectedItem = vm.AncOptions.Single(option => option.Key == 0x0107);
            Dispatcher.UIThread.RunJobs();
            Check(anc.IsEnabled && !apply.IsEnabled, "Editor stays available while its submission is pending.");
            service.ReleaseRead.TrySetResult();
            await action.WaitAsync(TimeSpan.FromSeconds(3));
            await read.WaitAsync(TimeSpan.FromSeconds(3));
            Dispatcher.UIThread.RunJobs();
            Check(service.Actions.Count == 1 && service.Actions[0] is { Kind: "Anc", Value: 0x0003 },
                "Queued click sends its captured protocol key once.");
            Check(vm.SelectedAnc?.Key == 0x0107 && vm.EqDirty && vm.EqName == "读取期间的草稿"
                && vm.Bands[0].Gain == 4, "Newer selections and draft survive readback.");
            Check(ReferenceEquals(options, anc.ItemsSource), "Unchanged options keep the existing item source.");
        }
        finally { service.ReleaseRead.TrySetResult(); window.Close(); }
    }

    private static async Task SettingsSerializationAsync()
    {
        await using var vm = new MainViewModel(new PreviewService(), new NoConfirm(), preview: true);
        await vm.InitializeAsync();
        var view = new MainView { DataContext = vm };
        var window = CreateWindow(view);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? settings = null;
        Task? owner = null;
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            owner = vm.Dialogs.RunAsync(token => release.Task.WaitAsync(token));
            settings = view.OpenSettingsDialogAsync();
            Dispatcher.UIThread.RunJobs();
            Check(!settings.IsCompleted && !window.GetVisualDescendants().OfType<FAContentDialog>()
                .Any(dialog => Equals(dialog.Title, "应用设置")), "Settings waits behind the current dialog owner.");
            await view.DismissSettingsDialogAsync().WaitAsync(TimeSpan.FromSeconds(3));
            Check(settings.IsCompleted, "Closing cancels settings waiting for the dialog owner.");
            release.TrySetResult(); await owner;
            view.ResumeSettingsDialogs();
            settings = view.OpenSettingsDialogAsync();
            await Task.Delay(20); Dispatcher.UIThread.RunJobs();
            Check(!settings.IsCompleted && window.GetVisualDescendants().OfType<FAContentDialog>()
                .Any(dialog => Equals(dialog.Title, "应用设置")), "Settings can open after cancelling the queued interaction.");
            await view.DismissSettingsDialogAsync().WaitAsync(TimeSpan.FromSeconds(3));
            await settings;
        }
        finally
        {
            release.TrySetResult();
            await view.DismissSettingsDialogAsync();
            if (owner is not null) await owner;
            window.Close();
        }
    }

    private static Task DialogButtonsAsync()
    {
        var retry = WindowConfirmationService.CreateConfirmation("重试设置", "再次发送？", "确认");
        Check(retry.PrimaryButtonText == "确认" && retry.CloseButtonText == "取消"
            && retry.DefaultButton == FAContentDialogButton.Close, "Retry confirmation defaults to cancel.");
        var alert = WindowConfirmationService.CreateAlert("设置未完成", "请核对设备状态。");
        Check(string.IsNullOrEmpty(alert.PrimaryButtonText) && string.IsNullOrEmpty(alert.SecondaryButtonText)
            && alert.CloseButtonText == "确定" && alert.DefaultButton == FAContentDialogButton.Close,
            "Failure alert has only its acknowledgement button.");
        return Task.CompletedTask;
    }

    private static Window CreateWindow(MainView view) => new()
        { Width = 1280, Height = 900, Content = view, WindowDecorations = WindowDecorations.None };

    private static void Click(Window window, Control control)
    {
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class BlockingService : IHeadsetService
    {
        private readonly PreviewService _inner = new();
        private UiSnapshot? _snapshot;
        public bool BlockNextRead { get; set; }
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<UiAction> Actions { get; } = [];
        public Task<IReadOnlyList<UiDevice>> EnumerateAsync(CancellationToken token) => _inner.EnumerateAsync(token);
        public async Task<UiSnapshot> ConnectAsync(string identity, CancellationToken token) => _snapshot = await _inner.ConnectAsync(identity, token);
        public Task DisconnectAsync(CancellationToken token) => Task.CompletedTask;
        public Task<UiSnapshot> RefreshAsync(CancellationToken token) => RefreshAsync(RefreshScope.Full, token);
        public async Task<UiSnapshot> RefreshAsync(RefreshScope scope, CancellationToken token)
        {
            if (BlockNextRead)
            {
                BlockNextRead = false; ReadStarted.TrySetResult();
                await ReleaseRead.Task.WaitAsync(token);
            }
            return _snapshot!;
        }
        public Task<UiApplyResult> ApplyAsync(UiAction action, CancellationToken token)
        {
            Actions.Add(action);
            if (action.Kind == "Anc") _snapshot!.Anc = action.Value;
            return Task.FromResult(new UiApplyResult
            {
                BackendReceiptReceived = true, Accepted = true, SubmissionAttempted = true,
                TargetSubmitted = true, ConnectionState = ConnectionState.Connected,
                ReadbackMatches = true, After = _snapshot
            });
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
