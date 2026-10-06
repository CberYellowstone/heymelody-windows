namespace HeyMelodyWindows.App;

// Only used by --ui-preview. No Windows/native/backend references and no Bluetooth calls.
internal sealed class PreviewService : IHeadsetService
{
    private const string Identity = "preview-device";
    public Task<IReadOnlyList<UiDevice>> EnumerateAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<UiDevice>>([new(Identity, "OPPO Enco X4", true)]);
    public Task<UiSnapshot> ConnectAsync(string identity, CancellationToken token) => Task.FromResult(Snapshot());
    public Task DisconnectAsync(CancellationToken token) => Task.CompletedTask;
    public Task<UiSnapshot> RefreshAsync(CancellationToken token) => Task.FromResult(Snapshot());
    public Task<UiApplyResult> ApplyAsync(UiAction action, CancellationToken token) => throw new InvalidOperationException("Preview never sends settings.");
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    private static UiSnapshot Snapshot() => new()
    {
        Identity = Identity, DeviceName = "OPPO Enco X4", Connected = true, SupportedModel = true,
        Firmware = "116", LeftBattery = 92, RightBattery = 89, CaseBattery = 76,
        LeftCharging = false, RightCharging = false, CaseCharging = false,
        Anc = 0x0104, Equalizer = 0, Spatial = 0, HiRes = true, DualConnection = true, GameMode = false,
        CanSetAnc = true, CanSetEqualizer = true, CanSetCustomEqualizer = true,
        CanSetDualConnection = true, CanSetGameMode = true, CanSetSpatial = true, CanSetHiRes = true, SpatialHiResMutex = true,
        AncOptions = [new(0x0003, "关闭降噪"), new(0x0107, "智能降噪"), new(0x0104, "深度降噪"), new(0x0105, "中度降噪"), new(0x0106, "轻度降噪"), new(0x0208, "通透模式")],
        EqualizerOptions = [new(0, "丹拿原声"), new(7, "丹拿高解析"), new(2, "纯享人声"), new(1, "清亮高音"), new(3, "澎湃低音")],
        SpatialOptions = [new(0, "关闭"), new(1, "固定空间音频"), new(2, "头部追踪")],
        EqualizerEntries = [new(1, "我的均衡器", -6, 6, true, new[] {31,62,125,250,500,1000,2000,4000,8000,16000}.Select((hz, i) => new UiBand(hz, i < 3 ? 2 : i > 6 ? 1 : 0)).ToArray())],
        SampleTime = DateTimeOffset.UtcNow
    };
}
