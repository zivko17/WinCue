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

    [Fact]
    public void IsCapturing_IsFalseBeforeStartAndAfterDispose()
    {
        var meter = new LoopbackProgramMeter();
        Assert.False(meter.IsCapturing);   // objeto creado ≠ midiendo
        meter.Start();
        // Si la máquina no tiene loopback utilizable sigue en false y el
        // AudioEngine mide por la mezcla interna: eso es exactamente lo que
        // esta propiedad existe para decir.
        meter.Dispose();
        Assert.False(meter.IsCapturing);
    }

    [Fact]
    public void UnknownDeviceId_WithoutFallback_NeverCaptures()
    {
        // Medidor de AUDICIÓN: sin su dispositivo NO mide (medir otro sería
        // mentir). Reintenta en segundo plano sin capturar ni reventar.
        using var meter = new LoopbackProgramMeter(
            "{0.0.0.00000000}.{no-existe}", fallbackToDefault: false);
        meter.Start();
        meter.Start();
        Assert.False(meter.IsCapturing);
    }

    [Fact]
    public void ManyStartDisposeCycles_DoNotBlockNorAccumulateTimers()
    {
        // Un cambio de driver en caliente reabre el medidor. Con 30 ciclos:
        // (a) ninguno puede bloquear al que llama —era el tirón de la UI— y
        // (b) al final no puede quedar ningún reintento vivo goteando.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 30; i++)
        {
            var meter = new LoopbackProgramMeter();
            meter.Start();
            meter.Dispose();
            meter.Dispose();   // idempotente
            Assert.False(meter.IsCapturing);
        }
        sw.Stop();
        Assert.True(sw.Elapsed.TotalMilliseconds < 2_000,
            $"30 ciclos Start/Dispose bloquearon {sw.Elapsed.TotalMilliseconds:0} ms al llamante");
    }
}
