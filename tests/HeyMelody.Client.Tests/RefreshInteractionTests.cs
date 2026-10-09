using HeyMelodyWindows.App;

internal static class RefreshInteractionTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    public static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        await test("background polling keeps editors active and writes take the next communication slot", async () =>
        {
            var service = new Service(); var entered = Signal(); var release = Signal();
            service.Read = async (scope, count, token) =>
            {
                if (count == 1) { entered.TrySetResult(); await release.Task.WaitAsync(token); }
                return service.Snapshot();
            };
            await using var vm = await Connected(service);
            var poll = vm.PollAsync(); await entered.Task.WaitAsync(Deadline);
            Check(vm.IsRefreshing && !vm.Busy && vm.CanChooseAnc && vm.CanEditEq, "A background query must not lock editors");
            var write = vm.ToggleGameCommand.ExecuteAsync(true);
            Check(service.Actions.Count == 0, "The active query retains its communication slot");
            release.SetResult(); await Task.WhenAll(poll, write).WaitAsync(Deadline);
            Check(service.Actions.Count == 1 && service.MaximumConcurrent == 1, "One accepted write must be sent exactly once without overlap");
            Check(service.Events.IndexOf("apply:GameMode") == 1, "The queued write must precede the next poll segment");
        });
        await test("editing during a blocked poll preserves selections and the EQ draft", async () =>
        {
            var service = new Service(); var entered = Signal(); var release = Signal();
            service.Read = async (_, count, token) => { if (count == 1) { entered.SetResult(); await release.Task.WaitAsync(token); } return service.Snapshot(); };
            await using var vm = await Connected(service);
            var poll = vm.PollAsync(); await entered.Task.WaitAsync(Deadline);
            vm.SelectedAnc = vm.AncOptions.Last(); vm.SelectedPreset = vm.PresetOptions.Last(); vm.SelectedSpatial = vm.SpatialOptions.Last();
            vm.EqName = "刷新期间的草稿"; vm.Bands[0].Gain = 5;
            release.SetResult(); await poll.WaitAsync(Deadline);
            Check(vm.SelectedAnc?.Key == 0x0107 && vm.SelectedPreset?.Key == 7 && vm.SelectedSpatial?.Key == 2, "Pending selectors must survive every query segment");
            Check(vm.EqDirty && vm.EqName == "刷新期间的草稿" && vm.Bands[0].Gain == 5 && service.Actions.Count == 0, "Polling must retain the local draft and remain read only");
        });
        await test("unchanged poll results retain option and EQ collections", async () =>
        {
            var service = new Service(); await using var vm = await Connected(service);
            var anc = vm.AncOptions; var presets = vm.PresetOptions; var spatial = vm.SpatialOptions; var entries = vm.EqEntries;
            await vm.PollAsync();
            Check(ReferenceEquals(anc, vm.AncOptions) && ReferenceEquals(presets, vm.PresetOptions) && ReferenceEquals(spatial, vm.SpatialOptions), "Unchanged options must not replace an open dropdown's items");
            Check(ReferenceEquals(entries, vm.EqEntries), "Unchanged EQ entries must retain their collection");
        });
        await test("new and selected EQ drafts do not wait for a background query", async () =>
        {
            var service = new Service(); var entered = Signal(); var release = Signal();
            service.Read = async (_, count, token) => { if (count == 1) { entered.SetResult(); await release.Task.WaitAsync(token); } return service.Snapshot(); };
            await using var vm = await Connected(service);
            var poll = vm.PollAsync(); await entered.Task.WaitAsync(Deadline);
            await vm.SelectEqEntryAsync(vm.EqEntries[1]).WaitAsync(Deadline);
            Check(vm.SelectedEqEntry?.Id == 2, "Local EQ selection must execute while the query is blocked");
            await vm.NewEqCommand.ExecuteAsync(null).WaitAsync(Deadline);
            vm.EqName = "新草稿"; vm.Bands[1].Gain = 3;
            Check(vm.SelectedEqEntry is null && vm.EqDirty, "New EQ remains a local draft");
            release.SetResult(); await poll.WaitAsync(Deadline);
            Check(vm.SelectedEqEntry is null && vm.EqName == "新草稿" && vm.Bands[1].Gain == 3 && service.Actions.Count == 0, "Poll completion must not replace a new local draft");
        });
        foreach (bool creatingNew in new[] { false, true })
            await test("stale local EQ " + (creatingNew ? "creation" : "selection") + " consent cannot change a suspended session", async () =>
            {
                var service = new Service();
                var dialogs = new Dialogs { ConfirmationRelease = Answer(), ConfirmIgnoresCancellation = creatingNew };
                await using var vm = await Connected(service, dialogs); vm.EqName = "关闭前保留的草稿"; vm.Bands[0].Gain = 4;
                var edit = creatingNew ? vm.NewEqCommand.ExecuteAsync(null) : vm.SelectEqEntryAsync(vm.EqEntries[1]);
                await dialogs.ConfirmationEntered.Task.WaitAsync(Deadline);
                vm.SuspendActionsForClose(); dialogs.ConfirmationRelease.SetResult(true);
                await edit.WaitAsync(Deadline);
                Check(vm.Closing && vm.SelectedEqEntry?.Id == 1 && vm.EqDirty && vm.EqName == "关闭前保留的草稿" && vm.Bands[0].Gain == 4,
                    "Advancing the session while awaiting discard consent must preserve the old draft and ignore late approval");
                Check(service.Actions.Count == 0, "Cancelled local editing must not send a setting");
            });
        await test("a pending setting blocks its submission group while unrelated controls remain available", async () =>
        {
            var service = new Service(); var entered = Signal(); var release = Signal();
            service.Write = async (action, count, token) => { if (count == 1) { entered.SetResult(); await release.Task.WaitAsync(token); } return service.Success(action); };
            await using var vm = await Connected(service); vm.SelectedAnc = vm.AncOptions.Last();
            var anc = vm.ApplyAncCommand.ExecuteAsync(null); await entered.Task.WaitAsync(Deadline);
            Check(!vm.CanSetAnc && vm.CanChooseAnc && vm.CanSetGameMode && vm.CanEditEq, "Only the pending submission group is unavailable");
            var game = vm.ToggleGameCommand.ExecuteAsync(true);
            Check(service.Actions.Count == 1, "The second feature must queue without concurrent Bluetooth work");
            release.SetResult(); await Task.WhenAll(anc, game).WaitAsync(Deadline);
            Check(service.Actions.Select(x => x.Kind).SequenceEqual(new[] { "Anc", "GameMode" }) && service.MaximumConcurrent == 1, "The unrelated setting must execute in user order");
        });
        await test("a completed ANC submission retains a newer unsubmitted choice", async () =>
        {
            var service = new Service(); var entered = Signal(); var release = Signal();
            service.Write = async (action, _, token) => { entered.SetResult(); await release.Task.WaitAsync(token); return service.Success(action); };
            await using var vm = await Connected(service); vm.SelectedAnc = vm.AncOptions.Last();
            var apply = vm.ApplyAncCommand.ExecuteAsync(null); await entered.Task.WaitAsync(Deadline);
            vm.SelectedAnc = vm.AncOptions.First();
            release.SetResult(); await apply.WaitAsync(Deadline); await vm.PollAsync();
            Check(service.Actions.Single().Value == 0x0107 && vm.SelectedAnc?.Key == 0x0104, "The captured request and later local choice have independent revisions");
        });
        await test("a successful EQ save preserves newer edits and rebases a newly allocated entry", async () =>
        {
            var service = new Service(); var entered = Signal(); var release = Signal();
            service.Write = async (action, _, token) => { entered.SetResult(); await release.Task.WaitAsync(token); return service.Success(action); };
            await using var vm = await Connected(service); await vm.NewEqCommand.ExecuteAsync(null);
            vm.EqName = "提交的名称"; vm.Bands[0].Gain = 2;
            var save = vm.SaveEqCommand.ExecuteAsync(null); await entered.Task.WaitAsync(Deadline);
            vm.EqName = "继续编辑的名称"; vm.Bands[0].Gain = 5;
            release.SetResult(); await save.WaitAsync(Deadline);
            Check(service.Actions.Single().EqualizerEntry is { Id: 0, Name: "提交的名称" } submitted && submitted.Bands[0].Gain == 2, "The queued save must own an immutable payload");
            Check(vm.EqDirty && vm.EqName == "继续编辑的名称" && vm.Bands[0].Gain == 5 && vm.SelectedEqEntry?.Id == 3, "A newly allocated ID must rebase the retained newer draft");
            service.Write = null; await vm.SaveEqCommand.ExecuteAsync(null);
            Check(service.Actions.Count == 2 && service.Actions[1].EqualizerEntry?.Id == 3 && !vm.EqDirty, "Saving the retained draft must update the known entry instead of allocating another");
        });
        await test("a complete failed receipt with connected fresh state retries a safe scalar once", async () =>
        {
            var service = new Service(); var dialogs = new Dialogs();
            service.Write = (action, count, _) => Task.FromResult(count == 1 ? service.Failure(ApplyFailure.PeerRejected) : service.Success(action));
            await using var vm = await Connected(service, dialogs);
            await vm.ToggleGameCommand.ExecuteAsync(true);
            Check(service.Actions.Count == 2 && service.Actions.All(x => x is { Kind: "GameMode", Value: 1 }), "One safe retry must replay the captured absolute target");
            Check(dialogs.Retries == 0 && dialogs.Alerts == 0 && service.Reads.Count != 0 && vm.GameMode == true, "Retry eligibility requires a fresh read and no unnecessary question");
            Check(vm.LastApplyAttempts is [{ FailureKind: ApplyFailure.PeerRejected, Accepted: false }, { Accepted: true }], "Both original receipts must remain available after a successful retry");
        });
        await test("an unknown target ACK with a complete receipt does not resend an already reached target", async () =>
        {
            var service = new Service(); var dialogs = new Dialogs();
            service.Write = (_, _, _) =>
            {
                service.Game = true;
                var failure = service.Failure(ApplyFailure.TransportUnknown); failure.AcknowledgementUnknown = true;
                failure.Accepted = false; failure.After = null;
                return Task.FromResult(failure);
            };
            await using var vm = await Connected(service, dialogs); await vm.ToggleGameCommand.ExecuteAsync(true);
            Check(service.Actions.Count == 1 && vm.GameMode == true && dialogs.Retries == 0, "Fresh matching state completes synchronization without another setter");
            Check(vm.LastApplyAttempts is [{ Accepted: false, AcknowledgementUnknown: true }], "Matching readback must not fabricate a successful peer acknowledgment");
        });
        foreach (var failureKind in new[] { ApplyFailure.TransportUnknown, ApplyFailure.PeerRejected })
            await test("a fresh ANC match after " + failureKind + " releases the submitted choice for later phone changes", async () =>
            {
                var service = new Service();
                service.Write = (action, _, _) =>
                {
                    service.Anc = action.Value;
                    var failure = service.Failure(failureKind); failure.After = null;
                    failure.AcknowledgementUnknown = failureKind == ApplyFailure.TransportUnknown;
                    return Task.FromResult(failure);
                };
                await using var vm = await Connected(service); vm.SelectedAnc = vm.AncOptions.Last();
                await vm.ApplyAncCommand.ExecuteAsync(null);
                Check(service.Actions.Count == 1 && vm.SelectedAnc?.Key == 0x0107, "Fresh matching ANC must synchronize without a second setter");
                service.Anc = 0x0104; await vm.PollAsync();
                Check(vm.SelectedAnc?.Key == 0x0104, "An unchanged local edit version must release pending selection after synchronization");
                Check(vm.LastApplyAttempts is [{ Accepted: false }] && vm.LastApplyAttempts[0].FailureKind == failureKind, "Synchronization must preserve the original unknown or rejected acknowledgment");
            });
        await test("an unknown EQ ACK with matching readback commits the same draft version without changing its receipt", async () =>
        {
            var service = new Service();
            service.Write = (action, _, _) =>
            {
                var result = service.Success(action); result.Accepted = false; result.AcknowledgementUnknown = true;
                result.FailureKind = ApplyFailure.TransportUnknown; return Task.FromResult(result);
            };
            await using var vm = await Connected(service); vm.EqName = "实际已保存"; vm.Bands[0].Gain = 4;
            await vm.SaveEqCommand.ExecuteAsync(null);
            Check(!vm.EqDirty && vm.EqName == "实际已保存" && vm.Bands[0].Gain == 4 && service.Actions.Count == 1, "A verified EQ readback must release an unchanged draft without another setter");
            Check(vm.LastApplyAttempts is [{ Accepted: false, AcknowledgementUnknown: true, ReadbackMatches: true }], "Verified readback must preserve the unknown ACK in its receipt");
        });
        await test("an unknown EQ ACK with matching readback preserves a newer draft version", async () =>
        {
            var service = new Service(); var entered = Signal(); var release = Signal();
            service.Write = async (action, _, token) =>
            {
                entered.SetResult(); await release.Task.WaitAsync(token);
                var result = service.Success(action); result.Accepted = false; result.AcknowledgementUnknown = true;
                result.FailureKind = ApplyFailure.TransportUnknown; return result;
            };
            await using var vm = await Connected(service); vm.EqName = "已提交版本"; vm.Bands[0].Gain = 2;
            var save = vm.SaveEqCommand.ExecuteAsync(null); await entered.Task.WaitAsync(Deadline);
            vm.EqName = "更晚的草稿"; vm.Bands[0].Gain = 5;
            release.SetResult(); await save.WaitAsync(Deadline); await vm.PollAsync();
            Check(vm.EqDirty && vm.EqName == "更晚的草稿" && vm.Bands[0].Gain == 5 && service.Actions.Count == 1, "Matching an older submitted EQ must retain newer local edits");
            Check(vm.LastApplyAttempts is [{ Accepted: false, AcknowledgementUnknown: true, ReadbackMatches: true }], "Preserving a newer draft must not rewrite its older unknown ACK");
        });
        await test("a failed new EQ save requires explicit retry consent", async () =>
        {
            var service = new Service(); var dialogs = new Dialogs { RetryAnswer = true };
            service.Write = (action, count, _) => Task.FromResult(count == 1 ? service.Failure(ApplyFailure.ReadbackUnavailable) : service.Success(action));
            await using var vm = await Connected(service, dialogs); await vm.NewEqCommand.ExecuteAsync(null);
            vm.EqName = "新的均衡器"; vm.Bands[0].Gain = 4; await vm.SaveEqCommand.ExecuteAsync(null);
            Check(dialogs.Retries == 1 && service.Actions.Count == 2 && vm.SelectedEqEntry?.Id == 3 && !vm.EqDirty, "A new entry may only be retried following explicit confirmation");
        });
        foreach (bool changedDuringSelect in new[] { false, true })
            await test("new EQ retry reuses one created entry and " + (changedDuringSelect ? "rejects changed content" : "verifies original content"), async () =>
            {
                var service = new Service(); var dialogs = new Dialogs { RetryAnswer = true };
                service.Write = (action, count, _) =>
                {
                    if (count == 1)
                    {
                        service.Success(action);
                        service.Entries = service.Entries.Select(entry => entry with { Selected = false }).ToList();
                        var result = service.Failure(ApplyFailure.ReadbackUnavailable);
                        result.AcknowledgementUnknown = true; result.ObservedEqualizerId = 3;
                        return Task.FromResult(result);
                    }
                    if (changedDuringSelect)
                        service.Entries = service.Entries.Select(entry => entry.Id != 3 ? entry : entry with
                        {
                            Name = "手机修改后的内容",
                            Bands = entry.Bands.Select((band, index) => index == 0 ? band with { Gain = 1 } : band).ToArray()
                        }).ToList();
                    return Task.FromResult(service.Success(action));
                };
                await using var vm = await Connected(service, dialogs); await vm.NewEqCommand.ExecuteAsync(null);
                vm.EqName = "创建后未激活"; vm.Bands[0].Gain = 4;
                await vm.SaveEqCommand.ExecuteAsync(null);
                Check(service.Actions is [{ Kind: "CustomEqualizer", EqualizerEntry.Id: 0 }, { Kind: "CustomSelect", Value: 3 }] && dialogs.Retries == 1,
                    "A uniquely observed created EQ must be selected instead of created a second time");
                if (changedDuringSelect)
                {
                    Check(dialogs.Alerts == 1 && vm.StatusWarning && vm.EqDirty && vm.EqName == "创建后未激活" && vm.Bands[0].Gain == 4,
                        "Selecting an externally changed entry must not acknowledge the original save or clear its draft");
                    Check(service.Reads.Last() == RefreshScope.Full && service.Actions.Count == 2, "Changed content must trigger full synchronization after the final alert with no third setter");
                    Check(vm.LastApplyAttempts is [_, { Accepted: true, ReadbackMatches: true }], "A successful select receipt must remain truthful even when the original EQ save goal was not reached");
                }
                else Check(dialogs.Alerts == 0 && !vm.EqDirty && vm.SelectedEqEntry?.Id == 3 && vm.EqName == "创建后未激活" && vm.Bands[0].Gain == 4,
                    "Selecting an unchanged recovered entry must finish the original EQ save");
            });
        await test("an older partial-effect receipt retains explicit retry consent without an initial audio confirmation", async () =>
        {
            var service = new Service { HiRes = true }; var dialogs = new Dialogs { RetryAnswer = true };
            service.Write = (action, count, _) =>
            {
                if (count != 1) return Task.FromResult(service.Success(action));
                service.HiRes = false;
                var failure = service.Failure(ApplyFailure.ReadbackUnavailable); failure.PrerequisiteSubmitted = true; failure.PartialEffects = true;
                failure.TargetSubmitted = false; return Task.FromResult(failure);
            };
            await using var vm = await Connected(service, dialogs); vm.SelectedSpatial = vm.SpatialOptions.Last();
            await vm.ApplySpatialCommand.ExecuteAsync(null);
            Check(dialogs.Confirmations == 0 && dialogs.Retries == 1 && service.Actions.Count == 2
                && service.Actions.All(action => !action.AllowMutexChange), "Legacy partial effects remain evidence without introducing an audio prerequisite");
        });
        await test("cancelling an unsafe retry discards all drafts and queued writes before a full refresh", async () =>
        {
            var service = new Service(); var dialogs = new Dialogs { RetryRelease = Answer() }; var entered = Signal(); var release = Signal();
            service.Write = async (_, _, token) => { entered.SetResult(); await release.Task.WaitAsync(token); return service.Failure(ApplyFailure.Validation); };
            await using var vm = await Connected(service, dialogs);
            var apply = vm.ToggleGameCommand.ExecuteAsync(true); await entered.Task.WaitAsync(Deadline);
            vm.SelectedAnc = vm.AncOptions.Last(); vm.SelectedPreset = vm.PresetOptions.Last(); vm.SelectedSpatial = vm.SpatialOptions.Last();
            vm.EqName = "应当丢弃"; vm.Bands[0].Gain = 4;
            var queued = vm.ApplyAncCommand.ExecuteAsync(null);
            release.SetResult(); await dialogs.RetryEntered.Task.WaitAsync(Deadline);
            dialogs.RetryRelease.SetResult(false); await Task.WhenAll(apply, queued).WaitAsync(Deadline);
            Check(service.Actions.Count == 1, "Cancelling retry must remove every setter which has not started");
            Check(!vm.EqDirty && vm.EqName == "均衡器一" && vm.Bands[0].Gain == 0, "Cancellation must discard the EQ draft");
            Check(vm.SelectedAnc?.Key == 0x0104 && vm.SelectedPreset?.Key == 0 && vm.SelectedSpatial?.Key == 0 && service.Reads.Last() == RefreshScope.Full, "Cancellation must restore all selectors from a full fresh snapshot");
        });
        await test("a second failure waits for alert dismissal before the final full refresh and never retries again", async () =>
        {
            var service = new Service(); var dialogs = new Dialogs { AlertRelease = Signal() };
            service.Write = (_, _, _) => Task.FromResult(service.Failure(ApplyFailure.PeerRejected));
            await using var vm = await Connected(service, dialogs);
            var apply = vm.ToggleGameCommand.ExecuteAsync(true); await dialogs.AlertEntered.Task.WaitAsync(Deadline);
            int fullReads = service.Reads.Count(x => x == RefreshScope.Full);
            Check(service.Actions.Count == 2 && !apply.IsCompleted && dialogs.Alerts == 1, "The second failure must stop submission and present one final alert");
            dialogs.AlertRelease.SetResult(); await apply.WaitAsync(Deadline);
            Check(service.Actions.Count == 2 && service.Reads.Count(x => x == RefreshScope.Full) == fullReads + 1, "Only alert dismissal permits the final full refresh; a third submission is forbidden");
        });
        foreach (var state in new[] { ConnectionState.Disconnected, ConnectionState.Unknown })
            await test("a " + state + " failure alerts on the first attempt without retry", async () =>
            {
                var service = new Service(); var dialogs = new Dialogs { AlertRelease = Signal() };
                service.Write = (_, _, _) =>
                {
                    var result = service.Failure(state == ConnectionState.Disconnected ? ApplyFailure.Disconnected : ApplyFailure.WorkerUnavailable);
                    result.ConnectionState = state; result.BackendReceiptReceived = state != ConnectionState.Unknown;
                    result.After = null; return Task.FromResult(result);
                };
                await using var vm = await Connected(service, dialogs);
                var apply = vm.ToggleGameCommand.ExecuteAsync(true); await dialogs.AlertEntered.Task.WaitAsync(Deadline);
                Check(service.Actions.Count == 1 && dialogs.Retries == 0 && !apply.IsCompleted, "Connection loss or missing receipt must not authorize replay");
                dialogs.AlertRelease.SetResult(); await apply.WaitAsync(Deadline);
                Check(service.Actions.Count == 1 && service.Reads.Last() == RefreshScope.Full, "One dismissed alert should be followed by synchronization, never a retry");
            });
        await test("missing complete receipt alerts even when accepted and matching flags claim success", async () =>
        {
            var service = new Service(); var dialogs = new Dialogs { AlertRelease = Signal() };
            service.Write = (action, _, _) =>
            {
                var result = service.Success(action); result.BackendReceiptReceived = false;
                return Task.FromResult(result);
            };
            await using var vm = await Connected(service, dialogs);
            var apply = vm.ToggleGameCommand.ExecuteAsync(true); await dialogs.AlertEntered.Task.WaitAsync(Deadline);
            Check(service.Actions.Count == 1 && dialogs.Retries == 0 && dialogs.Alerts == 1 && service.Reads.Count == 0 && !apply.IsCompleted,
                "Success flags without a complete backend receipt must lead directly to the first failure alert");
            dialogs.AlertRelease.SetResult(); await apply.WaitAsync(Deadline);
            Check(service.Actions.Count == 1 && service.Reads.Last() == RefreshScope.Full && vm.StatusWarning,
                "Alert dismissal must synchronize without replaying the incomplete result");
            Check(vm.LastApplyAttempts is [{ BackendReceiptReceived: false, Accepted: true, ReadbackMatches: true }],
                "The incomplete receipt must remain unchanged for diagnosis");
        });
        await test("a failed write cannot retry against a different device snapshot", async () =>
        {
            var service = new Service(); var dialogs = new Dialogs();
            service.Write = (_, _, _) => Task.FromResult(service.Failure(ApplyFailure.PeerRejected));
            service.Read = (_, _, _) => Task.FromResult(service.Snapshot("device-b"));
            await using var vm = await Connected(service, dialogs); await vm.ToggleGameCommand.ExecuteAsync(true);
            Check(service.Actions.Count == 1 && dialogs.Alerts == 1 && !vm.HasVerifiedSnapshot, "Fresh identity validation must precede any second submission");
        });
        await test("switching device cancels queued old-device writes and ignores the old read result", async () =>
        {
            var service = new Service(); var entered = Signal(); var release = Signal();
            service.Read = async (_, count, token) => { if (count == 1) { entered.SetResult(); await release.Task.WaitAsync(token); } return service.Snapshot(); };
            await using var vm = await Connected(service);
            var poll = vm.PollAsync(); await entered.Task.WaitAsync(Deadline);
            var write = vm.ToggleGameCommand.ExecuteAsync(true); var change = vm.SelectDeviceAsync(vm.Devices[1]);
            release.TrySetResult(); await Task.WhenAll(poll, write, change).WaitAsync(Deadline);
            Check(service.Actions.Count == 0 && service.Disconnections == 1 && vm.SelectedDevice?.Identity == "device-b" && !vm.HasVerifiedSnapshot, "The old generation must not publish snapshots or send queued settings after device change");
        });
        await test("disconnecting while an audio setting waits behind a read prevents its first setter", async () =>
        {
            var service = new Service { HiRes = true }; var dialogs = new Dialogs(); var entered = Signal(); var release = Signal();
            service.Read = async (_, _, token) => { entered.TrySetResult(); await release.Task.WaitAsync(token); return service.Snapshot(); };
            await using var vm = await Connected(service, dialogs); vm.SelectedSpatial = vm.SpatialOptions.Last();
            var poll = vm.PollAsync(); await entered.Task.WaitAsync(Deadline);
            var apply = vm.ApplySpatialCommand.ExecuteAsync(null);
            var disconnect = vm.ConnectCommand.ExecuteAsync(null);
            release.TrySetResult(); await Task.WhenAll(poll, apply, disconnect).WaitAsync(Deadline);
            Check(dialogs.Confirmations == 0 && service.Actions.Count == 0 && service.Disconnections == 1 && !vm.HasVerifiedSnapshot,
                "A lifecycle request must supersede the queued audio setting before its first submission");
        });
        await test("closing cancels a background query and every queued write", async () =>
        {
            var service = new Service(); var entered = Signal(); var release = Signal();
            service.Read = async (_, _, token) => { entered.TrySetResult(); await release.Task.WaitAsync(token); return service.Snapshot(); };
            var vm = await Connected(service);
            var poll = vm.PollAsync(); await entered.Task.WaitAsync(Deadline); var write = vm.ToggleGameCommand.ExecuteAsync(true);
            Check(await vm.RequestCloseAsync().WaitAsync(Deadline), "Close must complete by cancelling the active query");
            await Task.WhenAll(poll, write).WaitAsync(Deadline); await vm.DisposeAsync();
            Check(service.Actions.Count == 0 && service.Disposals == 1 && !vm.IsRefreshing && !vm.Busy, "Closing must drain queues and dispose the service exactly once");
        });
        await test("dialog ownership serializes confirmations and cancels waiters on shutdown", async () =>
        {
            using var lifetime = new CancellationTokenSource();
            var coordinator = new DialogCoordinator(lifetime.Token); var entered = Signal(); var release = Signal();
            var first = coordinator.RunAsync(async token => { entered.SetResult(); await release.Task.WaitAsync(token); });
            await entered.Task.WaitAsync(Deadline);
            bool secondEntered = false;
            var second = coordinator.RunAsync(_ => { secondEntered = true; return Task.CompletedTask; });
            Check(!secondEntered && !second.IsCompleted, "A second modal cannot obtain ownership while the first is visible");
            lifetime.Cancel();
            await Cancelled(first); await Cancelled(second);
            Check(!secondEntered, "A waiting modal must not appear after window shutdown");
        });
    }

    private static async Task<MainViewModel> Connected(Service service, Dialogs? dialogs = null)
    {
        var vm = new MainViewModel(service, dialogs ?? new Dialogs());
        await vm.InitializeAsync(); await vm.ConnectCommand.ExecuteAsync(null);
        Check(vm.HasVerifiedSnapshot, "Fake connection must establish the verified device"); return vm;
    }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static TaskCompletionSource<bool> Answer() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
    private static async Task Cancelled(Task task)
    {
        try { await task.WaitAsync(Deadline); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("A cancelled dialog must complete with cancellation");
    }

    private sealed class Dialogs : IConfirmationService
    {
        public bool RetryAnswer = true;
        public bool ConfirmIgnoresCancellation;
        public int Confirmations, Retries, Alerts;
        public TaskCompletionSource ConfirmationEntered { get; } = Signal();
        public TaskCompletionSource RetryEntered { get; } = Signal();
        public TaskCompletionSource AlertEntered { get; } = Signal();
        public TaskCompletionSource<bool>? ConfirmationRelease;
        public TaskCompletionSource<bool>? RetryRelease;
        public TaskCompletionSource? AlertRelease;
        public Task<bool> ConfirmAsync(string title, string message, CancellationToken token)
        {
            Confirmations++; ConfirmationEntered.TrySetResult();
            return ConfirmationRelease is null ? Task.FromResult(true)
                : ConfirmIgnoresCancellation ? ConfirmationRelease.Task : ConfirmationRelease.Task.WaitAsync(token);
        }
        public Task<bool> ConfirmRetryAsync(string title, string message, CancellationToken token)
        {
            Retries++; RetryEntered.TrySetResult(); return RetryRelease?.Task.WaitAsync(token) ?? Task.FromResult(RetryAnswer);
        }
        public Task AlertAsync(string title, string message, CancellationToken token)
        {
            Alerts++; AlertEntered.TrySetResult(); return AlertRelease?.Task.WaitAsync(token) ?? Task.CompletedTask;
        }
    }

    private sealed class Service : IHeadsetService
    {
        private static readonly int[] Frequencies = [31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000];
        private int _inFlight;
        public int MaximumConcurrent, Disconnections, Disposals;
        public string Identity = "device-a";
        public bool Game, Dual = true, HiRes;
        public int Anc = 0x0104, Preset, Spatial;
        public List<UiEqEntry> Entries =
        [
            new(1, "均衡器一", -6, 6, true, Frequencies.Select(hz => new UiBand(hz, 0)).ToArray()),
            new(2, "均衡器二", -6, 6, false, Frequencies.Select(hz => new UiBand(hz, 2)).ToArray())
        ];
        public List<UiAction> Actions { get; } = [];
        public List<RefreshScope> Reads { get; } = [];
        public List<string> Events { get; } = [];
        public Func<RefreshScope, int, CancellationToken, Task<UiSnapshot>>? Read;
        public Func<UiAction, int, CancellationToken, Task<UiApplyResult>>? Write;
        public Task<IReadOnlyList<UiDevice>> EnumerateAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<UiDevice>>
            ([new("device-a", "OPPO Enco X4", true), new("device-b", "第二副耳机", true)]);
        public Task<UiSnapshot> ConnectAsync(string identity, CancellationToken token) { Identity = identity; return Task.FromResult(Snapshot()); }
        public Task DisconnectAsync(CancellationToken token) { Disconnections++; return Task.CompletedTask; }
        public Task<UiSnapshot> RefreshAsync(CancellationToken token) => RefreshAsync(RefreshScope.Full, token);
        public async Task<UiSnapshot> RefreshAsync(RefreshScope scope, CancellationToken token)
        {
            Enter(); Reads.Add(scope); Events.Add("read:" + scope);
            try { return Read is null ? Snapshot() : await Read(scope, Reads.Count, token); }
            finally { Interlocked.Decrement(ref _inFlight); }
        }
        public async Task<UiApplyResult> ApplyAsync(UiAction action, CancellationToken token)
        {
            Enter(); Actions.Add(action); Events.Add("apply:" + action.Kind);
            try { return Write is null ? Success(action) : await Write(action, Actions.Count, token); }
            finally { Interlocked.Decrement(ref _inFlight); }
        }
        private void Enter() { MaximumConcurrent = Math.Max(MaximumConcurrent, Interlocked.Increment(ref _inFlight)); }
        public UiSnapshot Snapshot(string? identity = null) => new()
        {
            Identity = identity ?? Identity, DeviceName = "OPPO Enco X4", ProductId = "06EC10", Connected = true,
            ConnectionState = ConnectionState.Connected, SupportedModel = true, LeftBattery = 90, Firmware = "116",
            Anc = Anc, Equalizer = Preset, Spatial = Spatial, HiRes = HiRes, DualConnection = Dual, GameMode = Game,
            CanSetAnc = true, CanSetEqualizer = true, CanSetCustomEqualizer = true, CanSetSpatial = true,
            CanSetHiRes = true, CanSetDualConnection = true, CanSetGameMode = true, SpatialHiResMutex = true,
            AncOptions = [new(0x0104, "深度降噪"), new(0x0107, "智能降噪")],
            EqualizerOptions = [new(0, "丹拿原声"), new(7, "丹拿高解析")], SpatialOptions = [new(0, "关闭"), new(2, "头部追踪")],
            EqualizerEntries = Entries.Select(x => x with { Bands = x.Bands.ToArray() }).ToArray(), SampleTime = DateTimeOffset.UtcNow
        };
        public UiApplyResult Success(UiAction action)
        {
            int? observedId = null;
            switch (action.Kind)
            {
                case "Anc": Anc = action.Value; break;
                case "Equalizer": Preset = action.Value; break;
                case "Spatial": Spatial = action.Value; if (Spatial != 0) HiRes = false; break;
                case "GameMode": Game = action.Value == 1; break;
                case "DualConnection": Dual = action.Value == 1; break;
                case "HiRes": HiRes = action.Value == 1; if (HiRes) Spatial = 0; break;
                case "CustomSelect": Entries = Entries.Select(x => x with { Selected = x.Id == action.Value }).ToList(); break;
                case "CustomEqualizer" when action.EqualizerEntry is { } entry:
                    observedId = entry.Id == 0 ? 3 : entry.Id;
                    Entries = Entries.Where(x => x.Id != observedId).Select(x => x with { Selected = false })
                        .Append(entry with { Id = observedId.Value, Selected = true, Bands = entry.Bands.ToArray() }).ToList(); break;
            }
            return new()
            {
                BackendReceiptReceived = true, ConnectionState = ConnectionState.Connected, TargetSubmitted = true,
                SubmissionAttempted = true, Accepted = true, ReadbackMatches = true, ObservedEqualizerId = observedId, After = Snapshot()
            };
        }
        public UiApplyResult Failure(ApplyFailure kind) => new()
        {
            BackendReceiptReceived = true, ConnectionState = ConnectionState.Connected, FailureKind = kind,
            TargetSubmitted = true, SubmissionAttempted = true, Accepted = false, ReadbackMatches = false,
            AckStatus = kind == ApplyFailure.PeerRejected ? (byte)1 : null, After = Snapshot()
        };
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }
}
