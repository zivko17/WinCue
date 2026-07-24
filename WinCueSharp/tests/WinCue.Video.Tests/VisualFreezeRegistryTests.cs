// =============================================================================
// VisualFreezeRegistryTests.cs — el congelado del saliente NO filtra slots
// =============================================================================
// El encargo dice literalmente lo que aquí se comprueba: «una liberación
// diferida que nunca llegue deja el pool sin superficies». Estos tests recorren
// TODOS los caminos de cierre sin necesidad de GPU: cruce completado, cruce
// cancelado, STOP/STOP ALL, PANIC, fin/recarga de show, cambio de modo del
// compositor y el TOPE DE SEGURIDAD por tiempo.
// =============================================================================

using WinCue.Video;

namespace WinCue.Video.Tests;

public class VisualFreezeRegistryTests
{
    private static long Now => Environment.TickCount64;

    [Fact]
    public void Congelar_NoLiberaHastaQueSePide()
    {
        var reg = new VisualFreezeRegistry();
        int freed = 0;

        reg.Freeze(7, Now + 5_000, () => freed++);

        Assert.True(reg.IsFrozen(7));
        Assert.Equal(1, reg.Count);
        Assert.Equal(0, freed);   // el cruce sigue vivo: el fotograma se queda
    }

    [Fact]
    public void CruceCompletado_LiberaElSlotUnaSolaVez()
    {
        var reg = new VisualFreezeRegistry();
        int freed = 0;
        reg.Freeze(7, Now + 5_000, () => freed++);

        Assert.True(reg.Release(7));
        Assert.False(reg.Release(7));   // idempotente: el slot ya volvió al pool

        Assert.Equal(1, freed);
        Assert.False(reg.IsFrozen(7));
        Assert.Equal(0, reg.Count);
    }

    [Fact]
    public void StopAllPanicYRecarga_LiberanTodosLosCongelados()
    {
        var reg = new VisualFreezeRegistry();
        var freed = new List<int>();
        reg.Freeze(1, Now + 30_000, () => freed.Add(1));
        reg.Freeze(2, Now + 30_000, () => freed.Add(2));
        reg.Freeze(3, Now + 30_000, () => freed.Add(3));

        Assert.Equal(3, reg.ReleaseAll());

        Assert.Equal(new[] { 1, 2, 3 }, freed);
        Assert.Equal(0, reg.Count);
        Assert.Equal(0, reg.ReleaseAll());   // segunda pasada: nada que soltar
    }

    [Fact]
    public void TopeDeSeguridad_ElCongeladoNoSobreviveASuPlazo()
    {
        var reg = new VisualFreezeRegistry();
        int freed = 0;
        long deadline = Now + 1_000;
        reg.Freeze(4, deadline, () => freed++);

        // Dentro del plazo (y dentro del margen de gracia): sigue en pantalla
        Assert.Equal(0, reg.ReleaseExpired(deadline - 1));
        Assert.Equal(0, reg.ReleaseExpired(deadline + VisualFreezeRegistry.FreezeGraceMs - 1));
        Assert.True(reg.IsFrozen(4));

        // Pasado el plazo + gracia: lo reclama el barrido, pase lo que pase
        Assert.Equal(1, reg.ReleaseExpired(deadline + VisualFreezeRegistry.FreezeGraceMs));
        Assert.Equal(1, freed);
        Assert.Equal(0, reg.Count);
    }

    [Fact]
    public void FundidoLarguisimo_SigueCongeladoMientrasDuraElCruce()
    {
        // NO hay tope duro de duración: un cruce de 10 minutos sostiene su
        // fotograma los 10 minutos (los fundidos largos son legítimos)
        var reg = new VisualFreezeRegistry();
        int freed = 0;
        long deadline = Now + 600_000;
        reg.Freeze(9, deadline, () => freed++);

        Assert.Equal(0, reg.ReleaseExpired(Now + 599_000));
        Assert.True(reg.IsFrozen(9));
        Assert.Equal(0, freed);
    }

    [Fact]
    public void CruceCancelado_LaPistaLaTomaOtro_SueltaSoloLosSuyos()
    {
        var reg = new VisualFreezeRegistry();
        var freed = new List<int>();
        reg.Freeze(10, Now + 9_000, () => freed.Add(10));   // pista 2
        reg.Freeze(20, Now + 9_000, () => freed.Add(20));   // pista 3

        // Otro vídeo toma la pista 2 sin cruce: solo se suelta el suyo
        Assert.Equal(1, reg.ReleaseWhere(id => id == 10));

        Assert.Equal(new[] { 10 }, freed);
        Assert.True(reg.IsFrozen(20));
    }

    [Fact]
    public void CongelarDosVecesElMismoCue_SueltaElAnterior()
    {
        // Retrigger del cue mientras su fotograma anterior sigue en pantalla:
        // jamás pueden quedar DOS slots reservados por el mismo cue
        var reg = new VisualFreezeRegistry();
        var freed = new List<string>();
        reg.Freeze(5, Now + 5_000, () => freed.Add("viejo"));
        reg.Freeze(5, Now + 5_000, () => freed.Add("nuevo"));

        Assert.Equal(new[] { "viejo" }, freed);
        Assert.Equal(1, reg.Count);

        reg.ReleaseAll();
        Assert.Equal(new[] { "viejo", "nuevo" }, freed);
    }

    [Fact]
    public void UnReleaseQueFalla_NoBloqueaALosDemas()
    {
        // Un sink dañado no puede dejar el registro atascado: la entrada sale
        // ANTES de invocar y los demás congelados se liberan igual
        var reg = new VisualFreezeRegistry();
        bool other = false;
        reg.Freeze(1, Now + 1_000, () => throw new InvalidOperationException("sink roto"));
        reg.Freeze(2, Now + 1_000, () => other = true);

        Assert.Equal(2, reg.ReleaseAll());

        Assert.True(other);
        Assert.Equal(0, reg.Count);
        Assert.False(reg.IsFrozen(1));
    }

    [Fact]
    public void CueSinCongelar_ReleaseEsNoOp()
    {
        var reg = new VisualFreezeRegistry();
        Assert.False(reg.Release(99));
        Assert.Equal(0, reg.Count);
        Assert.Empty(reg.FrozenCues);
    }
}
