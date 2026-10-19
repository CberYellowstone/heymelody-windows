using System.Text.Json;
using HeyMelodyWindows.Devices;

namespace HeyMelodyWindows.Daily;

public sealed partial class DailyClient
{
    // Diagnostics describe source requirements, never hardware validation or permission to write.
    private IReadOnlyList<string> CollectFirmwareCompatibilityWarnings(DailySnapshot s)
    {
        if (_profile is null || _commands is null || !s.ProfileVerified || !s.Connected) return [];
        var versions = new DeviceVersionContext(s.FirmwareVersion, _appVersionMarker);
        var groups = new Dictionary<(string Minimum, string Current, VersionConditionStatus Status), SortedSet<string>>();
        void AddIssues(string feature, IReadOnlyList<FirmwareConditionIssue> issues)
        {
            foreach (var issue in issues)
            {
                string minimum = issue.Minimum.ValueKind switch
                {
                    JsonValueKind.String => FirmwareFact(issue.Minimum.GetString(), "配置格式未知"),
                    JsonValueKind.Number => FirmwareFact(issue.Minimum.GetRawText(), "配置格式未知"),
                    _ => "配置格式未知"
                };
                var key = (minimum, FirmwareFact(issue.CurrentVersion, "未知"), issue.Status);
                if (!groups.TryGetValue(key, out var names)) groups[key] = names = new(StringComparer.Ordinal);
                names.Add(feature);
            }
        }
        void Add(string feature, VersionConditions conditions)
        {
            // An App requirement still selects whether this configured control is applicable.
            if (conditions.EvaluateForControl(versions) == VersionConditionStatus.Pass)
                AddIssues(feature, conditions.GetFirmwareIssues(versions));
        }
        var implemented = s.FeatureSections.SelectMany(section => section.Controls).Select(control => control.Title)
            .ToHashSet(StringComparer.Ordinal);
        if (_profile.NoiseReductionModes.Count > 0 && GeneralCommands(0x010C, 0x0404))
        {
            implemented.Add("降噪模式");
            foreach (var parent in _profile.NoiseReductionModes)
            {
                Add("降噪模式", parent.Conditions);
                if (parent.Conditions.EvaluateForControl(versions) != VersionConditionStatus.Pass) continue;
                foreach (var child in parent.Children) Add(AncOptionName(parent, child), child.Conditions);
            }
        }
        if (_profile.FunctionVersion("equalizer") > 0 && GeneralCommands(0x010F, 0x0406))
        {
            implemented.Add("调音预设");
            byte? color = s.Color ?? (string.Equals(s.ProductId, _observedProductId, StringComparison.OrdinalIgnoreCase) ? _observedColor : null);
            foreach (var mode in _profile.GetEffectiveEqualizerModes(versions, color))
                AddIssues($"调音预设 {mode.ProtocolIndex}", mode.GetFirmwareIssues(versions));
        }
        if (_profile.FunctionVersion("customEqualizer") > 0 && GeneralCommands(0x0122, 0x0418))
            implemented.Add("自定义均衡器");
        if (_profile.GameSoundModes.Count > 0 && GeneralCommands(0x012B, 0x0423))
        {
            implemented.Add("游戏音效");
            foreach (var mode in _profile.GameSoundModes.Where(mode => s.GameSound is null
                || s.GameSound.SupportedTypes.Contains((byte)mode.Type))) Add("游戏音效", mode.Conditions);
        }
        if (GeneralCommands(0x0108, 0x0401)
            && _profile.RawFunctions.TryGetProperty("control", out var controls) && controls.ValueKind == JsonValueKind.Array)
        {
            foreach (var control in controls.EnumerateArray())
            {
                int? action = JsonInt(control, "action"), support = JsonInt(control, "support");
                if (!action.HasValue || !support.HasValue || !s.Gestures.Any(gesture => gesture.Device is 1 or 2
                    && gesture.Action == ProtocolAction(action.Value) && gesture.Button == (JsonInt(control, "button") ?? 0))) continue;
                uint mask = unchecked((uint)support.Value);
                bool onePlusLegacy = _profile.Name is "OnePlus Buds" or "OnePlus Buds Z";
                if (!NativeGestureFunctions.Any(function => (mask & (function.Function == 5 && onePlusLegacy ? 0x40 : function.Mask)) != 0)) continue;
                implemented.Add("耳机操控");
                Add("耳机操控", VersionConditions.FromSource(control));
            }
        }
        bool Commands(params ushort[] commands) => commands.All(_commands.Supports);
        bool earScan = _profile.FunctionVersion("earScan") > 0;
        if (_profile.FunctionVersion("fitDetection") > 0 && Commands(0x0405, 0x0109)) implemented.Add("佩戴贴合度测试");
        if ((_profile.FunctionVersion("hearingEnhancement") > 0 || _profile.FunctionVersion("hearingEnhancementNew") > 0)
            && Commands(0x0109, 0x040D, 0x040E, 0x0116) && (!earScan || _commands.Supports(0x011F)))
            implemented.Add("黄金听感测试");
        if (earScan && Commands(0x040D, 0x0109)) implemented.Add("耳道扫描");
        foreach (string feature in implemented) Add(feature, _profile.Conditions);
        return groups.OrderBy(pair => pair.Key.Minimum, StringComparer.Ordinal)
            .ThenBy(pair => pair.Key.Current, StringComparer.Ordinal).ThenBy(pair => pair.Key.Status)
            .Select(pair => pair.Key.Status == VersionConditionStatus.Fail
                ? $"固件兼容提示：{string.Join("、", pair.Value)}的配置要求固件至少为 {pair.Key.Minimum}；当前为 {pair.Key.Current}，低于该要求。"
                : $"固件兼容提示：{string.Join("、", pair.Value)}的配置固件要求为 {pair.Key.Minimum}；当前为 {pair.Key.Current}，无法核对该要求。")
            .ToArray();
    }

    private static string FirmwareFact(string? value, string missing)
    {
        if (string.IsNullOrWhiteSpace(value)) return missing;
        string bounded = value.Length > 80 ? value[..79] + "…" : value;
        return new string(bounded.Select(character => char.IsControl(character) ? ' ' : character).ToArray());
    }
}
