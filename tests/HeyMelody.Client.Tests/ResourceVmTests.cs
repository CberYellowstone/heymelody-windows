using HeyMelodyWindows.App;

internal static class ResourceVmTests
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        await test("resource settings keep source keys separate and preserve exact user text", async () =>
        {
            var service = new Service(); await using var vm = new MainViewModel(service, new Confirmation());
            await vm.InitializeAsync(); Check(vm.CloudKey == "hey-key");
            vm.CloudKey = "  自己的 key  "; vm.SelectedResourceSource = vm.ResourceSourceOptions[1];
            Check(vm.CloudKey == "wireless-key"); vm.CloudKey = "OEM override";
            vm.SelectedResourceSource = vm.ResourceSourceOptions[0]; Check(vm.CloudKey == "  自己的 key  ");
            vm.UseOnlineResources = false; await vm.SaveResourcePreferencesAsync();
            Check(service.Saved is { Source: "HeyMelody", UseOnlineResources: false, CloudKey: "  自己的 key  ",
                HeyMelodyKey: "  自己的 key  ", WirelessEarphonesKey: "OEM override" });
        });
        await test("resource status pushes preserve unsaved source online preference and key drafts", async () =>
        {
            var service = new EventService(); await using var vm = new MainViewModel(service, new Confirmation());
            await vm.InitializeAsync();
            vm.SelectedResourceSource = vm.ResourceSourceOptions[1]; vm.CloudKey = "  OEM 草稿  "; vm.UseOnlineResources = false;
            vm.AdoptBackendEvent(new() { Kind = "resources", BackendInstanceId = "resource-test",
                Resources = new UiResourceSettings(Status: "资源图像已更新", CloudKey: "hey-key")
                    { HeyMelodyKey = "hey-key", WirelessEarphonesKey = "wireless-key" } });
            Check(vm.SelectedResourceSource.Key == 1 && vm.CloudKey == "  OEM 草稿  " && !vm.UseOnlineResources);
            Check(vm.ResourceStatus == "资源图像已更新");
            await vm.SaveResourcePreferencesAsync();
            Check(service.Saved is { Source: "WirelessEarphones", CloudKey: "  OEM 草稿  ", UseOnlineResources: false });
            vm.AdoptBackendEvent(new() { Kind = "resources", BackendInstanceId = "resource-test", Resources = new() });
            Check(vm.SelectedResourceSource.Key == 0 && vm.CloudKey.Length == 0 && vm.UseOnlineResources);
        });
        await test("saving online preferences retains EQ edits while source switching confirms their discard", async () =>
        {
            var service = new Service(); var confirmation = new Confirmation();
            await using var vm = new MainViewModel(service, confirmation);
            await vm.InitializeAsync(); await vm.ConnectCommand.ExecuteAsync(null);
            vm.Bands[0].Gain = 2; vm.CloudKey = "updated key"; vm.UseOnlineResources = false;
            await vm.SaveResourcePreferencesAsync();
            Check(vm.EqDirty && vm.Bands[0].Gain == 2 && confirmation.Count == 0);
            vm.SelectedResourceSource = vm.ResourceSourceOptions[1];
            await vm.SaveResourcePreferencesAsync();
            Check(!vm.EqDirty && confirmation.Count == 1);
        });
        await test("equalizer drafts follow six and ten reported frequencies and slot capacity", async () =>
        {
            foreach (int count in new[] { 6, 10 })
            {
                var service = new Service { Count = count }; await using var vm = new MainViewModel(service, new Confirmation());
                await vm.InitializeAsync(); await vm.ConnectCommand.ExecuteAsync(null);
                Check(vm.Bands.Count == count && vm.Bands.Select(x => x.FrequencyHz).SequenceEqual(service.Frequencies));
                Check(vm.CanCreateEq); vm.Bands[0].Gain = 3; await vm.SaveEqCommand.ExecuteAsync(null);
                Check(service.Actions.Single().EqualizerEntry is { } eq && eq.Bands.Count == count && eq.Bands[0].Gain == 3);
                service.Capacity = 0; await vm.RefreshStateCommand.ExecuteAsync(null); Check(!vm.CanCreateEq);
            }
        });
        await test("hearing selection uses signed device UID and setup is available without saved profiles", async () =>
        {
            var service = new Service(); await using var vm = new MainViewModel(service, new Confirmation());
            await vm.InitializeAsync(); await vm.ConnectCommand.ExecuteAsync(null);
            Check(vm.CanSetupHearing && !vm.CanSelectHearingProfile);
            await vm.CreateCalibrationAsync("hearing"); Check(service.Created == "hearing");
            service.Profiles = [new(0, "零编号配置", true, true), new(int.MinValue, "负编号配置", false, true)];
            await vm.RefreshStateCommand.ExecuteAsync(null);
            Check(vm.CanSelectHearingProfile && vm.HearingProfiles.Count == 3 && vm.HearingProfiles[0].Uid is null);
            await vm.SelectHearingProfileAsync(vm.HearingProfiles.Single(x => x.Uid == int.MinValue));
            Check(service.SelectedUid == int.MinValue);
            await vm.SelectHearingProfileAsync(vm.HearingProfiles[0]); Check(service.SelectedUid is null);
        });
    }
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Resource VM regression failed."); }
    internal static async Task RunArtworkAsync(Func<string, Func<Task>, Task> test)
    {
        await test("re-download shows progress, keeps drafts and reports partial failures", async () =>
        {
            var service = new RedownloadService(); await using var vm = new MainViewModel(service, new Confirmation());
            await vm.InitializeAsync(); vm.CloudKey = "  草稿  ";
            Check(vm.CanRedownloadResources && !vm.IsRedownloadingResources && vm.ConfiguredResourceSourceName == "欢律");
            var run = vm.RedownloadResourcesAsync();
            Check(vm.IsRedownloadingResources && !vm.CanRedownloadResources && service.Redownloads == 1);
            await vm.RedownloadResourcesAsync(); Check(service.Redownloads == 1);
            service.Pending.SetResult(new(new UiResourceSettings(CloudKey: "hey-key", Status: "使用已下载的在线资源")
                { HeyMelodyKey = "hey-key", WirelessEarphonesKey = "wireless-key" }, false, 1, 2, "2 项在线资源重新下载失败，已保留原有资源。"));
            await run;
            Check(!vm.IsRedownloadingResources && vm.RedownloadFailed && vm.RedownloadMessage.StartsWith("2 项", StringComparison.Ordinal)
                && vm.CloudKey == "  草稿  " && vm.ResourceStatus == "使用已下载的在线资源" && vm.LogText.Contains("2 项", StringComparison.Ordinal));
            service.Failure = new IOException("蓝牙后台暂时不可用，请等待重连。");
            await vm.RedownloadResourcesAsync();
            Check(vm.RedownloadFailed && vm.RedownloadMessage == "蓝牙后台暂时不可用，请等待重连。" && vm.CanRedownloadResources);
        });
        await test("re-download is disabled while online resources are off or unsupported", async () =>
        {
            var service = new RedownloadService { Online = false }; await using var vm = new MainViewModel(service, new Confirmation());
            await vm.InitializeAsync();
            Check(!vm.CanRedownloadResources && !vm.OnlineResourcesConfigured);
            await vm.RedownloadResourcesAsync(); Check(service.Redownloads == 0);
            await using var preview = new MainViewModel(new PreviewService(), new Confirmation());
            await preview.InitializeAsync(); Check(!preview.CanRedownloadResources && !preview.ResourceServiceAvailable);
        });
        await test("artwork follows the Host-confirmed source and an epoch that only advances", async () =>
        {
            var service = new Service(); await using var vm = new MainViewModel(service, new Confirmation());
            await vm.InitializeAsync(); vm.SelectedResourceSource = vm.ResourceSourceOptions[1];
            Check(vm.ResourceSource == "HeyMelody");
            var events = new EventService(); await using var pushed = new MainViewModel(events, new Confirmation());
            await pushed.InitializeAsync(); Check(pushed.ArtworkEpoch == "resource-test:0");
            void Push(string backend, long revision, long resources) => pushed.AdoptBackendEvent(new() { Kind = "snapshot", BackendInstanceId = backend,
                Snapshot = new UiSnapshot { Identity = "other", BackendInstanceId = backend, DeviceGeneration = 1, Revision = revision, ResourceRevision = resources } });
            Push("resource-test", 1, 5); Check(pushed.ArtworkEpoch == "resource-test:5");
            Push("resource-test", 2, 3); Check(pushed.ArtworkEpoch == "resource-test:5");
            Push("restarted", 1, 1); Check(pushed.ArtworkEpoch == "restarted:1");
        });
    }
    private sealed class Confirmation : IConfirmationService
    { internal int Count; public Task<bool> ConfirmAsync(string title, string message, CancellationToken token) { Count++; return Task.FromResult(true); } }
    private class Service : IHeadsetService, IResourceSettingsService, ICalibrationService
    {
        internal int Count = 6, Capacity = 3;
        internal int[] Frequencies => Count == 6 ? [62,250,1000,4000,8000,16000] : [31,62,125,250,500,1000,2000,4000,8000,16000];
        internal List<UiAction> Actions = [];
        internal IReadOnlyList<UiHearingProfile> Profiles = [];
        internal string? Created;
        internal int? SelectedUid;
        internal UiResourceSettings? Saved;
        public Task<IReadOnlyList<UiDevice>> EnumerateAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<UiDevice>>([new("peer", "耳机", true)]);
        private UiSnapshot Snapshot() => new() { Identity = "peer", Connected = true, SupportedModel = true, ConnectionState = ConnectionState.Connected,
            ProductId = Count == 6 ? "067410" : "06EC10", CanSetCustomEqualizer = true, CanSetupHearing = true,
            CustomEqualizerCapacity = Capacity, EqualizerFrequencies = Frequencies, HearingProfiles = Profiles, HearingEnabled = true };
        public Task<UiSnapshot> ConnectAsync(string identity, CancellationToken token) => Task.FromResult(Snapshot());
        public Task DisconnectAsync(CancellationToken token) => Task.CompletedTask;
        public Task<UiSnapshot> RefreshAsync(CancellationToken token) => Task.FromResult(Snapshot());
        public Task<UiApplyResult> ApplyAsync(UiAction action, CancellationToken token)
        {
            Actions.Add(action); var after = Snapshot();
            if (action.EqualizerEntry is { } eq) after.EqualizerEntries = [eq with { Id = 1, Selected = true }];
            return Task.FromResult(new UiApplyResult { BackendReceiptReceived = true, ConnectionState = ConnectionState.Connected,
                TargetSubmitted = true, SubmissionAttempted = true, Accepted = true, ReadbackMatches = true, ObservedEqualizerId = 1, After = after });
        }
        public Task<UiResourceSettings> GetResourceSettingsAsync(CancellationToken token) => Task.FromResult(new UiResourceSettings(CloudKey: "hey-key")
        { HeyMelodyKey = "hey-key", WirelessEarphonesKey = "wireless-key" });
        public Task<UiResourceSettings> SaveResourceSettingsAsync(UiResourceSettings settings, CancellationToken token) { Saved = settings; return Task.FromResult(settings); }
        public Task<IReadOnlyList<UiHearingProfile>> GetHearingProfilesAsync(string identity, long generation, CancellationToken token) => Task.FromResult(Profiles);
        public Task<UiApplyResult> SelectHearingProfileAsync(string identity, long generation, int? uid, CancellationToken token)
        { SelectedUid = uid; return Task.FromResult(new UiApplyResult { After = Snapshot(), ReadbackMatches = true, Message = "已应用" }); }
        public Task<UiCalibrationState> CreateCalibrationAsync(string identity, long generation, string kind, CancellationToken token)
        { Created = kind; return Task.FromResult(new UiCalibrationState { Handle = new string('a',32), Workflow = kind }); }
        public Task<UiCalibrationState> GetCalibrationAsync(string handle, CancellationToken token) => throw new NotSupportedException();
        public Task<UiCalibrationState> AdvanceCalibrationAsync(string handle, long revision, UiCalibrationInput input, CancellationToken token) => throw new NotSupportedException();
        public Task<UiCalibrationState> CancelCalibrationAsync(string handle, CancellationToken token) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class RedownloadService : Service, IResourceSettingsService
    {
        internal int Redownloads; internal bool Online = true; internal Exception? Failure;
        internal TaskCompletionSource<UiResourceRedownloadResult> Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public new Task<UiResourceSettings> GetResourceSettingsAsync(CancellationToken token) => Task.FromResult(
            new UiResourceSettings(UseOnlineResources: Online, CloudKey: "hey-key") { HeyMelodyKey = "hey-key", WirelessEarphonesKey = "wireless-key" });
        public Task<UiResourceRedownloadResult> RedownloadResourcesAsync(CancellationToken token)
        { Redownloads++; return Failure is { } failure ? Task.FromException<UiResourceRedownloadResult>(failure) : Pending.Task; }
    }
    private sealed class EventService : Service, IHeadsetEventService
    {
        public event Action<BackendEvent>? EventReceived { add { } remove { } }
        public Task<BackendSubscription> SubscribeAsync(string role, CancellationToken token) =>
            Task.FromResult(new BackendSubscription { BackendInstanceId = "resource-test" });
        public Task<UiSnapshot> SelectAsync(string identity, CancellationToken token) => ConnectAsync(identity, token);
        public Task<UiSnapshot> PauseAsync(string identity, CancellationToken token) => ConnectAsync(identity, token);
        public Task<UiSnapshot> ResumeAsync(string identity, CancellationToken token) => ConnectAsync(identity, token);
        public Task<UiSnapshot> RefreshAsync(string identity, RefreshScope scope, CancellationToken token) => RefreshAsync(token);
        public Task<UiApplyResult> ApplyAsync(string identity, long generation, UiAction action, CancellationToken token) => ApplyAsync(action, token);
        public Task AcknowledgeExitAsync(string id, bool approved, CancellationToken token) => Task.CompletedTask;
        public Task ActivateMainUiAsync(CancellationToken token) => Task.CompletedTask;
    }
}
