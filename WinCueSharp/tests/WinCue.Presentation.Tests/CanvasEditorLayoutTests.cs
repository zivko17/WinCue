// =============================================================================
// CanvasEditorLayoutTests.cs — El calculo del editor de canvas
// =============================================================================
// Lo que estas pruebas defienden es que los editores de los DOS productos no
// puedan divergir: el de WPF y el de WinUI 3 preguntan aqui.
// =============================================================================

using WinCue.Presentation;

namespace WinCue.Presentation.Tests;

public class CanvasEditorLayoutTests
{
    private static IReadOnlyList<CanvasRegion> Cuatro(params CanvasRegion[] r)
    {
        var list = new List<CanvasRegion>(r);
        while (list.Count < 4)
            list.Add(new CanvasRegion(0, 0, 1920, 1080));
        return list;
    }

    private static IReadOnlyList<bool> Habilitadas(params bool[] e)
    {
        var list = new List<bool> { true };
        list.AddRange(e);
        while (list.Count < 4)
            list.Add(false);
        return list;
    }

    // ── Encaje del lienzo en la vista ──────────────────────────────────

    [Fact]
    public void Scale_LienzoApaisado_EncajaPorElAncho()
    {
        // 5760x1080 en una vista de 480x270: manda el ancho.
        double s = CanvasEditorLayout.Scale(5760, 1080, 480, 270);
        Assert.Equal((480 - 4) / 5760.0, s, 6);
    }

    [Fact]
    public void Scale_LienzoVertical_EncajaPorElAlto()
    {
        // 1920x2160 en 480x270: manda el alto.
        double s = CanvasEditorLayout.Scale(1920, 2160, 480, 270);
        Assert.Equal((270 - 4) / 2160.0, s, 6);
    }

    [Fact]
    public void Origin_CentraElLienzoEnLaVista()
    {
        var (x, y) = CanvasEditorLayout.Origin(1920, 1080, 480, 270);
        double s = CanvasEditorLayout.Scale(1920, 1080, 480, 270);
        Assert.Equal((480 - 1920 * s) / 2.0, x, 6);
        Assert.Equal((270 - 1080 * s) / 2.0, y, 6);
    }

    [Fact]
    public void ToView_RegionCompleta_EsElLienzoEntero()
    {
        var r = CanvasEditorLayout.ToView(
            new CanvasRegion(0, 0, 1920, 1080), 1920, 1080, 480, 270);
        var (ox, oy) = CanvasEditorLayout.Origin(1920, 1080, 480, 270);
        double s = CanvasEditorLayout.Scale(1920, 1080, 480, 270);
        Assert.Equal(ox, r.X, 6);
        Assert.Equal(oy, r.Y, 6);
        Assert.Equal(1920 * s, r.W, 6);
        Assert.Equal(1080 * s, r.H, 6);
    }

    // ── El iman mide en pixeles de PANTALLA ────────────────────────────

    [Fact]
    public void Move_ElUmbralDelIman_SeMideEnPixelesDePantalla()
    {
        // Lienzo enorme (5760 de ancho) en una vista pequena: la escala es
        // ~0.0826, asi que 10 px de pantalla son ~121 px de canvas. Un borde a
        // 100 px del centro del lienzo TIENE que engancharse.
        var regiones = Cuatro(new CanvasRegion(0, 0, 1920, 1080));
        double s = CanvasEditorLayout.Scale(5760, 1080, 480, 270);

        var movida = CanvasEditorLayout.Move(
            new CanvasRegion(2780, 0, 1920, 1080), 0, 0,
            regiones, Habilitadas(), self: 1, cw: 5760, ch: 1080, scale: s);

        Assert.Equal(2880, movida.X);   // enganchado a la mitad del lienzo
    }

    [Fact]
    public void Move_LejosDelIman_NoSeMueveSolo()
    {
        var regiones = Cuatro(new CanvasRegion(0, 0, 1920, 1080));
        double s = CanvasEditorLayout.Scale(1920, 1080, 480, 270);

        // OJO con elegir la region: con escala 0.2463 el umbral son 40 px de
        // canvas, y una region en y=200 con alto 300 tiene el borde inferior
        // en 500 — a exactamente 40 de la mitad del lienzo (540), asi que SI
        // se engancharia. Aqui se elige y=150 a proposito.
        var movida = CanvasEditorLayout.Move(
            new CanvasRegion(500, 150, 400, 300), 0, 0,
            regiones, Habilitadas(), self: 1, cw: 1920, ch: 1080, scale: s);

        Assert.Equal(500, movida.X);
        Assert.Equal(150, movida.Y);
    }

    [Fact]
    public void Move_ElBordeDERECHO_TambienEngancha()
    {
        // Region cuyo borde derecho cae casi en el borde del lienzo.
        var regiones = Cuatro(new CanvasRegion(0, 0, 1920, 1080));
        double s = CanvasEditorLayout.Scale(1920, 1080, 480, 270);

        var movida = CanvasEditorLayout.Move(
            new CanvasRegion(1500, 0, 400, 1080), 0, 0,
            regiones, Habilitadas(), self: 1, cw: 1920, ch: 1080, scale: s);

        Assert.Equal(1520, movida.X);   // x + w == 1920
    }

