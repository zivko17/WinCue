using WinCue.Core;
using WinCue.Presentation;
using Xunit;

namespace WinCue.Presentation.Tests;

public class TimelineCursorTests
{
    static int _nextId = 1;

    static TimelineBlock B(CueType type, double start, double dur, int layer = 0,
                           bool ghost = false, bool dimmed = false, double trimStart = 0)
        => new()
        {
            Cue = new Cue { Id = _nextId++, Type = type, TrimStart = trimStart },
            Start = start,
            Dur = dur,
            Layer = layer,
            Ghost = ghost,
            Dimmed = dimmed,
        };

    // ── Scrub ────────────────────────────────────────────────────────────

    [Fact]
    public void Scrub_SinVideoBajoElCursor_NoHayNadaQueEnsenar()
    {
        var blocks = new[] { B(CueType.Audio, 0, 10) };
        var (video, _) = TimelineCursor.ScrubTarget(blocks, 5);
        Assert.Null(video);
    }

    [Fact]
    public void Scrub_CuentaElTrimDeEntrada()
    {
        // El bloque empieza en 10 s del show, el archivo entra recortado 2 s:
        // a 13 s del show, el fotograma del ARCHIVO es el 5.
        var blocks = new[] { B(CueType.Video, 10, 20, trimStart: 2) };
        var (video, pos) = TimelineCursor.ScrubTarget(blocks, 13);
        Assert.NotNull(video);
        Assert.Equal(5.0, pos, 6);
    }

    [Fact]
    public void Scrub_ConDosVideosSuperpuestos_MandaElDeLaPistaMasALTA()
    {
        // "Más alta" = índice de capa MENOR: es el que se está viendo.
        var arriba = B(CueType.Video, 0, 10, layer: 0);
        var abajo = B(CueType.Video, 0, 10, layer: 3);
        var (video, _) = TimelineCursor.ScrubTarget(new[] { abajo, arriba }, 5);
        Assert.Same(arriba.Cue, video);
    }

    [Fact]
    public void Scrub_ElFantasmaNoCuenta()
    {
        var blocks = new[] { B(CueType.Video, 0, 10, ghost: true) };
        Assert.Null(TimelineCursor.ScrubTarget(blocks, 5).Video);
    }

    // ── Reproducir desde el cursor ───────────────────────────────────────

    [Fact]
    public void LoQueCRUZAElCursor_ArrancaAMitad()
    {
        var b = B(CueType.Audio, 10, 20);
        var items = TimelineCursor.PlayItems(new[] { b }, Array.Empty<double>(), 15);
        var item = Assert.Single(items);
        Assert.Equal(b.Cue.Id, item.CueId);
        Assert.Equal(5.0, item.Offset, 6);   // 15 − 10
        Assert.Equal(0.0, item.Delay, 6);
    }

    [Fact]
    public void LoQueEmpiezaDESPUES_SeProgramaConSuRetraso()
    {
        var b = B(CueType.Audio, 30, 5);
        var items = TimelineCursor.PlayItems(new[] { b }, Array.Empty<double>(), 10);
        var item = Assert.Single(items);
        Assert.Equal(0.0, item.Offset, 6);
        Assert.Equal(20.0, item.Delay, 6);   // 30 − 10
    }

    [Fact]
    public void LoQueYaTERMINO_NoEntra()
    {
        var b = B(CueType.Audio, 0, 5);
        Assert.Empty(TimelineCursor.PlayItems(new[] { b }, Array.Empty<double>(), 10));
    }

    [Fact]
    public void ElProximoGO_CORTAElSegmento()
    {
        // Reproducir desde la mitad de una sección NO puede arrastrar la
        // siguiente, que espera un GO del operador.
        var dentro = B(CueType.Audio, 12, 3);
        var despues = B(CueType.Audio, 25, 3);
        var items = TimelineCursor.PlayItems(
            new[] { dentro, despues }, new[] { 20.0 }, 10);
        var item = Assert.Single(items);
        Assert.Equal(dentro.Cue.Id, item.CueId);
    }

    [Fact]
    public void UnGoANTESDelCursor_NoCortaNada()
    {
        var b = B(CueType.Audio, 25, 3);
        var items = TimelineCursor.PlayItems(new[] { b }, new[] { 5.0 }, 10);
        Assert.Single(items);
    }

    [Fact]
    public void UnWaitQueCruzaElCursor_NoArrancaAMitad()
    {
        // Empezado por la mitad no aporta: la cadena ya viene resuelta.
        var b = B(CueType.Wait, 5, 20);
        Assert.Empty(TimelineCursor.PlayItems(new[] { b }, Array.Empty<double>(), 10));
    }

    [Fact]
    public void UnFadeQueEmpiezaDESPUES_SiSePrograma()
    {
        var b = B(CueType.Fade, 20, 3);
        Assert.Single(TimelineCursor.PlayItems(new[] { b }, Array.Empty<double>(), 10));
    }

    [Theory]
    [InlineData(true, false)]   // fantasma: lo dispara su cue START
    [InlineData(false, true)]   // atenuado: desarmado o rama que no se dispara
    public void NiFantasmasNiAtenuados(bool ghost, bool dimmed)
    {
        var b = B(CueType.Audio, 20, 5, ghost: ghost, dimmed: dimmed);
        Assert.Empty(TimelineCursor.PlayItems(new[] { b }, Array.Empty<double>(), 10));
    }

    [Fact]
    public void ElGrupoNoSeDispara_LoHacenSusHijos()
    {
        var b = B(CueType.Group, 20, 5);
        Assert.Empty(TimelineCursor.PlayItems(new[] { b }, Array.Empty<double>(), 10));
    }
}
