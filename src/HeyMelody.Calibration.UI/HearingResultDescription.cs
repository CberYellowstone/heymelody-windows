namespace HeyMelodyWindows.Calibration.UI;

public sealed record HearingResultDescriptionSegment(string Text, bool IsHighlighted);

public sealed record HearingResultDescriptionText(string Frequency, string Quality, string Experience)
{
    public IReadOnlyList<HearingResultDescriptionSegment> Segments { get; } =
    [
        new("经过耳道结构、听力特征、心理声学、耳机声学系统等多维建模分析，你的听感在", false),
        new(Frequency, true),
        new("区间可提升度较高，已为你针对性提升声音的", false),
        new(Quality, true),
        new("，快去听歌体验，你会发现歌曲中的", false),
        new(Experience, true),
        new("。", false)
    ];

    public string Text => string.Concat(Segments.Select(segment => segment.Text));
}

/// <summary>HeyMelody's completion copy, keyed by the device enhancement band and selected description ID.</summary>
public static class HearingResultDescription
{
    // Ic.C0483l.f3184f/f3185g/h and melody_common_gold_hearing_complete_enhancing_tips.
    // These are descriptive labels from the device result, not an interpretation of radar radii.
    public static HearingResultDescriptionText? Create(byte? enhanceType, byte? restoreDescriptionId)
    {
        string? frequency = enhanceType switch { 0 => "低频", 1 => "中频", 2 => "高频", _ => null };
        (string Quality, string Experience)? description = restoreDescriptionId switch
        {
            1 => ("力量感", "底鼓更澎湃有力"),
            2 => ("浑厚感", "男低音更温暖醇厚"),
            3 => ("澎湃感", "底鼓更澎湃有力"),
            4 => ("节奏感", "底鼓节奏更富激情"),
            5 => ("速度感", "底鼓节奏更富激情"),
            6 => ("力度感", "底鼓更澎湃有力"),
            7 => ("温暖感", "女低音更温暖柔和"),
            8 => ("柔和感", "女低音更温暖柔和"),
            9 => ("现场感", "声场更具临场感"),
            10 => ("饱满感", "人声更温暖生动"),
            11 => ("立体感", "声场更具临场感"),
            12 => ("氛围感", "声场更具临场感"),
            13 => ("延伸感", "管弦泛音更丰富"),
            14 => ("细腻感", "女高音更自然柔和"),
            15 => ("明亮感", "管弦细节更丰富"),
            16 => ("清亮感", "管弦细节更丰富"),
            17 => ("穿透感", "管弦更透亮清晰"),
            18 => ("通透感", "细节更透亮清晰"),
            19 => ("细节感", "细节更透亮清晰"),
            20 => ("清晰感", "细节更透亮清晰"),
            _ => null
        };
        return frequency is not null && description is { } selected
            ? new(frequency, selected.Quality, selected.Experience) : null;
    }
}
