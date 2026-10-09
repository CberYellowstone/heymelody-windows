using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HeyMelodyWindows.App;

internal static class SettingsManagementTests
{
    public static int Run(HeadlessUnitTestSession session)
    {
        if (!OperatingSystem.IsWindows()) return 0;
        session.Dispatch<bool>(async () =>
        {
            var service = new CountingService();
            var confirmation = new AcceptConfirmation();
            await using var vm = new MainViewModel(service, confirmation);
            await vm.InitializeAsync();
            await vm.ConnectCommand.ExecuteAsync(null);
            vm.EqName = "管理前的草稿";
            var calls = 0;
            var view = new MainView { DataContext = vm };
            view.ManageDevicesRequested = async () =>
            {
                if (!await vm.PrepareDeviceManagementAsync()) throw new Exception("Management preparation failed.");
                calls++;
                if (!await vm.CompleteDeviceManagementAsync(saved: false)) throw new Exception("Cancellation lost the old list.");
            };
            var window = new Window { Width = 1280, Height = 900, Content = view };
            try
            {
                window.Show(); Dispatcher.UIThread.RunJobs();
                var settings = view.OpenSettingsDialogAsync();
                await Task.Delay(30); Dispatcher.UIThread.RunJobs();
                var button = window.GetVisualDescendants().OfType<Button>()
                    .Single(button => Equals(button.Content, "管理设备"));
                if (!button.IsEnabled) throw new Exception("The management entry must be available.");
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await settings.WaitAsync(TimeSpan.FromSeconds(3));
                if (calls != 1 || confirmation.Calls != 1 || service.Disconnects != 1
                    || service.Enumerations != 1 || service.Applies != 0 || vm.ManagingDevices || vm.EqDirty)
                    throw new Exception("Settings must release Dialogs before the real discard confirmation and preserve cancellation semantics.");
            }
            finally { await view.DismissSettingsDialogAsync(); window.Close(); }
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();
        Console.WriteLine("PASS settings releases its dialog before opening device management");
        return 1;
    }

    private sealed class AcceptConfirmation : IConfirmationService
    {
        public int Calls { get; private set; }
        public Task<bool> ConfirmAsync(string title, string message, CancellationToken token)
        { Calls++; return Task.FromResult(true); }
    }

    private sealed class CountingService : IHeadsetService
    {
        private readonly PreviewService _inner = new();
        public int Enumerations, Disconnects, Applies;
        public Task<IReadOnlyList<UiDevice>> EnumerateAsync(CancellationToken token)
        { Enumerations++; return _inner.EnumerateAsync(token); }
        public Task<UiSnapshot> ConnectAsync(string identity, CancellationToken token) => _inner.ConnectAsync(identity, token);
        public Task DisconnectAsync(CancellationToken token) { Disconnects++; return Task.CompletedTask; }
        public Task<UiSnapshot> RefreshAsync(CancellationToken token) => _inner.RefreshAsync(token);
        public Task<UiApplyResult> ApplyAsync(UiAction action, CancellationToken token)
        { Applies++; return _inner.ApplyAsync(action, token); }
        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}
