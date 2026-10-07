using HeyMelodyWindows.App;
using HeyMelodyWindows.Calibration.UI;

var passed = 0;
try
{
    var service = new TestService();
    await using (var model = new CalibrationViewModel(service, "operation", "hearing"))
    {
        await model.InitializeAsync();
        model.Adopt(service.State with { Revision = 0, Stage = "Failed" });
        Check(model.State.Stage == "Ready", "Late events cannot roll the operation state back.");
        model.Adopt(service.State with { Handle = "another-operation", Revision = 99, Stage = "Completed" });
        Check(model.State.Stage == "Ready", "A different operation cannot update this component.");
        model.Name = "A"; Check(model.ValidateName() is not null, "Short profile names are rejected.");
        model.Name = "Existing"; Check(model.ValidateName() is not null, "Duplicate profile names are rejected.");
        model.Name = "新配置"; Check(model.ValidateName() is null && model.ReplacesCurrent, "The current signed UID zero is a profile that requires replacement consent.");
    }
    Console.WriteLine("PASS isolated calibration state and naming"); passed++;

    service = new TestService { BlockStart = true };
    await using (var model = new CalibrationViewModel(service, "operation", "fit"))
    {
        await model.InitializeAsync();
        var start = model.SendAsync("start");
        await service.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Check(model.Busy, "The measurement request is active.");
        await model.CancelAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await start.WaitAsync(TimeSpan.FromSeconds(2));
        Check(service.Cancels == 1 && model.State.Stage == "Cancelled", "Closing cancels without waiting for a measurement lock.");
    }
    Console.WriteLine("PASS independent cancellation during measurement"); passed++;

    service = new TestService { BlockFirstLevel = true };
    await using (var model = new CalibrationViewModel(service, "operation", "hearing"))
    {
        await model.InitializeAsync();
        var initial = model.AdjustLevelAsync(2, false);
        await service.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var released = model.AdjustLevelAsync(10, true);
        service.ReleaseLevel.TrySetResult();
        await released.WaitAsync(TimeSpan.FromSeconds(2)); await initial;
        Check(service.Inputs is [{ Action: "level", LevelIndex: 2, Released: false }, { Action: "level", LevelIndex: 10, Released: true }],
            "The released slider value is queued and never dropped behind an in-flight level request.");
        await model.SendAsync("next");
        Check(service.Inputs.Last().Action == "next" && model.State.Revision == 4, "Continue uses the latest acknowledged operation revision.");
    }
    Console.WriteLine("PASS coalesced level releases and revision ordering"); passed++;
    passed += CalibrationVisualTests.Run();
    Console.WriteLine($"{{\"success\":true,\"passed\":{passed},\"nativeBluetoothCalls\":0}}");
    return 0;
}
catch (Exception error) { Console.Error.WriteLine(error); return 1; }

static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

sealed class TestService : ICalibrationService
{
    public UiCalibrationState State { get; set; } = new() { Handle = "operation", Identity = "earbuds", Generation = 42, Revision = 1, BackendInstanceId = "fixture", Stage = "Ready" };
    public bool BlockStart { get; init; }
    public bool BlockFirstLevel { get; init; }
    public int Cancels { get; private set; }
    public List<UiCalibrationInput> Inputs { get; } = [];
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseLevel { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<IReadOnlyList<UiHearingProfile>> GetHearingProfilesAsync(string identity, long generation, CancellationToken token)
        => Task.FromResult<IReadOnlyList<UiHearingProfile>>([new(0, "Existing", true, true)]);
    public Task<UiApplyResult> SelectHearingProfileAsync(string identity, long generation, int? uid, CancellationToken token) => throw new NotSupportedException();
    public Task<UiCalibrationState> CreateCalibrationAsync(string identity, long generation, string kind, CancellationToken token) => throw new NotSupportedException();
    public Task<UiCalibrationState> GetCalibrationAsync(string handle, CancellationToken token) => Task.FromResult(State);
    public async Task<UiCalibrationState> AdvanceCalibrationAsync(string handle, long revision, UiCalibrationInput input, CancellationToken token)
    {
        if (revision != State.Revision) throw new InvalidOperationException("Stale operation revision.");
        Inputs.Add(input);
        if (BlockStart && input.Action == "start") { Started.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); }
        if (BlockFirstLevel && Inputs.Count == 1 && input.Action == "level") { Started.TrySetResult(); await ReleaseLevel.Task.WaitAsync(token); }
        State = State with { Revision = State.Revision + 1, Stage = "AwaitingAnswer", LevelIndex = input.LevelIndex ?? State.LevelIndex };
        return State;
    }
    public Task<UiCalibrationState> CancelCalibrationAsync(string handle, CancellationToken token)
    { Cancels++; State = State with { Revision = State.Revision + 1, Stage = "Cancelled" }; return Task.FromResult(State); }
}
