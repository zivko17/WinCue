// =============================================================================
// LoopbackProgramMeterTests.cs — VU de programa por loopback WASAPI
// =============================================================================
// El medidor debe ser inofensivo en cualquier máquina: sin dispositivo, con
// id obsoleto o con doble Dispose no puede tirar la app (es UI de directo).
// =============================================================================

using WinCue.Audio;

namespace WinCue.Audio.Tests;

public class LoopbackProgramMeterTests
{
    [Fact]
    public void StartAndDoubleDispose_DoesNotThrow()
    {
        var meter = new LoopbackProgramMeter();
        meter.Start();
        meter.Dispose();
        meter.Dispose();   // idempotente
    }

    [Fact]
    public void UnknownDeviceId_FallsBackToDefault_WithoutThrowing()
    {
        using var meter = new LoopbackProgramMeter("{0.0.0.00000000}.{no-existe}");
        meter.Start();
    }

    [Fact]
    public void StartTwice_IsIdempotent()
    {
        using var meter = new LoopbackProgramMeter();
        meter.Start();
        meter.Start();
    }
}
