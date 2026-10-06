[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class HeyMelodyArchitecture {
    [DllImport("kernel32.dll", SetLastError=true)]
    private static extern bool IsWow64Process2(IntPtr process, out ushort processMachine, out ushort nativeMachine);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    public static string Read() {
        ushort process, native;
        if (!IsWow64Process2(GetCurrentProcess(), out process, out native)) throw new InvalidOperationException();
        if (native == 0xAA64) return "arm64";
        if (native == 0x8664) return "x64";
        if (native == 0x014c) return "x86";
        throw new PlatformNotSupportedException();
    }
}
'@
$architecture = [HeyMelodyArchitecture]::Read()
$executable = Join-Path $PSScriptRoot ($architecture + '\HeyMelody.exe')
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw 'Client files are incomplete. Extract the complete archive first.' }
Start-Process -FilePath $executable -WorkingDirectory (Split-Path -Parent $executable) -WindowStyle Normal
