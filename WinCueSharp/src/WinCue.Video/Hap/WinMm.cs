// =============================================================================
// WinMm.cs — timeBeginPeriod/timeEndPeriod para el hilo de presentación HAP
// (promocionado de tools/hap-lab/HapLab.View/WinMm.cs)
// =============================================================================

using System.Runtime.InteropServices;

namespace WinCue.Video.Hap;

/// <summary>
/// Resolución del timer de Windows (winmm): el hilo de presentación duerme con
/// Thread.Sleep(1) entre frames y sin timeBeginPeriod(1) la granularidad real
/// sería ~15,6 ms (imposible clavar cadencias de 16,7 ms). Es lo que hacen los
/// reproductores de vídeo (mpv incluido).
/// </summary>
internal static partial class WinMm
{
    [LibraryImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial uint BeginPeriod(uint period);

    [LibraryImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial uint EndPeriod(uint period);
}
