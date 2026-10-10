using HeyMelodyWindows.App;
using HeyMelodyWindows.Calibration.UI;

internal static class HearingPreviewTests
{
    public static async Task<int> RunAsync()
    {
        var service = new TestService();
        service.State = service.State with { Stage = "Previewing", FiltersReady = true, CanPreview = true,
            PreviewAudioPlaying = true, PreviewMode = "Enhanced", PreviewEpoch = 7 };
        await using (var model = new CalibrationViewModel(service, "operation", "hearing"))
        {
            await model.InitializeAsync();
            var frame = new UiCalibrationSpectrum("operation", "fixture", 42, 7, 1, new double[] { .2, .4, .6, .8, 1, .5 });
            model.AdoptSpectrum(frame);
            var accepted = model.SpectrumLevels;
            foreach (var invalid in new[] { frame with { Handle = "another" }, frame with { BackendInstanceId = "old-backend" },
                frame with { Generation = 41 }, frame with { PreviewEpoch = 6 }, frame with { Sequence = 0 },
                frame with { Sequence = 2, Levels = [.2, .3] }, frame with { Sequence = 2, Levels = [.2, .3, .4, .5, .6, double.NaN] },
                frame with { Sequence = 2, Levels = [.2, .3, .4, .5, .6, 1.1] } })
            {
                model.AdoptSpectrum(invalid); Check(ReferenceEquals(model.SpectrumLevels, accepted), "Stale or invalid telemetry was accepted.");
            }
            for (int sequence = 2; sequence <= 500; sequence++) model.AdoptSpectrum(frame with { Sequence = sequence });
            Check(model.State.Revision == 1 && !model.Busy && model.CanSave && model.CanPreview, "Spectra changed control permission or revision.");
            await model.SendAsync("preview-original");
            Check(service.Inputs is [{ Action: "preview-original" }] && model.State.PreviewAudioPlaying && model.SpectrumLevels.Count == 0,
                "Continuous telemetry invalidated an A/B action or leaked the old enhancement spectrum.");
            model.AdoptSpectrum(frame with { Sequence = 501 });
            Check(model.SpectrumLevels.Count == 0, "The previous enhanced epoch reached original audition.");
            model.AdoptSpectrum(frame with { PreviewEpoch = model.State.PreviewEpoch, Sequence = 501 });
            Check(model.SpectrumLevels.SequenceEqual(frame.Levels), "Original audition did not accept its music spectrum.");
            await model.SendAsync("preview-stop");
            model.AdoptSpectrum(frame with { PreviewEpoch = model.State.PreviewEpoch, Sequence = 502 });
            Check(model.SpectrumLevels.Count == 0, "A late original spectrum revived paused audition.");
        }
        Console.WriteLine("PASS spectrum ownership, validation and revision-independent A/B input");

        service = new TestService();
        service.State = service.State with { Stage = "Previewing", PreviewAudioPlaying = true, PreviewMode = "Enhanced", PreviewEpoch = 9 };
        await using (var model = new CalibrationViewModel(service, "operation", "hearing"))
        {
            await model.InitializeAsync();
            double[] input = [.2, .4, .6, .8, 1, .5];
            var frame = new UiCalibrationSpectrum("operation", "fixture", 42, 9, 1, input);
            model.AdoptSpectrum(frame); input[0] = .9;
            Check(model.SpectrumLevels[0] == .2, "A mutable producer buffer changed the accepted UI frame.");
            foreach (string stage in new[] { "Saving", "Completed", "Failed", "Cancelled" })
            {
                model.Adopt(service.State with { Revision = model.State.Revision + 1, Stage = stage });
                model.AdoptSpectrum(frame with { Sequence = 100 });
                Check(model.SpectrumLevels.Count == 0, "Queued spectrum revived a retired playback stage.");
            }
            model.BackendUnavailable("断开"); model.AdoptSpectrum(frame with { Sequence = 101 });
            Check(model.SpectrumLevels.Count == 0, "A disconnected backend revived spectrum.");
        }
        Console.WriteLine("PASS terminal and disconnected stages reject queued spectrum");
        return 2;
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
