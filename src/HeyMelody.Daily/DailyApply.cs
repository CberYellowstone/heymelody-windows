namespace HeyMelodyWindows.Daily;

public sealed partial class DailyClient
{
    private async Task<DailyApplyResult> ApplyCoreAsync(string kind, byte value, byte level,
        EqualizerEdit? suppliedEdit, bool allowMutexChange, CancellationToken token)
    {
        // Copy mutable GUI input before any await; future slider changes cannot alter this submission.
        EqualizerEdit? edit = suppliedEdit is null ? null : new()
        { Id = suppliedEdit.Id, Name = suppliedEdit.Name, MinimumGain = suppliedEdit.MinimumGain,
          MaximumGain = suppliedEdit.MaximumGain, Bands = suppliedEdit.Bands?.ToList() ?? [], Activate = suppliedEdit.Activate };
        var result = new DailyApplyResult();
        await _gate.WaitAsync(token);
        try
        {
            if (_disposed) return Reject(result, "客户端已关闭。");
            DailySnapshot before = await ReadFreshAsync(token); result.Before = before;
            if (!before.Connected || !before.ProfileVerified || before.Error is not null)
                return Reject(result, "新鲜设备状态不可用，没有发送设置。");
            var caps = before.Capabilities;
            ushort cmd; byte[] payload; Func<DailySnapshot, bool> target;
            switch (kind)
            {
                case "anc":
                    if (!caps.Anc || !before.AncOptions.Any(x => x.Primary == value && x.Level == level))
                        return Reject(result, "该降噪档位未通过当前型号和状态校验。");
                    cmd = 0x0404; payload = DailyParsers.BuildAnc(level);
                    target = s => s.Anc?.Primary == value && s.Anc.Level == level;
                    break;
                case "preset":
                    if (!caps.Preset || !before.Presets.Any(x => x.ProtocolId == value))
                        return Reject(result, "该预设编号不在型号配置中。");
                    cmd = 0x0406; payload = [value]; target = s => s.PresetId == value; break;
                case "custom-select":
                    if (!caps.CustomEqualizer || !before.CustomEqualizersRead || value == 0 ||
                        !before.CustomEqualizers.Any(x => x.Id == value))
                        return Reject(result, "没有回读到该自定义 EQ 条目。");
                    var selected = before.CustomEqualizers.Single(x => x.Id == value);
                    // Official APK Ab/Z.smali:548-583 selects with action=2 and the complete EqInfo.
                    // Built-in 0406 IDs and custom EqInfo IDs are separate namespaces.
                    var selection = new EqualizerEdit { Id = selected.Id, Name = selected.Name,
                        MinimumGain = selected.MinimumGain, MaximumGain = selected.MaximumGain,
                        Bands = selected.Bands.ToList() };
                    cmd = 0x0418; payload = DailyParsers.BuildEqualizer(selection, before.EqualizerFrequencies, preserveReportedName: true);
                    target = s => s.CustomEqualizersRead && s.CustomEqualizers.Any(x => x.Id == selected.Id && x.Selected &&
                        x.Name == selected.Name && x.MinimumGain == selected.MinimumGain &&
                        x.MaximumGain == selected.MaximumGain && x.Bands.SequenceEqual(selected.Bands));
                    break;
                case "custom-save": return await SaveCoreAsync(edit, result, token);
                case "dual":
                    if (!caps.DualConnection) return Reject(result, "双连状态未知或不可设置。");
                    cmd = 0x0403; payload = [0x11, value]; target = s => s.DualConnection == (value == 1); break;
                case "game":
                    if (!caps.GameMode || caps.GameFeatureId is not (0x06 or 0x28))
                        return Reject(result, "设备未报告可验证的游戏模式开关。");
                    cmd = 0x0403; payload = [caps.GameFeatureId.Value, value];
                    target = s => s.Capabilities.GameFeatureId == caps.GameFeatureId && s.GameMode == (value == 1); break;
                case "spatial":
                    if (!caps.SpatialMode || value > 2 || !_profile!.SpatialTypes.Contains(value) ||
                        caps.SpatialRoute == "legacy" && value > 1)
                        return Reject(result, "当前空间模式不在已核实的协议能力中。");
                    if (value > 0 && caps.SpatialHiResMutex)
                    {
                        if (before.HiRes is null) return Reject(result, "Hi-Res 状态未知，不能安全开启空间模式。");
                        if (before.HiRes == true)
                        {
                            if (!allowMutexChange) return Reject(result, "开启空间模式需要先关闭 Hi-Res，请明确确认联动。");
                            if (!caps.HiRes) return Reject(result, "Hi-Res 无法设置，未发送空间模式。");
                            if (!await SetPrerequisiteAsync(0x0403, [0x18, 0], s => s.HiRes == false,
                                "已关闭 Hi-Res", result, token)) return Finish(result);
                            caps = result.After!.Capabilities;
                            if (!caps.SpatialMode || result.After.HiRes != false ||
                                caps.SpatialRoute == "legacy" && value > 1)
                                return Reject(result, "联动后的空间能力或 Hi-Res 状态已改变，未继续发送设置。");
                        }
                    }
                    cmd = caps.SpatialRoute == "modern" ? (ushort)0x0422 : (ushort)0x0403;
                    payload = caps.SpatialRoute == "modern" ? [value] : [0x1B, value];
                    target = s => s.SpatialMode == value; break;
                case "hires":
                    if (!caps.HiRes) return Reject(result, "Hi-Res 状态未知或不可设置。");
                    if (value == 1 && caps.SpatialHiResMutex)
                    {
                        if (before.SpatialMode is null) return Reject(result, "空间模式状态未知，不能安全开启 Hi-Res。");
                        if (before.SpatialMode > 0)
                        {
                            if (!allowMutexChange) return Reject(result, "开启 Hi-Res 需要先关闭空间模式，请明确确认联动。");
                            if (!caps.SpatialMode) return Reject(result, "空间模式无法设置，未发送 Hi-Res。");
                            ushort prerequisite = caps.SpatialRoute == "modern" ? (ushort)0x0422 : (ushort)0x0403;
                            byte[] off = caps.SpatialRoute == "modern" ? [0] : [0x1B, 0];
                            if (!await SetPrerequisiteAsync(prerequisite, off, s => s.SpatialMode == 0,
                                "已关闭空间模式", result, token)) return Finish(result);
                            caps = result.After!.Capabilities;
                            if (!caps.HiRes || result.After.SpatialMode != 0)
                                return Reject(result, "联动后的 Hi-Res 能力或空间状态已改变，未继续发送设置。");
                        }
                    }
                    cmd = 0x0403; payload = [0x18, value]; target = s => s.HiRes == (value == 1); break;
                default: return Reject(result, "未知设置请求。");
            }
            DailySnapshot latest = result.After ?? before;
            if (target(latest))
            {
                result.ReadbackVerified = true; result.Success = true; result.After = latest;
                result.Message = "当前回读已经符合请求，没有重复发送设置。"; return result;
            }
            await SendAndObserveAsync(cmd, payload, target, result, token);
            return Finish(result);
        }
        catch (Exception)
        {
            result.Error = token.IsCancellationRequested ? "操作已取消；已提交的设置不会自动重发。" :
                "设置过程未完成；已提交的设置不会自动重发。";
            if (result.SubmissionAttempted) result.PartialEffects.Add("本次已提交设置，最终状态需要再次读取确认。");
            return Finish(result);
        }
        finally { _gate.Release(); }
    }
    private async Task SendAndObserveAsync(ushort command, byte[] payload, Func<DailySnapshot, bool> target,
        DailyApplyResult result, CancellationToken token)
    {
        var reply = await _session!.RequestAsync(command, payload, token);
        MergeAck(result, reply);
        // Reads may reconnect; a setter is never retried, including partial-send failures.
        if (_session is not { Usable: true }) await DropSessionAsync();
        result.After = await ReadFreshAsync(token);
        bool identityMatches = result.After.ProfileVerified && result.After.ProductId == result.Before!.ProductId;
        result.ReadbackVerified = identityMatches && target(result.After);
        result.Success = result.AckAccepted == true && result.ReadbackVerified;
    }
    private async Task<bool> SetPrerequisiteAsync(ushort command, byte[] payload, Func<DailySnapshot, bool> target,
        string description, DailyApplyResult result, CancellationToken token)
    {
        await SendAndObserveAsync(command, payload, target, result, token);
        bool verified = result.AckAccepted == true && result.ReadbackVerified;
        // Record even an uncertain prerequisite conservatively; no compensating write or later setter follows it.
        result.PartialEffects.Add(verified ? description : description + "的请求已提交，结果未完整确认。");
        if (!verified) { result.Success = false; result.ReadbackVerified = false; return false; }
        result.Success = false; result.ReadbackVerified = false;
        // Refresh may have changed capabilities. Never use stale route/command information for the final setter.
        if (result.After is not { Connected: true, ProfileVerified: true } ||
            result.After.ProductId != result.Before!.ProductId)
        { result.Error = "联动后设备身份或连接状态改变，已停止后续设置。"; return false; }
        return true;
    }
    private static void MergeAck(DailyApplyResult result, DailyResponse response)
    {
        bool first = !result.SubmissionAttempted;
        result.SubmissionAttempted |= response.Submitted;
        result.ResponseMatched = first ? response.Matched : result.ResponseMatched && response.Matched;
        bool? accepted = response.Status is null ? null : response.Status == 0;
        result.AckAccepted = first ? accepted : result.AckAccepted == false || accepted == false ? false :
            result.AckAccepted == true && accepted == true ? true : null;
    }
    private static DailyApplyResult Reject(DailyApplyResult result, string text)
    { result.Error = text; result.Message = text; return result; }
    private static DailyApplyResult Finish(DailyApplyResult result)
    {
        if (result.Success) result.Message = "设备已确认设置，状态回读一致。";
        else if (result.Error is not null) result.Message = result.Error;
        else if (result.AckAccepted == false) result.Message = "设备拒绝了设置；请查看重新读取的状态。";
        else if (result.SubmissionAttempted && result.AckAccepted is null)
            result.Message = result.ReadbackVerified ? "状态回读符合请求，但设置响应未完整确认；不会自动重发。" :
                "设置已提交，响应或回读未确认；不会自动重发。";
        else result.Message = "设置响应与状态回读未同时确认，请刷新状态。";
        return result;
    }
    private async Task<DailyApplyResult> SaveCoreAsync(EqualizerEdit? edit, DailyApplyResult result, CancellationToken token)
    {
        var before = result.Before!;
        if (edit is null || !before.Capabilities.CustomEqualizer || !before.CustomEqualizersRead)
            return Reject(result, "自定义 EQ 编辑数据或设备列表不可用。");
        var existing = before.CustomEqualizers.FirstOrDefault(x => x.Id == edit.Id && edit.Id != 0);
        if (edit.Id != 0 && existing is null) return Reject(result, "该 EQ 条目已不存在。");
        if (before.CustomEqualizers.Any(x => x.Id != edit.Id && x.Name == edit.Name))
            return Reject(result, "该名称已经存在，请使用另一个名称。");
        if (existing is not null && (existing.MinimumGain != edit.MinimumGain || existing.MaximumGain != edit.MaximumGain))
            return Reject(result, "编辑范围与该条目刚回读的增益范围不一致。");
        if (existing is null)
        {
            // Official APK Ab/Z$i.smali:115/123 creates action=1 with +6/-6 dB. It is source policy,
            // not a guessed device-reported range. Updates still require the exact returned entry limits.
            if (edit.MinimumGain != -6 || edit.MaximumGain != 6)
                return Reject(result, "新建 EQ 需要使用已核实的官方默认范围 -6 至 +6 dB。");
        }
        if (!edit.Activate)
            return Reject(result, "本客户端仅提供保存并选用；不会自动补发选用请求。");
        byte[] payload;
        try { payload = DailyParsers.BuildEqualizer(edit, before.EqualizerFrequencies,
            preserveReportedName: existing is not null && existing.Name == edit.Name); }
        catch (Exception) { return Reject(result, "EQ 名称、十段频率或增益未通过校验。"); }
        bool Match(CustomEqualizer x) => x.Name == edit.Name && x.MinimumGain == edit.MinimumGain &&
            x.MaximumGain == edit.MaximumGain && x.Bands.SequenceEqual(edit.Bands) &&
            (edit.Id != 0 ? x.Id == edit.Id : !before.CustomEqualizers.Any(old => old.Id == x.Id));
        await SendAndObserveAsync(0x0418, payload, s => s.CustomEqualizersRead &&
            s.CustomEqualizers.Count(x => Match(x) && x.Selected) == 1, result, token);
        if (!result.Success)
        {
            if (result.After is { CustomEqualizersRead: true } after && after.CustomEqualizers.Count(Match) == 1)
                result.PartialEffects.Add("自定义 EQ 内容已回读匹配，选中状态或设置响应尚未完整确认。");
            return Finish(result);
        }
        result.PartialEffects.Add("自定义 EQ 已保存并选用设备回读的条目。");
        return Finish(result);
    }
}
