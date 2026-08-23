// =============================================================================
// WaveformLayoutTests.cs — red del cálculo del waveform (traslado a Win2D).
// =============================================================================
// Fija los valores que WaveformControl.cs (WinCue.App) tenía HOY: submuestreo
// de la onda a buckets, proyección de buckets sobre columnas de píxel, mapeo
// tiempo↔píxel y el hit-test/arrastre de los manejadores de trim. El
// submuestreo es la parte que el encargo pide fijar con valores concretos
// (una onda de N muestras en M píxeles debe dar los mismos picos que hoy):
// por eso los dos primeros bloques construyen ondas sintéticas a mano en vez
// de decodificar un archivo real.
// =============================================================================

using WinCue.Presentation;

namespace WinCue.Presentation.Tests;

public class WaveformLayoutTests
{
    // ═════════════════════════ ComputePeaks (muestras → buckets) ═══════════

    [Fact]
    public void MonoSinSubmuestreoDaUnBucketPorFrame()
    {
        // 4 frames mono, buckets pedidos (8) > frames (4) → un bucket por frame,
        // con min == max (una sola muestra por bucket).
        float[] samples = { 0.1f, -0.2f, 0.5f, -0.9f };
        var peaks = WaveformLayout.ComputePeaks(samples, channels: 1, frameCount: 4, bucketCap: 8);

        Assert.Equal(4, peaks.Count);
        Assert.Equal(new[] { 0.1f, -0.2f, 0.5f, -0.9f }, peaks.Mins);
        Assert.Equal(new[] { 0.1f, -0.2f, 0.5f, -0.9f }, peaks.Maxs);
    }

    [Fact]
    public void MonoConSubmuestreoAgrupaFramesPorBucket()
    {
        // 8 frames mono en 2 buckets: bucket0 = frames[0..4), bucket1 = frames[4..8).
        float[] samples = { 0f, 1f, -1f, 0.5f, 0.2f, -0.3f, 0.9f, -0.9f };
        var peaks = WaveformLayout.ComputePeaks(samples, channels: 1, frameCount: 8, bucketCap: 2);

        Assert.Equal(2, peaks.Count);
        Assert.Equal(-1f, peaks.Mins[0]);
        Assert.Equal(1f, peaks.Maxs[0]);
        Assert.Equal(-0.9f, peaks.Mins[1]);
        Assert.Equal(0.9f, peaks.Maxs[1]);
    }

    [Fact]
    public void EstereoMezclaCanalesAMonoAntesDelMinMax()
    {
        // 2 frames estéreo: frame0 = (1.0, -1.0) → media 0.0; frame1 = (0.4, 0.2) → media 0.3.
        float[] samples = { 1.0f, -1.0f, 0.4f, 0.2f };
        var peaks = WaveformLayout.ComputePeaks(samples, channels: 2, frameCount: 2, bucketCap: 8);

        Assert.Equal(2, peaks.Count);
        Assert.Equal(new[] { 0.0f, 0.3f }, peaks.Mins);
        Assert.Equal(new[] { 0.0f, 0.3f }, peaks.Maxs);
    }

    [Fact]
    public void SinFramesLanzaExcepcionConElMismoMensajeDeHoy()
    {
        // WaveformControl atrapa esta excepción y muestra "no disponible":
        // el mensaje exacto es parte del contrato, no un detalle interno.
        var exc = Assert.Throws<InvalidOperationException>(
            () => WaveformLayout.ComputePeaks(Array.Empty<float>(), channels: 1, frameCount: 0));
        Assert.Equal("sin audio", exc.Message);
    }

    [Fact]
    public void RespetaElTopeDeBucketsPorDefecto()
    {
        // 10 frames, sin bucketCap explícito → nunca más buckets que frames,
        // y el tope de fábrica es 1600 (Buckets), muy por encima de 10.
        float[] samples = new float[10];
        for (int i = 0; i < 10; i++) samples[i] = i / 10f;
        var peaks = WaveformLayout.ComputePeaks(samples, channels: 1, frameCount: 10);

        Assert.Equal(10, peaks.Count);
        Assert.Equal(1600, WaveformLayout.Buckets);
    }

    // ═════════════════════════ ColumnBucketIndex (buckets → columnas) ══════

    [Fact]
    public void MenosColumnasQueBucketsEligeVecinoMasCercano()
    {
        // 4 buckets proyectados sobre 2 columnas: mismo cálculo entero que
        // OnRender hacía en línea, columna por columna.
        Assert.Equal(0, WaveformLayout.ColumnBucketIndex(0, width: 2, bucketCount: 4));
        Assert.Equal(2, WaveformLayout.ColumnBucketIndex(1, width: 2, bucketCount: 4));
    }

