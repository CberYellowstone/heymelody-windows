using HeyMelody.Rendering.Filament;
using HeyMelody.Rendering.Tests;

internal static class ModelMaterialCorrectionTests
{
    internal static Task RunAsync()
    {
        foreach (var hash in new[] { GlbSource.Free4Sha256, GlbSource.DynaudioSha256, GlbSource.X3BlackSha256, GlbSource.X3WhiteSha256 })
        {
            Check(FilamentModelProfiles.IsAuditedOpaque1001(hash) && FilamentModelProfiles.IsAuditedOpaque1001(hash.ToUpperInvariant()),
                "Every exact audited digest selects the opaque-1001 profile with normalized hash case.");
            Check(!FilamentModelProfiles.IsAuditedOpaque1001(hash[..63] + (hash[^1] == '0' ? "1" : "0")),
                "A one-character digest change cannot inherit an audited material correction.");
        }
        Check(!FilamentModelProfiles.IsAuditedOpaque1001(new string('a', 64))
            && !FilamentModelProfiles.IsAuditedOpaque1001("1001"),
            "Unknown models and a matching material name retain authored transparency.");
        foreach (var name in new[] { "EncoFree4", "EncoFree4Dynaudio" })
        {
            var bytes = File.ReadAllBytes(GlbSource.BundledModel(name));
            var source = GlbSource.Inspect(bytes);
            var model = FilamentModelData.CreateValidated(bytes);
            Check(model.MaterialProfile == "opaque-1001" && source.MaterialName == "1001" && source.AlphaMode == "BLEND"
                && bytes.AsSpan().SequenceEqual(model.Bytes.Span),
                "Selecting the render-time material profile preserves all authored source bytes.");
        }
        return Task.CompletedTask;
    }
    private static void Check(bool condition, string reason)
    { if (!condition) throw new InvalidOperationException(reason); }
}