    [Fact]
    public void Move_SeEnganchaAlBordeDeOtraSalidaHabilitada()
    {
        // El ancho de S2 importa: con 970 el tope (cw - w = 950) volveria a
        // dejarla en 950 DESPUES del iman, y la prueba mediria el tope en vez
        // del iman. Con 800 el tope es 1120 y no estorba.
        var regiones = Cuatro(
            new CanvasRegion(0, 0, 960, 1080),       // S1: mitad izquierda
            new CanvasRegion(950, 0, 800, 1080));    // S2: casi pegada
        double s = CanvasEditorLayout.Scale(1920, 1080, 480, 270);

        var movida = CanvasEditorLayout.Move(
            regiones[1], 0, 0, regiones, Habilitadas(true),
            self: 1, cw: 1920, ch: 1080, scale: s);

        Assert.Equal(960, movida.X);   // pegada al borde derecho de S1
    }

    [Fact]
    public void Move_IgnoraLosBordesDeUnaSalidaDESHABILITADA()
    {
        // Lienzo de 2000 a proposito: asi la mitad (1000) NO coincide con el
        // borde de la salida apagada (950), y la prueba mide lo que dice medir
        // en vez de engancharse a la mitad del lienzo por casualidad.
        var regiones = Cuatro(
            new CanvasRegion(0, 0, 2000, 1080),
            new CanvasRegion(0, 0, 2000, 1080),
            new CanvasRegion(950, 0, 900, 1080));    // S3, apagada
        double s = CanvasEditorLayout.Scale(2000, 1080, 480, 270);

        var movida = CanvasEditorLayout.Move(
            new CanvasRegion(940, 0, 400, 1080), 0, 0,
            regiones, Habilitadas(false, false),
            self: 1, cw: 2000, ch: 1080, scale: s);

        Assert.Equal(940, movida.X);   // no se ha movido: nadie tira de el
    }

    // ── Topes ──────────────────────────────────────────────────────────

    [Fact]
    public void Move_NuncaSaleDelLienzo()
    {
        var regiones = Cuatro();
        double s = CanvasEditorLayout.Scale(1920, 1080, 480, 270);

        var movida = CanvasEditorLayout.Move(
            new CanvasRegion(1000, 500, 400, 300), 5000, 5000,
            regiones, Habilitadas(), self: 1, cw: 1920, ch: 1080, scale: s);

        Assert.Equal(1920 - 400, movida.X);
        Assert.Equal(1080 - 300, movida.Y);
    }

    [Fact]
    public void Resize_NoBajaDelMinimo()
    {
        var regiones = Cuatro();
        double s = CanvasEditorLayout.Scale(1920, 1080, 480, 270);

        var r = CanvasEditorLayout.Resize(
            new CanvasRegion(0, 0, 400, 300), -5000, -5000,
            regiones, Habilitadas(), self: 1, cw: 1920, ch: 1080, scale: s);

        Assert.Equal(CanvasEditorLayout.MinSize, r.W);
        Assert.Equal(CanvasEditorLayout.MinSize, r.H);
    }

    [Fact]
    public void Resize_NoDesbordaElLienzo()
    {
        var regiones = Cuatro();
        double s = CanvasEditorLayout.Scale(1920, 1080, 480, 270);

        var r = CanvasEditorLayout.Resize(
            new CanvasRegion(1000, 500, 400, 300), 5000, 5000,
            regiones, Habilitadas(), self: 1, cw: 1920, ch: 1080, scale: s);

        Assert.Equal(1920 - 1000, r.W);
        Assert.Equal(1080 - 500, r.H);
    }

    [Fact]
    public void Clamp_RegionFueraDeSitio_VuelveDentro()
    {
        var r = CanvasEditorLayout.Clamp(
            new CanvasRegion(-50, -50, 5000, 5000), 1920, 1080);

        Assert.Equal(0, r.X);
        Assert.Equal(0, r.Y);
        Assert.Equal(1920, r.W);
        Assert.Equal(1080, r.H);
    }

    // ── Texto: ida y vuelta con el parser que YA existe ────────────────

    [Fact]
    public void Format_YParse_DanLaVueltaCompleta()
    {
        var original = new CanvasRegion(120, 40, 800, 600);
        string texto = CanvasEditorLayout.Format(original);

        Assert.Equal("120,40,800,600", texto);

        var vuelta = WinCue.Controller.CanvasLayout.ParseRegion(texto, 0, 1920, 1080);
        Assert.Equal(original.X, vuelta.X);
        Assert.Equal(original.Y, vuelta.Y);
        Assert.Equal(original.W, vuelta.W);
        Assert.Equal(original.H, vuelta.H);
    }

    // ── Colores y presets ──────────────────────────────────────────────

    [Fact]
    public void OutputColors_SonLosCuatroDeLaPaleta_EnOrden()
    {
        Assert.Equal(
            new uint[] { Palette.Go, Palette.Info, Palette.Standby, Palette.Magenta },
            CanvasEditorLayout.OutputColors);
    }

    [Fact]
    public void SizePresets_TraenLosCincoDeWinCue()
    {
        var tamanos = CanvasEditorLayout.SizePresets
            .Select(p => (p.W, p.H)).ToArray();

        Assert.Equal(
            new[] { (1920, 1080), (3840, 1080), (5760, 1080), (1920, 2160), (3840, 2160) },
            tamanos);
    }
}
