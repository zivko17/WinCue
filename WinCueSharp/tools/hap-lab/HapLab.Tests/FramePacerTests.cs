namespace HapLab.Tests;

/// <summary>
/// Tests del selector de frames por reloj (PASO 2): avance normal, drops,
/// clamp al final, monotonía y la semántica Peek/Commit (un Peek sin Commit
/// no consume el frame — es lo que pasa cuando el decoder va tarde).
/// </summary>
public sealed class FramePacerTests
{
    [Fact]
    public void EnCero_TocaElFrameCero()
    {
        FramePacer pacer = new(fps: 60, frameCount: 100);
        PacerStep step = pacer.Peek(0);

        Assert.Equal(0, step.FrameIndex);
        Assert.True(step.IsNew);
        Assert.Equal(0, step.Dropped);
        Assert.False(step.Finished);
    }

    [Fact]
    public void AvanceNormal_UnFramePorPeriodo_SinDrops()
    {
        // Ticks a mitad de cada período de frame (t = (n + 0,5)/fps)
        FramePacer pacer = new(fps: 60, frameCount: 100);
        for (int n = 0; n < 50; n++)
        {
            PacerStep step = pacer.Peek((n + 0.5) / 60.0);
            Assert.Equal(n, step.FrameIndex);
            Assert.True(step.IsNew);
            Assert.Equal(0, step.Dropped);
            pacer.Commit(step);
        }

        Assert.Equal(50, pacer.PresentedCount);
        Assert.Equal(0, pacer.TotalDropped);
    }

    [Fact]
    public void FrameEsFloorDeTPorFps()
    {
        // El frame n es vigente durante [n/fps, (n+1)/fps)
        FramePacer pacer = new(fps: 30, frameCount: 100);
        Assert.Equal(0, pacer.Peek(0.0333).FrameIndex);   // justo antes de 1/30
        Assert.Equal(1, pacer.Peek(0.0334).FrameIndex);   // justo después
        Assert.Equal(29, pacer.Peek(0.999).FrameIndex);
        Assert.Equal(30, pacer.Peek(1.0).FrameIndex);
    }

    [Fact]
    public void SaltoDelReloj_CuentaLosFramesSaltadosComoDrops()
    {
        FramePacer pacer = new(fps: 60, frameCount: 100);
        pacer.Commit(pacer.Peek(0));               // frame 0

        // El siguiente tick llega 4 períodos después: toca el frame 4, se
        // saltaron el 1, 2 y 3
        PacerStep step = pacer.Peek(4.5 / 60.0);
        Assert.Equal(4, step.FrameIndex);
        Assert.True(step.IsNew);
        Assert.Equal(3, step.Dropped);

        pacer.Commit(step);
        Assert.Equal(3, pacer.TotalDropped);
        Assert.Equal(2, pacer.PresentedCount);
    }

    [Fact]
    public void MismoPeriodo_RepiteFrameSinContarNada()
    {
        FramePacer pacer = new(fps: 60, frameCount: 100);
        pacer.Commit(pacer.Peek(0));

        // Segundo tick dentro del período del frame 0 (display a 144 Hz)
        PacerStep step = pacer.Peek(0.008);
        Assert.Equal(0, step.FrameIndex);
        Assert.False(step.IsNew);
        Assert.Equal(0, step.Dropped);
        Assert.Equal(1, pacer.PresentedCount);
    }

    [Fact]
    public void RelojHaciaAtras_EsMonotono()
    {
        FramePacer pacer = new(fps: 60, frameCount: 100);
        pacer.Commit(pacer.Peek(10.5 / 60.0));     // frame 10

        PacerStep step = pacer.Peek(8.0 / 60.0);   // el reloj "retrocede"
        Assert.Equal(10, step.FrameIndex);
        Assert.False(step.IsNew);

        PacerStep negativo = pacer.Peek(-1);
        Assert.Equal(10, negativo.FrameIndex);
        Assert.False(negativo.IsNew);
    }

    [Fact]
    public void FinalDelClip_ClampAlUltimoFrameYFinished()
    {
        FramePacer pacer = new(fps: 30, frameCount: 30);   // clip de 1 s

        // Durante el último período (frame 29) aún no está Finished
        PacerStep ultimo = pacer.Peek(29.5 / 30.0);
        Assert.Equal(29, ultimo.FrameIndex);
        Assert.False(ultimo.Finished);
        pacer.Commit(ultimo);

        // Pasado el final: clamp al 29 y Finished
        PacerStep pasado = pacer.Peek(1.5);
        Assert.Equal(29, pasado.FrameIndex);
        Assert.False(pasado.IsNew);
        Assert.True(pasado.Finished);
    }

    [Fact]
    public void SaltoMasAllaDelFinal_PresentaElUltimoYCuentaDrops()
    {
        FramePacer pacer = new(fps: 30, frameCount: 30);
        pacer.Commit(pacer.Peek(0));               // frame 0

        // El reloj salta directamente más allá del final
        PacerStep step = pacer.Peek(10.0);
        Assert.Equal(29, step.FrameIndex);
        Assert.True(step.IsNew);                   // el 29 aún no se presentó
        Assert.Equal(28, step.Dropped);
        Assert.True(step.Finished);
    }

    [Fact]
    public void PeekSinCommit_NoConsume()
    {
        // El decoder va tarde: se consulta el frame pero no se confirma;
        // el siguiente Peek debe devolver lo mismo (reintento)
        FramePacer pacer = new(fps: 60, frameCount: 100);
        PacerStep primero = pacer.Peek(5.5 / 60.0);
        Assert.Equal(5, primero.FrameIndex);
        Assert.Equal(0, pacer.PresentedCount);

        PacerStep reintento = pacer.Peek(5.7 / 60.0);
        Assert.Equal(5, reintento.FrameIndex);
        Assert.True(reintento.IsNew);
        Assert.Equal(5, reintento.Dropped);   // se saltaron el 0..4
    }

    [Fact]
    public void CommitRepetidoODeFrameViejo_NoHaceNada()
    {
        FramePacer pacer = new(fps: 60, frameCount: 100);
        PacerStep step = pacer.Peek(3.5 / 60.0);
        pacer.Commit(step);
        pacer.Commit(step);                        // repetido: ignorado

        Assert.Equal(1, pacer.PresentedCount);
        Assert.Equal(3, pacer.TotalDropped);       // los drops del primer commit
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(60, 0)]
    [InlineData(-5, 100)]
    public void ParametrosInvalidos_Lanzan(double fps, int frames)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FramePacer(fps, frames));
    }
}