    [Fact]
    public void MasColumnasQueBucketsRepiteBuckets()
    {
        // 2 buckets estirados sobre 4 columnas: las dos primeras columnas caen
        // en el bucket 0, las dos últimas en el bucket 1.
        Assert.Equal(0, WaveformLayout.ColumnBucketIndex(0, width: 4, bucketCount: 2));
        Assert.Equal(0, WaveformLayout.ColumnBucketIndex(1, width: 4, bucketCount: 2));
        Assert.Equal(1, WaveformLayout.ColumnBucketIndex(2, width: 4, bucketCount: 2));
        Assert.Equal(1, WaveformLayout.ColumnBucketIndex(3, width: 4, bucketCount: 2));
    }

    [Fact]
    public void LaUltimaColumnaNuncaSeSaleDelUltimoBucket()
    {
        Assert.Equal(3, WaveformLayout.ColumnBucketIndex(99, width: 100, bucketCount: 4));
    }

    // ═════════════════════════ Mapeo tiempo ↔ píxel ═════════════════════════

    [Fact]
    public void SecToXEsProporcionalAlAncho()
    {
        Assert.Equal(50.0, WaveformLayout.SecToX(sec: 5, duration: 10, width: 100));
        Assert.Equal(0.0, WaveformLayout.SecToX(sec: 0, duration: 10, width: 100));
        Assert.Equal(100.0, WaveformLayout.SecToX(sec: 10, duration: 10, width: 100));
    }

    [Fact]
    public void SecToXRecortaFueraDeRango()
    {
        Assert.Equal(100.0, WaveformLayout.SecToX(sec: 999, duration: 10, width: 100));
        Assert.Equal(0.0, WaveformLayout.SecToX(sec: -5, duration: 10, width: 100));
    }

    [Fact]
    public void SecToXSinDuracionEsCero()
    {
        Assert.Equal(0.0, WaveformLayout.SecToX(sec: 5, duration: 0, width: 100));
    }

    [Fact]
    public void XToSecEsElInversoDeSecToX()
    {
        Assert.Equal(5.0, WaveformLayout.XToSec(x: 50, duration: 10, width: 100), precision: 9);
    }

    [Fact]
    public void EffectiveEndUsaLaDuracionCuandoElFinEsCeroOMenos()
    {
        Assert.Equal(12.0, WaveformLayout.EffectiveEnd(end: 0, duration: 12));
        Assert.Equal(12.0, WaveformLayout.EffectiveEnd(end: -1, duration: 12));
        Assert.Equal(4.0, WaveformLayout.EffectiveEnd(end: 4, duration: 12));
    }

    [Theory]
    [InlineData(0.0, "0:00.0")]
    [InlineData(5.4, "0:05.4")]
    [InlineData(65.0, "1:05.0")]
    public void FormatTimeUsaMinutosSegundosConUnDecimal(double s, string expected)
    {
        // FormatTime interpola sin fijar cultura (igual que el Fmt original de
        // WaveformControl): el separador decimal depende de CurrentCulture. Se
        // fija Invariant aquí para que el test sea determinista sin cambiar el
        // comportamiento real — mismo patrón que TimelineLayoutTests.
        var prev = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        try
        {
            Assert.Equal(expected, WaveformLayout.FormatTime(s));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = prev;
        }
    }

    // ═════════════════════════ Manejadores: hit-test y arrastre ═════════════

    [Fact]
    public void PickHandleGanaElMasCercanoDentroDelRadio()
    {
        Assert.Equal(WaveformHandle.Start, WaveformLayout.PickHandle(x: 12, startX: 10, endX: 90));
        Assert.Equal(WaveformHandle.End, WaveformLayout.PickHandle(x: 88, startX: 10, endX: 90));
    }

    [Fact]
    public void PickHandleFueraDeAmbosRadiosGanaElMasCercanoSinMas()
    {
        Assert.Equal(WaveformHandle.Start, WaveformLayout.PickHandle(x: 30, startX: 10, endX: 90));
        Assert.Equal(WaveformHandle.End, WaveformLayout.PickHandle(x: 70, startX: 10, endX: 90));
    }

    [Fact]
    public void PickHandleEmpateEntreRadiosGanaElInicio()
    {
        // A igual distancia de los dos manejadores, y dentro del radio de
        // ambos, el criterio de OnMouseDown (dStart <= dEnd) da el inicio.
        Assert.Equal(WaveformHandle.Start, WaveformLayout.PickHandle(x: 50, startX: 48, endX: 52));
    }

    [Fact]
    public void DragStartNoPuedeCruzarElFin()
    {
        Assert.Equal(5.0, WaveformLayout.DragStart(sec: 5, currentEnd: 8, duration: 20));
        Assert.Equal(8.0, WaveformLayout.DragStart(sec: 15, currentEnd: 8, duration: 20));
    }

    [Fact]
    public void DragStartConFinHastaElFinalUsaLaDuracion()
    {
        Assert.Equal(20.0, WaveformLayout.DragStart(sec: 999, currentEnd: 0, duration: 20));
    }

    [Fact]
    public void DragEndNoPuedeQuedarAntesDelInicio()
    {
        Assert.Equal(10.0, WaveformLayout.DragEnd(sec: 10, currentStart: 3));
        Assert.Equal(3.0, WaveformLayout.DragEnd(sec: 1, currentStart: 3));
    }
}
