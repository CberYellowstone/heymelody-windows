using System.Text.Json;
using HeyMelodyWindows.Devices;

internal static class FirmwareControlPolicyTests
{
    internal static void ConditionsAndDiagnostics()
    {
        var conditions = Conditions("""
            {"minAppVersion":17006004,"minFirmVersion":150,"minFirmwareVersion":"1.2"}
            """);
        var oldFirmware = new DeviceVersionContext("115", 17006004);
        Equal(VersionConditionStatus.Fail, conditions.Evaluate(oldFirmware));
        Equal(VersionConditionStatus.Pass, conditions.EvaluateForControl(oldFirmware));
        var issues = conditions.GetFirmwareIssues(oldFirmware);
        Equal(2, issues.Count);
        Equal("minFirmVersion", issues[0].SourceField);
        Equal(150, issues[0].Minimum.GetInt32());
        Equal("115", issues[0].CurrentVersion);
        Equal(VersionConditionStatus.Fail, issues[0].Status);
        Equal("minFirmwareVersion", issues[1].SourceField);
        Equal("1.2", issues[1].Minimum.GetString());
        Equal(VersionConditionStatus.Conditional, issues[1].Status);
        Equal(VersionConditionStatus.Pass, conditions.EvaluateForControl(new(AndroidAppVersionMarker: 17006004)));
        Require(conditions.GetFirmwareIssues(new(AndroidAppVersionMarker: 17006004))
            .All(x => x.Status == VersionConditionStatus.Conditional && x.CurrentVersion is null), "Missing firmware diagnostics were lost");
        Equal(VersionConditionStatus.Fail, conditions.EvaluateForControl(new("999", 17006003)));
        Equal(VersionConditionStatus.Conditional, conditions.EvaluateForControl(new("999")));
        Equal(VersionConditionStatus.Conditional, Conditions("""{"minAppVersion":"bad","minFirmVersion":150}""")
            .EvaluateForControl(new("115", 17006004)));

        var malformed = Conditions("""{"minFirmVersion":{"future":true}}""");
        Equal(VersionConditionStatus.Conditional, malformed.Evaluate(new("115")));
        Equal(VersionConditionStatus.Pass, malformed.EvaluateForControl(new("115")));
        Require(malformed.GetFirmwareIssues(new("115")).Single().Minimum.GetProperty("future").GetBoolean(), "Detached minimum was lost");
        Equal(0, Conditions("""{"minFirmVersion":0}""").GetFirmwareIssues().Count);
        Equal(0, Conditions("""{"minFirmware":"1.2"}""").GetFirmwareIssues(new("1.2.0")).Count);
        var readOnly = (IList<FirmwareConditionIssue>)issues;
        bool rejected = false; try { readOnly.Clear(); } catch (NotSupportedException) { rejected = true; }
        Require(rejected, "Firmware diagnostics are mutable");
    }

    internal static void EqualizerSelectionAndDiagnostics()
    {
        var profile = Profile("""
            {"id":"ABCDEF","function":{
             "equalizerModeCompat":[{"protocolIndex":7,"modeType":34,"minFirmVersion":118,"order":1}],
             "equalizerModeByColor":[{"protocolIndex":8,"modeType":80,"colorId":0,"minFirmVersion":118,"order":1}],
             "equalizerModeByVersion":[{"protocolIndex":1,"modeType":41,"minAppVersion":17006004,"minFirmVersion":118,"order":1}],
             "equalizerMode":[{"protocolIndex":0,"modeType":26,"order":2},{"protocolIndex":7,"modeType":7,"order":1}]}}
            """);
        var versions = new DeviceVersionContext("115.119.101", 17006004);
        var compat = profile.EqualizerModeCompat.Single();
        Equal(VersionConditionStatus.Fail, compat.EvaluateConditions(versions));
        Equal(VersionConditionStatus.Pass, compat.EvaluateConditionsForControl(versions));
        var issue = compat.GetFirmwareIssues(versions).Single();
        Equal(VersionConditionStatus.Fail, issue.Status);
        Equal("115.119.101", issue.CurrentVersion);
        Equal(118, issue.Minimum.GetInt32());
        Equal(0, compat.GetFirmwareIssues(new("118.119.101")).Count);
        Equal(VersionConditionStatus.Conditional, compat.GetFirmwareIssues(new("115.bad.101")).Single().Status);

        Sequence(new[] { 34, 41, 7, 26 }, profile.GetEffectiveEqualizerModes(versions).Select(x => x.ModeType));
        Sequence(new[] { 34, 80, 41, 7, 26 }, profile.GetEffectiveEqualizerModes(versions, 0).Select(x => x.ModeType));
        Sequence(new[] { 34, 41, 7, 26 }, profile.GetEffectiveEqualizerModes(versions, 1).Select(x => x.ModeType));
        Equal(2, profile.GetEffectiveEqualizerModes(versions).Count(x => x.ProtocolIndex == 7));
        Sequence(new[] { 34, 7, 26 }, profile.GetEffectiveEqualizerModes(new("999", 17006003)).Select(x => x.ModeType));
        Sequence(new[] { 34, 7, 26 }, profile.GetEffectiveEqualizerModes(new("999")).Select(x => x.ModeType));
        Sequence(new[] { 34, 41, 7, 26 }, profile.GetEffectiveEqualizerModes(new(AndroidAppVersionMarker: 17006004)).Select(x => x.ModeType));
        Require(profile.EqualizerModeByVersion.Single().GetFirmwareIssues(new(AndroidAppVersionMarker: 17006004))
            .All(x => x.SourceField == "minFirmVersion" && x.Status == VersionConditionStatus.Conditional), "App requirement became a firmware warning");
    }

    private static VersionConditions Conditions(string json)
    { using var document = JsonDocument.Parse(json); return VersionConditions.FromSource(document.RootElement); }
    private static DeviceProfile Profile(string json)
    { using var document = JsonDocument.Parse(json); return DeviceProfile.FromResource(document.RootElement, "test"); }
    private static void Sequence<T>(IEnumerable<T> expected, IEnumerable<T> actual)
    { Require(expected.SequenceEqual(actual), $"Expected [{string.Join(',', expected)}]; got [{string.Join(',', actual)}]"); }
    private static void Equal<T>(T expected, T actual)
    { Require(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}; got {actual}"); }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
