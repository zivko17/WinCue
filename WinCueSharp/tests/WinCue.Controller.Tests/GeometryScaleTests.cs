// =============================================================================
// GeometryScaleTests.cs — «Escala (%)» del modo FULL: previo y SALIDA a la vez
// =============================================================================
// Reportado en directo: con MODO = «Superficie completa», mover el fader de
// escala cambiaba el previo del inspector pero NO la salida. Causa: «scale» no
// estaba en GeoKeys (la lista de campos que rehacen la geometría en vivo), y
// aunque hubiera estado, el motor leía la escala del snapshot de parámetros
// del GO — nadie la actualizaba. Aquí se fija el contrato del controlador; la
// matemática compartida (previo, mpv y motor HAP) va en la segunda mitad.
// =============================================================================

using WinCue.Core;

namespace WinCue.Controller.Tests;

public class GeometryScaleTests : ControllerTestBase
{
    [Fact]
    public void EscalaEnVivo_ModoFull_LlegaALaSalida()
    {
        var v = AddCue(CueType.Video, "clip", duration: 100.0);
        Controller.OnGoPressed();

        Controller.OnCueEditCommitted(0, new Dictionary<string, object?>
        { ["scale"] = 149.0 });

        var (cueId, geo, scale) = Assert.Single(Video.GeometryApplied);
        Assert.Equal(v.Id, cueId);
        Assert.Equal("FULL", geo.Mode);
        Assert.Equal(149.0, scale);   // el fader manda en la SALIDA, no solo en el previo
    }

    [Fact]
    public void EscalaEnVivo_ModoCustom_TambienViajaConLaGeometria()
    {
        // En CUSTOM el zoom es geo.ScaleX, pero la escala del cue sigue
        // viajando: si el operador vuelve a FULL, la salida ya la tiene.
        var v = AddCue(CueType.Video, "clip", duration: 100.0,
            setup: c => c.GeoMode = GeoMode.Custom);
        Controller.OnGoPressed();

        Controller.OnCueEditCommitted(0, new Dictionary<string, object?>
        { ["scale_x"] = 2.0 });

        var (_, geo, scale) = Assert.Single(Video.GeometryApplied);
        Assert.Equal("CUSTOM", geo.Mode);
        Assert.Equal(2.0, geo.ScaleX);
        Assert.Equal(v.Scale, scale);
    }

    [Fact]
    public void EscalaSinReproducir_NoLlamaAlMotorPeroRePreparaElStandby()
    {
        var v = AddCue(CueType.Video, "clip", duration: 100.0);
        View.VisibleOutputs.Add(0);
        Video.Prepared.Clear();

        Controller.OnCueEditCommitted(0, new Dictionary<string, object?>
        { ["scale"] = 200.0 });

        Assert.Empty(Video.GeometryApplied);           // no hay nada en pantalla
        var prep = Assert.Single(Video.Prepared);      // pero el standby se rehace
        Assert.Equal(v.Id, prep.CueId);
        Assert.Equal(200.0, prep.P.Scale);             // con la escala NUEVA
    }

    // ── Matemática del modo FULL: lo que tienen que decir IGUAL el previo del
    //    inspector (InspectorPanel.ApplyPreviewGeometry), mpv (VideoEngine.
    //    BaseZoom → video-zoom) y el motor HAP (HapCuePlayer.ComputeDest) ────

    /// <summary>video-zoom de mpv y del previo: log2(escala/100).</summary>
    private static double BaseZoom(double scalePercent) =>
        Math.Abs(scalePercent - 100.0) < 0.01
            ? 0.0
            : Math.Log2(Math.Max(1.0, scalePercent) / 100.0);

    /// <summary>Rectángulo destino del motor HAP en modo FULL (ajuste con
    /// aspecto × escala, centrado): copia exacta de HapCuePlayer.ComputeDest.</summary>
    private static (int X, int Y, int W, int H) HapDest(
        int videoW, int videoH, int surfW, int surfH, double scalePercent)
    {
        double zoom = Math.Max(0.01, scalePercent) / 100.0;
        double fit = Math.Min((double)surfW / videoW, (double)surfH / videoH);
        double w = videoW * fit * zoom, h = videoH * fit * zoom;
        return ((int)Math.Round((surfW - w) / 2.0), (int)Math.Round((surfH - h) / 2.0),
            Math.Max(1, (int)Math.Round(w)), Math.Max(1, (int)Math.Round(h)));
    }

    [Theory]
    [InlineData(100.0, 1.0)]
    [InlineData(200.0, 2.0)]
    [InlineData(149.0, 1.49)]
    [InlineData(50.0, 0.5)]
    public void LosDosMotoresEscalanIGUAL(double scalePercent, double expected)
    {
        // mpv: video-zoom es log2 → el multiplicador real es 2^zoom
        Assert.Equal(expected, Math.Pow(2.0, BaseZoom(scalePercent)), 3);

        // HAP: el ancho destino es ancho_ajustado × multiplicador
        var full = HapDest(1920, 1080, 1920, 1080, 100.0);
        var scaled = HapDest(1920, 1080, 1920, 1080, scalePercent);
        Assert.Equal(expected, (double)scaled.W / full.W, 2);
    }

    [Fact]
    public void EscalaFull_MantieneElCentrado()
    {
        // Un zoom del modo FULL crece desde el CENTRO de la superficie
        var d = HapDest(1920, 1080, 1920, 1080, 150.0);
        Assert.Equal(1920, d.X * 2 + d.W);
        Assert.Equal(1080, d.Y * 2 + d.H);
    }

    [Fact]
    public void EscalaFull_RespetaElAspectoAlAjustar()
    {
        // 4:3 sobre una superficie 16:9: barras a los lados, y la escala
        // multiplica ambos lados por igual (el aspecto no se deforma)
        var d100 = HapDest(1440, 1080, 1920, 1080, 100.0);
        var d150 = HapDest(1440, 1080, 1920, 1080, 150.0);
        Assert.Equal(1440.0 / 1080.0, (double)d100.W / d100.H, 2);
        Assert.Equal(1440.0 / 1080.0, (double)d150.W / d150.H, 2);
        Assert.Equal(1.5, (double)d150.H / d100.H, 2);
    }
}
