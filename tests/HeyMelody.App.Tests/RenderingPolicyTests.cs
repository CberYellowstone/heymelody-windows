using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Platform;
using HeyMelodyWindows.Ui;

internal static class RenderingPolicyTests
{
    private static int _checks;

    internal static int Run()
    {
        _checks = 0;
        var defaults = RenderingSetup.CreateWindowsOptions(false,
            () => throw new InvalidOperationException("Default selection must not query WARP."));
        Check(defaults.GraphicsAdapterSelectionCallback!([Adapter("Adreno"), Adapter("Basic")]) == 0,
            "The default selects DXGI's first adapter, including Adreno.");
        Check(defaults.GraphicsAdapterSelectionCallback!([Adapter("Basic"), Adapter("NVIDIA")]) == 0,
            "The default does not guess hardware or override DXGI enumeration order.");
        Check(defaults.RenderingMode.SequenceEqual([Win32RenderingMode.AngleEgl, Win32RenderingMode.Software]),
            "The default retains the ANGLE to Software initialization fallback.");
        Check(defaults.CompositionMode.SequenceEqual([Win32CompositionMode.WinUIComposition,
                Win32CompositionMode.DirectComposition, Win32CompositionMode.RedirectionSurface]),
            "The existing composition order is preserved.");

        byte[] warpLuid = [1, 2, 3, 4, 5, 6, 7, 8];
        var basic = RenderingSetup.CreateWindowsOptions(true, () => warpLuid);
        var select = basic.GraphicsAdapterSelectionCallback!;
        Check(basic.RenderingMode.SequenceEqual([Win32RenderingMode.AngleEgl, Win32RenderingMode.Software]),
            "A resolved WARP adapter uses ANGLE with Software as the failure fallback.");
        Check(select([Adapter("Microsoft Basic Render Driver", [9, 9, 9, 9, 9, 9, 9, 9]),
                Adapter("Unrelated or localized name", warpLuid)]) == 1,
            "WARP identification uses LUID rather than a misleading or localized description.");
        Check(select([Adapter(null, warpLuid), Adapter("Hardware")]) == 0,
            "WARP can occupy the first slot and need not have a description.");
        Throws(() => select([Adapter("Microsoft Basic Render Driver"), Adapter("Adreno", [1, 2])]),
            "Missing or malformed adapter LUIDs must not silently select hardware.");
        Throws(() => select([]), "An empty callback list must not return an adapter index.");

        warpLuid[0] = 99;
        Check(select([Adapter("WARP", [1, 2, 3, 4, 5, 6, 7, 8])]) == 0,
            "The selected identity does not change when the resolver's buffer changes.");
        Throws(() => select([Adapter("WARP", warpLuid)]), "The mutated resolver buffer is not accepted.");

        foreach (var missing in new byte[]?[] { null, [], [1, 2, 3] })
        {
            var unavailable = RenderingSetup.CreateWindowsOptions(true, () => missing);
            Check(unavailable.RenderingMode.SequenceEqual([Win32RenderingMode.Software]),
                "An unavailable WARP identity selects Software before ANGLE initialization.");
            Check(unavailable.GraphicsAdapterSelectionCallback is null,
                "Software-only initialization has no callback that can reselect hardware.");
        }
        var failed = RenderingSetup.CreateWindowsOptions(true, () => throw new COMException("DXGI unavailable"));
        Check(failed.RenderingMode.SequenceEqual([Win32RenderingMode.Software]),
            "A DXGI query exception is contained and leaves Basic mode on Software.");

        Check(Marshal.SizeOf<WindowsWarpAdapterResolver.AdapterLuid>() == 8 &&
                Marshal.OffsetOf<WindowsWarpAdapterResolver.AdapterLuid>("HighPart").ToInt32() == 4,
            "Native LUID consists of two adjacent 32-bit fields.");
        var descriptionSize = IntPtr.Size == 8 ? 312 : 296;
        var luidOffset = IntPtr.Size == 8 ? 296 : 284;
        Check(Marshal.SizeOf<WindowsWarpAdapterResolver.AdapterDescription1>() == descriptionSize &&
                Marshal.OffsetOf<WindowsWarpAdapterResolver.AdapterDescription1>("AdapterLuid").ToInt32() == luidOffset,
            "DXGI_ADAPTER_DESC1 has the correct ABI for the current process pointer size.");
        var nativeLuid = new WindowsWarpAdapterResolver.AdapterLuid
            { LowPart = 0x11223344, HighPart = unchecked((int)0x8899aabb) };
        Check(nativeLuid.ToBytes().SequenceEqual(new byte[] { 0x44, 0x33, 0x22, 0x11, 0xbb, 0xaa, 0x99, 0x88 }),
            "Native LUID byte order matches Avalonia's little-endian DeviceLuid bytes.");

        Console.WriteLine($"PASS: rendering adapter policy ({_checks} checks; no windows or Bluetooth).");
        return 0;
    }

    private static PlatformGraphicsDeviceAdapterDescription Adapter(string? name, byte[]? luid = null)
        => new() { Description = name, DeviceLuid = luid };

    private static void Throws(Action action, string message)
    {
        try { action(); }
        catch (InvalidOperationException) { Check(true, message); return; }
        Check(false, message);
    }

    private static void Check(bool condition, string message)
    {
        _checks++;
        if (!condition) throw new InvalidOperationException(message);
    }
}
