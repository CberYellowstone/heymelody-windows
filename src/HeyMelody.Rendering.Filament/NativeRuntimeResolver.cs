using System.Runtime.InteropServices;

namespace HeyMelody.Rendering.Filament;

internal readonly record struct NativeRuntimeLocation(string RuntimeIdentifier, string LibraryFileName);

/// <summary>Maps the executing process to a packaged runtime; never resolves native code from the global PATH.</summary>
internal static class NativeRuntimeResolver
{
    internal static NativeRuntimeLocation Resolve(OSPlatform operatingSystem, Architecture architecture)
    {
        const string name = "HeyMelody.Filament.Native";
        if (operatingSystem == OSPlatform.Windows && architecture is Architecture.X64 or Architecture.Arm64)
            return new(architecture == Architecture.X64 ? "win-x64" : "win-arm64", name + ".dll");
        if (operatingSystem == OSPlatform.OSX && architecture is Architecture.X64 or Architecture.Arm64)
            return new(architecture == Architecture.X64 ? "osx-x64" : "osx-arm64", "lib" + name + ".dylib");
        if (operatingSystem == OSPlatform.Linux && architecture is Architecture.X64 or Architecture.Arm64)
            return new(architecture == Architecture.X64 ? "linux-x64" : "linux-arm64", "lib" + name + ".so");
        throw new PlatformNotSupportedException($"The Filament native runtime does not support {operatingSystem}/{architecture}.");
    }

    internal static NativeRuntimeLocation ResolveCurrent()
        => Resolve(OperatingSystem.IsWindows() ? OSPlatform.Windows : OperatingSystem.IsMacOS() ? OSPlatform.OSX
            : OperatingSystem.IsLinux() ? OSPlatform.Linux : OSPlatform.Create("Unsupported"), RuntimeInformation.ProcessArchitecture);

    internal static string FindLibraryPath(string baseDirectory, NativeRuntimeLocation runtime)
    {
        var root = Path.GetFullPath(baseDirectory);
        var runtimePath = Path.Combine(root, "runtimes", runtime.RuntimeIdentifier, "native", runtime.LibraryFileName);
        if (File.Exists(runtimePath)) return runtimePath;
        var flatPath = Path.Combine(root, runtime.LibraryFileName);
        if (File.Exists(flatPath)) return flatPath;
        throw new DllNotFoundException($"The packaged Filament runtime {runtime.RuntimeIdentifier}/{runtime.LibraryFileName} was not found.");
    }

    internal static string FindProbePath(string libraryPath)
    {
        var absoluteLibrary = Path.GetFullPath(libraryPath);
        var probe = Path.Combine(Path.GetDirectoryName(absoluteLibrary)!,
            Path.GetExtension(absoluteLibrary).Equals(".dll", StringComparison.OrdinalIgnoreCase)
                ? "hm_filament_probe.exe" : "hm_filament_probe");
        if (!File.Exists(probe)) throw new FileNotFoundException("The packaged Filament backend probe was not found.", probe);
        return probe;
    }

    internal static string FindManifestPath(string libraryPath)
    {
        var manifest = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(libraryPath))!, "filament-runtime.json");
        if (!File.Exists(manifest)) throw new FileNotFoundException("The packaged Filament runtime manifest was not found.", manifest);
        return manifest;
    }
}
