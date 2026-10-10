using HeyMelodyWindows.Calibration;
using HeyMelodyWindows.Protocol;
using static TestKit;

internal static class HearingRadarTests
{
    public static Task RunAsync(Func<string, Func<Task>, Task> test) => RunCoreAsync(test);
    private static async Task RunCoreAsync(Func<string, Func<Task>, Task> test)
    {
        await test("Radar uses filter responses at all six axes and composes ear-scan sections", () =>
        {
            var hearing = Curve([1, 0, 0, .75f, 0, 0], [1, 0, 0, .5f, 0, 0]);
            var snapshot = new CalibrationSnapshot { FiltersReady = true, Curves = [hearing] };
            var result = HearingRadarMath.Create(snapshot)!;
            double[] scales = [7.5, 7.5, 12.5, 12.5, 15, 15];
            for (int i = 0; i < 6; i++)
            {
                Near(result.Left[i], Math.Clamp(1 - Math.Abs(20 * Math.Log10(.75)) / scales[i], .2, 1));
                Near(result.Right[i], Math.Clamp(1 - Math.Abs(20 * Math.Log10(.5)) / scales[i], .2, 1));
            }
            var scan = Curve([1, 0, 0, 4f / 3, 0, 0], [1, 0, 0, 2, 0, 0], scan: true);
            result = HearingRadarMath.Create(snapshot with { ScanResult = new(1, [1], 1), EarScanCurves = [scan] })!;
            Check(result.Left.All(x => Math.Abs(x - 1) < 1e-6) && result.Right.All(x => Math.Abs(x - 1) < 1e-6),
                "Inverse responses compose before the absolute value and radius clamp.");
            return Task.CompletedTask;
        });
        await test("Radar separates 44100 Hz hearing filters from declared ear-scan sampling rate", () =>
        {
            float[] frequencyResponse = [1, .5f, 0, 1, 0, 0];
            var hearing = Curve(frequencyResponse, frequencyResponse, rate: 48000);
            var result = HearingRadarMath.Create(new() { FiltersReady = true, Curves = [hearing] })!;
            Near(result.Left[5], ExpectedResponse(10000, 44100, 15));
            var identity = Curve([1, 0, 0, 1, 0, 0], [1, 0, 0, 1, 0, 0]);
            result = HearingRadarMath.Create(new() { FiltersReady = true, Curves = [identity], ScanResult = new(1, [1], 1),
                EarScanCurves = [Curve(frequencyResponse, frequencyResponse, scan: true, rate: 22050)] })!;
            Near(result.Left[5], ExpectedResponse(10000, 22050, 15));
            Check(Math.Abs(result.Left[5] - ExpectedResponse(10000, 44100, 15)) > .01);
            return Task.CompletedTask;
        });
        await test("Radar declines incomplete, special or invalid filters instead of inventing a curve", () =>
        {
            var valid = Curve([1, 0, 0, 1, 0, 0], [1, 0, 0, 1, 0, 0]);
            var snapshot = new CalibrationSnapshot { FiltersReady = true, Curves = [valid] };
            Check(HearingRadarMath.Create(snapshot with { FiltersReady = false }) is null);
            Check(HearingRadarMath.Create(snapshot with { Curves = [] }) is null);
            Check(HearingRadarMath.Create(snapshot with { ScanResult = new(1, [1], 1) }) is null);
            foreach (var invalid in new[] { valid with { TotalSamples = 384 }, valid with { Left = [1, 0] },
                valid with { Left = [0, 0, 0, 1, 0, 0] }, valid with { Left = [1, 0, 0, 0, 0, 0] },
                valid with { Right = [float.NaN, 0, 0, 1, 0, 0] } })
                Check(HearingRadarMath.Create(snapshot with { Curves = [invalid] }) is null);
            foreach (ushort? rate in new ushort?[] { null, 0, 16000 })
                Check(HearingRadarMath.Create(snapshot with { ScanResult = new(1, [1], 1),
                    EarScanCurves = [valid with { SourceCommand = CalibrationCodecs.EarScanFilterCommand, SamplingFrequency = rate }] }) is null);
            return Task.CompletedTask;
        });
        await test("Music FFT follows the real PCM frequency bin, remains finite and resets its smoothing", () =>
        {
            var analyzer = new HearingSpectrumAnalyzer();
            var quiet = analyzer.Analyze(new float[1024]); Check(quiet.All(x => Math.Abs(x - .2) < 1e-9));
            float[] high = Enumerable.Range(0, 1024).Select(i => (float)(.9 * Math.Sin(2 * Math.PI * 102 * i / 1024))).ToArray();
            var loud = analyzer.Analyze(high);
            Check(loud[4] > .9 && loud.Where((_, i) => i != 4).All(x => x < loud[4]), "The high-frequency axis reacts to its matching bin.");
            Check(loud.All(x => double.IsFinite(x) && x is >= .2 and <= 1));
            high[50] = float.NaN; high[51] = float.PositiveInfinity;
            Check(analyzer.Analyze(high).All(double.IsFinite));
            analyzer.Reset(); quiet = analyzer.Analyze(new float[1024]); Check(quiet.All(x => Math.Abs(x - .2) < 1e-9));
            try { analyzer.Analyze(new float[32]); throw new Exception("Short frames must fail."); } catch (ArgumentException) { }
            return Task.CompletedTask;
        });
    }
    private static HearingFilterCurve Curve(float[] left, float[] right, bool scan = false, ushort? rate = null)
        => new(1, 3, scan ? null : (byte)1, left, right, 12, scan ? rate ?? 44100 : rate,
            scan ? CalibrationCodecs.EarScanFilterCommand : CalibrationCodecs.FilterCommand);
    private static double ExpectedResponse(int frequency, int rate, double scale)
    {
        double j = Math.Round(frequency * (rate - 1d) / rate), omega = 2 * Math.PI * j / (rate - 1);
        double magnitude = Math.Sqrt(1.25 + Math.Cos(omega));
        return Math.Clamp(1 - Math.Abs(20 * Math.Log10(1 / magnitude)) / scale, .2, 1);
    }
    private static void Near(double actual, double expected) => Check(Math.Abs(actual - expected) < 1e-6,
        $"Expected radius {expected}, got {actual}.");
}
