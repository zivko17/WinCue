// =============================================================================
// FadeSanityTests.cs — el predicado del aviso de fundido incoherente
// =============================================================================
// Regla del encargo: NINGÚN tope duro de duración (los fundidos largos son
// legítimos). Solo se avisa cuando el fundido es MÁS LARGO QUE EL TIEMPO
// SOBRE EL QUE PUEDE ACTUAR. Aquí se fijan los BORDES de esa decisión.
// =============================================================================

using WinCue.Core;

namespace WinCue.Core.Tests;

public class FadeSanityTests
{
    private static Cue Video(Action<Cue> setup)
    {
        var c = new Cue { Id = 1, Type = CueType.Video, Name = "clip" };
        setup(c);
        return c;
    }

    // ── fade_out más largo que el cue ──────────────────────────────────

    [Fact]
    public void FadeOutMasLargoQueElCue_Avisa()
    {
        var cue = Video(c => { c.Duration = 10.0; c.FadeOut = 12.0; });

        Assert.True(FadeSanity.Check(cue).HasFlag(FadeIssues.FadeOutTooLong));
    }

    [Fact]
    public void FadeOutExactamenteIgualALaDuracion_NoAvisa()
    {
        // Borde: cabe justo (el controlador lo clampa a la duración)
        var cue = Video(c => { c.Duration = 10.0; c.FadeOut = 10.0; });

        Assert.Equal(FadeIssues.None, FadeSanity.Check(cue));
    }

    [Fact]
    public void FadeOutLarguisimoPeroConCueLargo_NoAvisa()
    {
        // 5 minutos de fundido en un cue de 10: perfectamente legítimo
        var cue = Video(c => { c.Duration = 600.0; c.FadeOut = 300.0; });

        Assert.Equal(FadeIssues.None, FadeSanity.Check(cue));
    }

    [Fact]
    public void ElTrimRecortaLaDuracionEfectiva_YEntoncesSiAvisa()
    {
        // 60 s de archivo pero solo se reproducen 5 → un fade de 8 no cabe
        var cue = Video(c =>
        {
            c.Duration = 60.0;
            c.TrimStart = 20.0;
            c.TrimEnd = 25.0;
            c.FadeOut = 8.0;
        });

        Assert.Equal(5.0, FadeSanity.EffectiveDuration(cue), 3);
        Assert.True(FadeSanity.Check(cue).HasFlag(FadeIssues.FadeOutTooLong));
    }

    [Fact]
    public void LoopInfinito_NuncaAvisaPorDuracion()
    {
        var cue = Video(c => { c.Duration = 10.0; c.FadeOut = 60.0; c.Loop = true; });

        Assert.Equal(FadeIssues.None, FadeSanity.Check(cue));
    }

    [Fact]
    public void DuracionDesconocida_NoAvisa()
    {
        // Media aún sin sondear: no se inventa un aviso
        var cue = Video(c => { c.Duration = 0.0; c.FadeOut = 30.0; });

        Assert.Equal(FadeIssues.None, FadeSanity.Check(cue));
    }

    [Fact]
    public void FadeInMasLargoQueElCue_TambienAvisa()
    {
        var cue = Video(c => { c.Duration = 4.0; c.FadeIn = 9.0; });

        Assert.True(FadeSanity.Check(cue).HasFlag(FadeIssues.FadeInTooLong));
    }

    // ── crossfade más largo que lo que le queda al saliente ────────────

    [Fact]
    public void CrossfadeMasLargoQueLoQueLeQuedaAlSaliente_Avisa()
    {
        var cue = Video(c => { c.Duration = 30.0; c.FadeIn = 8.0; });

        var issues = FadeSanity.Check(cue, outgoingRemaining: 3.0);

        Assert.True(issues.HasFlag(FadeIssues.CrossfadeTooLong));
        Assert.False(issues.HasFlag(FadeIssues.FadeInTooLong));   // sí cabe en él
    }

    [Fact]
    public void CrossfadeQueCabeJusto_NoAvisa()
    {
        var cue = Video(c => { c.Duration = 30.0; c.FadeIn = 8.0; });

        Assert.Equal(FadeIssues.None, FadeSanity.Check(cue, outgoingRemaining: 8.0));
    }

    [Fact]
    public void SinSaliente_NoHayAvisoDeCruce()
    {
        var cue = Video(c => { c.Duration = 30.0; c.FadeIn = 20.0; });

        Assert.Equal(FadeIssues.None, FadeSanity.Check(cue, outgoingRemaining: null));
    }

    [Fact]
    public void SinFadeIn_NoHayCruceQueAvisar()
    {
        var cue = Video(c => { c.Duration = 30.0; c.FadeIn = 0.0; });

        Assert.Equal(FadeIssues.None, FadeSanity.Check(cue, outgoingRemaining: 0.5));
    }

    [Fact]
    public void AudioNoTieneCrossfadeDeOpacidad()
    {
        var cue = new Cue
        { Id = 2, Type = CueType.Audio, Duration = 30.0, FadeIn = 20.0 };

        Assert.Equal(FadeIssues.None, FadeSanity.Check(cue, outgoingRemaining: 1.0));
    }

    [Fact]
    public void CuesQueNoSonMedia_NuncaAvisan()
    {
        var cue = new Cue { Id = 3, Type = CueType.Wait, Duration = 2.0, FadeOut = 10.0 };

        Assert.Equal(FadeIssues.None, FadeSanity.Check(cue));
    }

    // ── textos ─────────────────────────────────────────────────────────

    [Fact]
    public void SinAvisos_NoHayTextoQueMostrar()
    {
        Assert.Null(FadeSanity.ShortText(FadeIssues.None));
        Assert.Null(FadeSanity.Explain(FadeIssues.None, 10.0, null));
    }

    [Fact]
    public void ElTooltipDelCruceExplicaElFotogramaCongelado()
    {
        string? text = FadeSanity.Explain(FadeIssues.CrossfadeTooLong, 30.0, 3.0);

        Assert.NotNull(text);
        Assert.Contains("CONGELADO", text, StringComparison.Ordinal);
    }

    [Fact]
    public void VariosAvisosALaVez_SeExplicanTodos()
    {
        var cue = Video(c => { c.Duration = 4.0; c.FadeIn = 9.0; c.FadeOut = 9.0; });

        var issues = FadeSanity.Check(cue, outgoingRemaining: 1.0);

        Assert.True(issues.HasFlag(FadeIssues.FadeInTooLong));
        Assert.True(issues.HasFlag(FadeIssues.FadeOutTooLong));
        Assert.True(issues.HasFlag(FadeIssues.CrossfadeTooLong));
        string? text = FadeSanity.Explain(issues, 4.0, 1.0);
        Assert.NotNull(text);
        Assert.Equal(2, text.Split("\n\n").Length - 1);   // tres párrafos
    }
}
