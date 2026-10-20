using System.Security.Cryptography;
using System.Text.Json;
using HeyMelody.Rendering.Filament;

bool native = false, occlusionOnly = false;
FilamentBackend backend = FilamentBackend.Vulkan;
string? requestedOutput = null, requestedModel = null;
for (int index = 0; index < args.Length; index++)
{
    if (args[index] == "--native-software-smoke")
    {
        Console.Error.WriteLine("Unsupported: both renderer modes use the GPU. Static failure fallback is covered by portable App tests.");
        return 2;
    }
    if (args[index] == "--native-smoke") native = true;
    else if (args[index] == "--native-occlusion") { native = true; occlusionOnly = true; }
    else if (args[index] == "--output-dir" && ++index < args.Length) requestedOutput = args[index];
    else if (args[index] == "--model-file" && ++index < args.Length) requestedModel = args[index];
    else if (args[index] == "--backend" && ++index < args.Length && args[index] is "vulkan" or "opengl")
        backend = args[index] == "opengl" ? FilamentBackend.OpenGL : FilamentBackend.Vulkan;
    else { Console.Error.WriteLine("Usage: [--native-smoke | --native-occlusion] [--backend vulkan|opengl] [--model-file <audited GLB>] [--output-dir <artifacts/path>]"); return 2; }
}
if (requestedModel is not null && !occlusionOnly) { Console.Error.WriteLine("--model-file requires --native-occlusion."); return 2; }
var workspace = HeyMelody.Rendering.Tests.GlbSource.RepositoryRoot();
var artifactRoot = Path.Combine(workspace, "artifacts");
var output = requestedOutput is null ? Path.Combine(artifactRoot,
    "model-preview-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8])
    : Path.GetFullPath(requestedOutput, workspace);
if (!output.StartsWith(artifactRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Test output must remain below this repository's artifacts directory.");
if (Directory.Exists(output)) throw new InvalidOperationException("Use a new test output directory.");
Directory.CreateDirectory(output);
var checks = new List<object>();
object? nativeResult = null;
string? failure = null;
void Check(bool condition, string description)
{
    checks.Add(new { passed = condition, description });
    if (!condition) throw new InvalidOperationException(description);
}
try
{
    if (!occlusionOnly) MotionChecks.Run(Check);
    if (native) nativeResult = NativeModelSmoke.Run(output, Check, backend, occlusionOnly,
        requestedModel is null ? null : Path.GetFullPath(requestedModel, workspace));
}
catch (Exception error) { failure = error.ToString(); }
var result = new
{
    schemaVersion = 2, success = failure is null, passed = checks.Count, checks,
    nativeRequested = native, requestedBackend = backend.ToString(), nativeOcclusionOnly = occlusionOnly, nativeSoftware = false,
    externalModelRequested = requestedModel is not null, nativeBluetoothCalls = 0,
    appAssemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(HeyMelodyWindows.App.ModelOrbitMotion).Assembly.Location))).ToLowerInvariant(),
    rendererAssemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(FilamentModelData).Assembly.Location))).ToLowerInvariant(),
    nativeResult,
    scope = "Portable deterministic motion; optional real Windows x64 Filament offscreen rendering. No Host, Bluetooth, network or user preferences. Model PNGs only.",
    failure
};
File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(JsonSerializer.Serialize(new { result.success, result.passed, result.nativeRequested, result.nativeBluetoothCalls, output, failure }));
return failure is null ? 0 : 1;
