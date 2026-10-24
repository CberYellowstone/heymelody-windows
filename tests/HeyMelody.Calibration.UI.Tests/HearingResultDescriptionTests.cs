using HeyMelodyWindows.Calibration.UI;

internal static class HearingResultDescriptionTests
{
    public static int Run()
    {
        (string Quality, string Experience)[] expected =
        [
            ("力量感", "底鼓更澎湃有力"), ("浑厚感", "男低音更温暖醇厚"),
            ("澎湃感", "底鼓更澎湃有力"), ("节奏感", "底鼓节奏更富激情"),
            ("速度感", "底鼓节奏更富激情"), ("力度感", "底鼓更澎湃有力"),
            ("温暖感", "女低音更温暖柔和"), ("柔和感", "女低音更温暖柔和"),
            ("现场感", "声场更具临场感"), ("饱满感", "人声更温暖生动"),
            ("立体感", "声场更具临场感"), ("氛围感", "声场更具临场感"),
            ("延伸感", "管弦泛音更丰富"), ("细腻感", "女高音更自然柔和"),
            ("明亮感", "管弦细节更丰富"), ("清亮感", "管弦细节更丰富"),
            ("穿透感", "管弦更透亮清晰"), ("通透感", "细节更透亮清晰"),
            ("细节感", "细节更透亮清晰"), ("清晰感", "细节更透亮清晰")
        ];
        string[] frequencies = ["低频", "中频", "高频"];
        for (byte id = 1; id <= expected.Length; id++)
        {
            byte band = id <= 6 ? (byte)0 : id <= 12 ? (byte)1 : (byte)2;
            var description = HearingResultDescription.Create(band, id)
                ?? throw new InvalidOperationException($"Missing OEM completion description {id}.");
            Check(description.Frequency == frequencies[band]
                && (description.Quality, description.Experience) == expected[id - 1],
                $"Completion description {id} does not match the HeyMelody resource mapping.");
            Check(description.Segments.Count == 7 && description.Segments.Where(segment => segment.IsHighlighted)
                .Select(segment => segment.Text).SequenceEqual(new[] { description.Frequency, description.Quality, description.Experience }),
                $"Completion description {id} must highlight exactly the three OEM keywords.");
            Check(description.Text == string.Concat(description.Segments.Select(segment => segment.Text)),
                "Accessible completion text differs from the formatted segments.");
        }
        Check(HearingResultDescription.Create(0, 1)?.Text ==
            "经过耳道结构、听力特征、心理声学、耳机声学系统等多维建模分析，你的听感在低频区间可提升度较高，已为你针对性提升声音的力量感，快去听歌体验，你会发现歌曲中的底鼓更澎湃有力。",
            "The low-frequency completion example must use the original template.");
        Console.WriteLine("PASS all 20 OEM result descriptions and three highlighted keywords");

        foreach (byte? band in new byte?[] { null, 3, 255 })
            Check(HearingResultDescription.Create(band, 1) is null, "An unknown enhancement band invented a completion claim.");
        foreach (byte? id in new byte?[] { null, 0, 21, 255 })
            Check(HearingResultDescription.Create(0, id) is null, "An unknown description ID invented a completion claim.");
        Check(HearingResultDescription.Create(2, 1)?.Frequency == "高频",
            "The enhancement band must come from protocol metadata, not from a description ID range.");
        Console.WriteLine("PASS unknown result metadata falls back without inferred hearing claims");
        return 2;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
