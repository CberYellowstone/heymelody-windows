using HeyMelodyWindows.Devices;

namespace HeyMelodyWindows.Daily;

public sealed partial class DailyClient
{
    /// <summary>Apply verified BLE metadata without reconnecting, querying, or writing the device.</summary>
    public async Task<DailySnapshot> SetObservedColorAsync(string productId, byte? color, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(productId);
        await _gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _observedProductId = productId; _observedColor = color;
            DailySnapshot next;
            lock (_stateGate)
            {
                next = _latest is null ? new() : Clone(_latest);
                ResolvePresetOptions(next);
                next.Revision = ++_revision;
                _latest = Clone(next);
            }
            Publish(next); return next;
        }
        finally { _gate.Release(); }
    }
    private void ResolvePresetOptions(DailySnapshot snapshot)
    {
        // The session's own color read is preferred; a verified broadcast for the same product fills a gap.
        byte? color = snapshot.Color ?? (string.Equals(snapshot.ProductId, _observedProductId, StringComparison.OrdinalIgnoreCase) ? _observedColor : null);
        snapshot.Presets = _profile?.GetEffectiveEqualizerModes(new(snapshot.FirmwareVersion, _appVersionMarker), color)
            .Select(x => new PresetOption(checked((byte)x.ProtocolIndex), x.ModeType, PresetName(x.ModeType, x.ProtocolIndex))).ToList() ?? [];
    }
    private string PresetName(int type, int protocolId)
    {
        bool alternative = _profile?.FunctionVersion("equalizer") == 2 || _profile?.Name is "OPPO Enco R" or "OPPO Enco Air2";
        if (alternative && type is >= 1 and <= 3) return type switch { 1 => "纯粹原音", 2 => "脉冲低音", _ => "悠扬人声" };
        if (type == 4 && _profile?.Name == "OPPO Enco R") return "微醺柔音";
        return type switch
        {
            1 => "经典", 2 => "动感低音", 3 => "清澈人声", 4 => "明亮通透", 5 or 35 => "默认",
            6 or 36 => "Dynaudio 淳朴悠然", 7 or 37 => "Dynaudio 温暖轻柔", 8 or 38 => "Dynaudio 澎湃爽朗", 9 or 39 => "Dynaudio 纯粹原音",
            10 => "久石让大师调音", 11 => "均衡（默认）", 12 => "深海低音", 13 => "明快清亮", 14 => "纯净人声", 15 => "微醺柔音",
            16 => "Enco X 怀旧经典", 17 => "均衡", 18 => "Reno特调（破晓曙光）", 19 => "汉斯季默大师调音", 20 => "自然竹韵",
            21 => "Reno特调（灿烂朝霞）", 22 => "纯粹原音", 23 => "澎湃爽朗", 24 => "悠扬开阔", 25 => "Reno特调（幻紫星河）",
            26 => "至臻原音", 27 => "高清解析", 28 => "纯享人声", 29 => "澎湃低音", 30 => "丹拿特调", 31 => "脉冲低音",
            32 => "悠扬人声", 33 => "星河独白", 34 => "活力动感", 40 => "丹拿人声", 41 => "清晰通透", 42 => "丹拿原声",
            43 => "丹拿高解析", 44 => "丹拿低音", 45 => "丹拿经典", 46 => "清亮高音", _ => $"预设 {protocolId}"
        };
    }
}
