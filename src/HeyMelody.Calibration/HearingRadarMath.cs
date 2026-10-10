using System.Numerics;
using HeyMelodyWindows.Protocol;

namespace HeyMelodyWindows.Calibration;

/// <summary>Unit-radius presentation values, in ascending 80/250/1200/2400/4800/10000 Hz order.</summary>
public sealed record HearingRadarResult(IReadOnlyList<double> Left, IReadOnlyList<double> Right);

/// <summary>OEM result-page response illustration; these values are not measured hearing acuity.</summary>
public static class HearingRadarMath
{
    public static IReadOnlyList<int> Frequencies { get; } = Array.AsReadOnly(new[] { 80, 250, 1200, 2400, 4800, 10000 });
    private static readonly double[] Scales = [7.5, 7.5, 12.5, 12.5, 15, 15];

    public static HearingRadarResult? Create(CalibrationSnapshot snapshot)
    {
        if (!snapshot.FiltersReady || snapshot.Curves.Count != 1
            || snapshot.ScanResult is not null && snapshot.EarScanCurves.Count != 1) return null;
        var curves = snapshot.Curves.Concat(snapshot.EarScanCurves).ToArray();
        if (!TryEar(curves, true, out var left) || !TryEar(curves, false, out var right)) return null;
        return new(Array.AsReadOnly(left), Array.AsReadOnly(right));
    }

    private static bool TryEar(IReadOnlyList<HearingFilterCurve> curves, bool left, out double[] radii)
    {
        radii = new double[6];
        for (int band = 0; band < radii.Length; band++)
        {
            double gain = 0;
            foreach (var curve in curves)
            {
                var values = left ? curve.Left : curve.Right;
                // The legacy 384-sample representation is not a bank of biquad coefficients.
                if (curve.TotalSamples == 384 || values.Count == 0 || values.Count % 6 != 0) return false;
                int rate = curve.SourceCommand == CalibrationCodecs.EarScanFilterCommand
                    ? curve.SamplingFrequency ?? 0 : 44100;
                if (rate <= Frequencies[band] * 2) return false;
                // Ae.a.M uses an Fs-point grid with a divisor of Fs - 1, then the closest point.
                double gridIndex = Math.Round(Frequencies[band] * (rate - 1d) / rate);
                double omega = 2 * Math.PI * gridIndex / (rate - 1);
                Complex z = Complex.FromPolarCoordinates(1, -omega), z2 = z * z;
                for (int section = 0; section < values.Count; section += 6)
                {
                    for (int i = section; i < section + 6; i++) if (!float.IsFinite(values[i])) return false;
                    Complex p = values[section] + values[section + 1] * z + values[section + 2] * z2;
                    Complex q = values[section + 3] + values[section + 4] * z + values[section + 5] * z2;
                    if (p.Magnitude <= 1e-20 || q.Magnitude <= 1e-20) return false;
                    // The original renderer uses Q/P, the inverse of the applied filter response.
                    gain += 20 * Math.Log10(q.Magnitude / p.Magnitude);
                }
            }
            if (!double.IsFinite(gain)) return false;
            radii[band] = Math.Clamp(10 - 10 * Math.Abs(gain) / Scales[band], 2, 10) / 10;
        }
        return true;
    }
}

/// <summary>Music-source spectrum for the orange illustration, independent of calibration revisions.</summary>
/// <remarks>Call from one audio thread. The result is not a microphone or post-DSP measurement.</remarks>
public sealed class HearingSpectrumAnalyzer
{
    public const int FrameLength = 1024, SampleRate = 48000;
    private static readonly double[] Window = Enumerable.Range(0, FrameLength)
        .Select(i => .5 - .5 * Math.Cos(2 * Math.PI * i / (FrameLength - 1))).ToArray();
    private static readonly double WindowSum = Window.Sum();
    private readonly Complex[] _work = new Complex[FrameLength];
    private double _previousSum;

    public void Reset() => _previousSum = 0;

    public IReadOnlyList<double> Analyze(ReadOnlySpan<float> mono)
    {
        if (mono.Length != FrameLength) throw new ArgumentException("A spectrum frame must contain 1024 mono samples.", nameof(mono));
        for (int i = 0; i < FrameLength; i++) _work[i] = new((float.IsFinite(mono[i]) ? mono[i] : 0) * Window[i], 0);
        for (int i = 1, j = 0; i < FrameLength; i++)
        {
            int bit = FrameLength >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (_work[i], _work[j]) = (_work[j], _work[i]);
        }
        for (int length = 2; length <= FrameLength; length <<= 1)
        {
            Complex step = Complex.FromPolarCoordinates(1, -2 * Math.PI / length);
            for (int start = 0; start < FrameLength; start += length)
            {
                Complex phase = Complex.One;
                for (int i = 0; i < length / 2; i++)
                {
                    Complex even = _work[start + i], odd = _work[start + i + length / 2] * phase;
                    _work[start + i] = even + odd; _work[start + i + length / 2] = even - odd; phase *= step;
                }
            }
        }
        double[] radii = new double[6];
        for (int band = 0; band < radii.Length; band++)
        {
            int bin = (int)Math.Round(HearingRadarMath.Frequencies[band] * FrameLength / (double)SampleRate);
            double magnitude = Math.Clamp(127 * 2 * _work[bin].Magnitude / WindowSum, 0, 127);
            double db = 20 * Math.Log10(Math.Max(magnitude, 1e-12));
            radii[band] = db < 7 ? 2 : db > 35 ? 10 : db * 10 / 35;
        }
        double sum = radii.Sum(), mean = sum / radii.Length;
        for (int i = 0; i < radii.Length; i++)
        {
            double value = Math.Max(radii[i], mean);
            radii[i] = _previousSum > 0 && sum - _previousSum > _previousSum / 6
                ? Math.Min(10, value * 1.1)
                : _previousSum == 0 || sum - _previousSum >= -_previousSum / 6
                    ? value : Math.Max(2, value / 3);
            radii[i] = Math.Clamp(radii[i], 2, 10) / 10;
        }
        _previousSum = sum;
        return Array.AsReadOnly(radii);
    }
}
