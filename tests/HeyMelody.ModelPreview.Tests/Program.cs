using System.Text.Json;

var native = false;
var occlusionOnly = false;
string? requestedOutput = null;
for (var index = 0; index < args.Length; index++)
{
    if (args[index] == "--native-smoke") native = true;
    else if (args[index] == "--native-occlusion") { native = true; occlusionOnly = true; }
    else if (args[index] == "--output-dir" && ++index < args.Length) requestedOutput = args[index];
    else { Console.Error.WriteLine("Usage: [--native-smoke | --native-occlusion] [--output-dir <repository/artifacts/path>]"); return 2; }
}
var workspace = new DirectoryInfo(AppContext.BaseDirectory);
while (workspace is not null && !File.Exists(Path.Combine(workspace.FullName, "src", "HeyMelody.App", "HeyMelody.App.csproj")))
    workspace = workspace.Parent;
if (workspace is null) throw new InvalidOperationException("Repository root unavailable.");
var artifactRoot = Path.GetFullPath(Path.Combine(workspace.FullName, "artifacts"));
var output = requestedOutput is null ? Path.Combine(artifactRoot,
    "model-preview-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8])
    : Path.GetFullPath(requestedOutput, workspace.FullName);
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
    if (native) nativeResult = NativeModelSmoke.Run(output, Check, occlusionOnly);
}
catch (Exception error) { failure = error.ToString(); }
var result = new
{
    schemaVersion = 1,
    success = failure is null,
    passed = checks.Count,
    checks,
    nativeRequested = native,
    nativeOcclusionOnly = occlusionOnly,
    nativeBluetoothCalls = 0,
    appAssemblySha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        File.ReadAllBytes(typeof(HeyMelodyWindows.App.ModelOrbitMotion).Assembly.Location))).ToLowerInvariant(),
    rendererAssemblySha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        File.ReadAllBytes(typeof(Ava3D.Ava3DView).Assembly.Location))).ToLowerInvariant(),
    cameraFitParameters = typeof(Ava3D.Camera).GetMethod("Fit", [typeof(Ava3D.BoundingBox), typeof(float)])!.GetParameters()
        .Select(parameter => new { parameter.Name, type = parameter.ParameterType.FullName,
            parameter.HasDefaultValue, defaultValue = parameter.HasDefaultValue ? parameter.DefaultValue : null }).ToArray(),
    nativeResult,
    scope = "Source validation. Deterministic motion checks; optional real Win32 compositor and Ava3D frame capture. No Bluetooth service, model network fetch or user settings writes.",
    failure
};
var report = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
File.WriteAllText(Path.Combine(output, "report.json"), report);
Console.WriteLine(report);
return failure is null ? 0 : 1;
