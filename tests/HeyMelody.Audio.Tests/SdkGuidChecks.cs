using System.Text.RegularExpressions;
using HeyMelodyWindows.Audio;

internal static class SdkGuidChecks
{
    internal static void Run(Action<bool, string> check)
    {
        // Snapshot of the actual installed 10.0.26100.0 SDK declarations. These assertions remain
        // usable on offline hosts without the SDK; when installed, also compare directly to its headers.
        var declarations = new (Type Type, string Header, string Name, string Guid, int Line)[]
        {
            (typeof(WasapiNative.DeviceEnumeratorClass), "mmdeviceapi.h", "MMDeviceEnumerator", "BCDE0395-E52F-467C-8E3D-C4579291692E", 1540),
            (typeof(WasapiNative.IDeviceEnumerator), "mmdeviceapi.h", "IMMDeviceEnumerator", "A95664D2-9614-4F35-A746-DE8DB63617E6", 756),
            (typeof(WasapiNative.IDeviceCollection), "mmdeviceapi.h", "IMMDeviceCollection", "0BD7A1BE-7A1A-44DB-8397-CC5392387B5E", 567),
            (typeof(WasapiNative.IDevice), "mmdeviceapi.h", "IMMDevice", "D666063F-1587-4E43-81F1-B948E807363F", 426),
            (typeof(WasapiNative.IPropertyStore), "propsys.h", "IPropertyStore", "886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99", 516),
            (typeof(WasapiNative.IAudioClient), "Audioclient.h", "IAudioClient", "1CB9AD4C-DBFA-4C32-B178-C2F568A703B2", 247),
            (typeof(WasapiNative.IRenderClient), "Audioclient.h", "IAudioRenderClient", "F294ACFC-3146-4483-A7BF-ADDCA7C260E2", 1079),
            (typeof(WasapiNative.IDeviceTopology), "devicetopology.h", "IDeviceTopology", "2A07407E-6497-4A18-9787-32F79BD0D98F", 3735),
            (typeof(WasapiNative.IConnector), "devicetopology.h", "IConnector", "9C2C4058-23F5-41DE-877A-DF3AF236A09E", 3300)
        };
        string sdkDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Windows Kits", "10", "Include", "10.0.26100.0", "um");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int actualHeaderChecks = 0;
        foreach (var declaration in declarations)
        {
            check(declaration.Type.GUID == new Guid(declaration.Guid),
                $"SDK IID snapshot {declaration.Header}:{declaration.Line} {declaration.Name}");
            string path = Path.Combine(sdkDirectory, declaration.Header);
            if (!File.Exists(path)) continue;
            if (!headers.TryGetValue(path, out var text)) headers[path] = text = File.ReadAllText(path);
            var match = Regex.Match(text, "(?:MIDL_INTERFACE|DECLSPEC_UUID)\\s*\\(\\s*\"(?<guid>[0-9A-Fa-f-]{36})\"\\s*\\)\\s*"
                + Regex.Escape(declaration.Name) + "\\b", RegexOptions.CultureInvariant);
            check(match.Success && declaration.Type.GUID == Guid.Parse(match.Groups["guid"].Value),
                $"actual local SDK declaration {declaration.Name}");
            actualHeaderChecks++;
        }
        check(WasapiNative.AudioClientId == typeof(WasapiNative.IAudioClient).GUID, "activation AudioClient IID matches imported interface");
        check(WasapiNative.RenderClientId == typeof(WasapiNative.IRenderClient).GUID, "GetService RenderClient IID matches imported interface");
        check(WasapiNative.TopologyId == typeof(WasapiNative.IDeviceTopology).GUID, "activation topology IID matches imported interface");
        Console.WriteLine($"SDK GUID checks: 9 declaration snapshots, {actualHeaderChecks} actual local header declarations, 3 activation/service constants.");
    }
}
