// =============================================================================
// GeometryLayoutTests.cs — red del cálculo del lienzo de geometría (traslado
// a Win2D).
// =============================================================================
// Fija los valores que GeometryCanvasControl.cs (WinCue.App) tenía HOY: el
// ajuste "contain" del canvas de salida dentro del control, el mapeo de
// coordenadas SALIDA↔LIENZO que de él se deriva, el encaje del vídeo según el
// modo, las esquinas rotadas y su hit-test, y el arrastre (traslación, escala
// e imán). El mapeo de coordenadas es lo que el encargo pide fijar con más
// cuidado: un punto de la SALIDA tiene que caer siempre en el mismo punto del
// LIENZO y viceversa, a distintos tamaños y proporciones de control —
// incluidos los casos raros en que el control es más ancho que 16:9
// (pillarbox, bandas a los lados) y más estrecho (letterbox, bandas arriba y
// abajo).
// =============================================================================

using WinCue.Presentation;

namespace WinCue.Presentation.Tests;

public class GeometryLayoutTests
{
    // ═════════════════════════ CanvasRect (ajuste "contain") ═══════════════

    [Fact]
    public void CanvasRectControlMasAnchoQue16x9DejaBandasALosLados()
    {
        // Control 400×100 (4:1, mucho más ancho que 16:9): el canvas se ajusta
        // por el ALTO y quedan bandas verticales a los lados (pillarbox).
        var canvas = GeometryLayout.CanvasRect(400, 100);

        Assert.True(canvas.Width < 400 - GeometryLayout.CanvasMargin * 2 + 0.01);
        // El alto disponible (100 - 28 = 72) manda: ancho = 72 * 16/9 = 128.
        Assert.Equal(128.0, canvas.Width, precision: 6);
        Assert.Equal(72.0, canvas.Height, precision: 6);
        // Centrado: sobra la misma banda a cada lado.
        Assert.Equal((400 - 128.0) / 2, canvas.Left, precision: 6);
    }

    [Fact]
    public void CanvasRectControlMasEstrechoQue16x9DejaBandasArribaYAbajo()
    {
        // Control 200×400 (mucho más alto que ancho): el canvas se ajusta por
        // el ANCHO y quedan bandas horizontales arriba y abajo (letterbox).
        var canvas = GeometryLayout.CanvasRect(200, 400);

        double expectedW = 200 - GeometryLayout.CanvasMargin * 2;   // 172
        double expectedH = expectedW * 9.0 / 16.0;
        Assert.Equal(expectedW, canvas.Width, precision: 6);
        Assert.Equal(expectedH, canvas.Height, precision: 6);
        Assert.Equal((400 - expectedH) / 2, canvas.Top, precision: 6);
    }

    [Fact]
    public void CanvasRectControlDiminutoRespetaElMinimo()
    {
        // Por debajo del margen, el rect no colapsa a 0 ni a negativo.
        var canvas = GeometryLayout.CanvasRect(10, 10);
        Assert.True(canvas.Width >= 40 || canvas.Height >= 24);
    }

    // ═════════════════════════ Mapeo SALIDA ↔ LIENZO ════════════════════════

    [Theory]
    [InlineData(400, 100)]     // control más ancho que 16:9 (pillarbox)
    [InlineData(200, 400)]     // control más estrecho que 16:9 (letterbox)
    [InlineData(1920, 1080)]   // control exactamente 16:9
    [InlineData(50, 800)]      // caso extremo: casi todo banda
    public void OutputToCanvasYVueltaEsLaIdentidad(double controlW, double controlH)
    {
        var canvas = GeometryLayout.CanvasRect(controlW, controlH);

        // Puntos de interés en la SALIDA: las cuatro esquinas y el centro del
        // canvas 1920×1080 — los mismos que un operador encuadraría contra.
        GeoPoint[] puntosSalida =
        {
            new(0, 0), new(1920, 0), new(1920, 1080), new(0, 1080), new(960, 540),
        };

        foreach (var p in puntosSalida)
        {
            var enLienzo = GeometryLayout.OutputToCanvas(p.X, p.Y, canvas);
            var vuelta = GeometryLayout.CanvasToOutput(enLienzo.X, enLienzo.Y, canvas);
            Assert.Equal(p.X, vuelta.X, precision: 6);
            Assert.Equal(p.Y, vuelta.Y, precision: 6);
        }
    }

    [Fact]
    public void OutputToCanvasElCentroDeLaSalidaCaeEnElCentroDelCanvasDibujado()
    {
        var canvas = GeometryLayout.CanvasRect(400, 100);
        var centro = GeometryLayout.OutputToCanvas(960, 540, canvas);
        Assert.Equal(canvas.Left + canvas.Width / 2, centro.X, precision: 6);
        Assert.Equal(canvas.Top + canvas.Height / 2, centro.Y, precision: 6);
    }

    [Fact]
    public void OutputToCanvasLaEsquinaSuperiorIzquierdaCaeEnLaDelCanvasDibujado()
    {
        var canvas = GeometryLayout.CanvasRect(1024, 600);
        var esquina = GeometryLayout.OutputToCanvas(0, 0, canvas);
        Assert.Equal(canvas.Left, esquina.X, precision: 6);
        Assert.Equal(canvas.Top, esquina.Y, precision: 6);
    }

    [Fact]
    public void CanvasToOutputSinAnchoDaOrigenSinExplotar()
    {
        // Un canvas degenerado (ancho 0) no puede invertir la escala: (0,0) en
        // vez de una división por cero.
        var degenerado = new GeoRect(0, 0, 0, 0);
        var p = GeometryLayout.CanvasToOutput(5, 5, degenerado);
        Assert.Equal(0.0, p.X);
        Assert.Equal(0.0, p.Y);
    }

    // ═════════════════════════ VideoRect (encaje según el modo) ═════════════

    [Fact]
    public void VideoRectFullAlCienPorCienLlenaElAjuste16x9()
    {
        var canvas = new GeoRect(0, 0, 1600, 900);   // ya 16:9 exacto
        var video = GeometryLayout.VideoRect(canvas, custom: false, fullScalePct: 100,
            preserveAspect: true, scaleX: 1.0, tx: 0, ty: 0);

        Assert.Equal(1600.0, video.Width, precision: 6);
        Assert.Equal(900.0, video.Height, precision: 6);
        Assert.Equal(canvas.Center.X, video.Center.X, precision: 6);
        Assert.Equal(canvas.Center.Y, video.Center.Y, precision: 6);
    }

    [Fact]
    public void VideoRectFullSinAspectoEstiraAlCanvasEntero()
    {
        var canvas = new GeoRect(0, 0, 1000, 300);   // NO es 16:9
        var video = GeometryLayout.VideoRect(canvas, custom: false, fullScalePct: 100,
            preserveAspect: false, scaleX: 1.0, tx: 0, ty: 0);

        Assert.Equal(1000.0, video.Width, precision: 6);
        Assert.Equal(300.0, video.Height, precision: 6);
    }

    [Fact]
    public void VideoRectFullEscalaAlPorcentaje()
    {
        var canvas = new GeoRect(0, 0, 1600, 900);
        var video = GeometryLayout.VideoRect(canvas, custom: false, fullScalePct: 50,
            preserveAspect: true, scaleX: 1.0, tx: 0, ty: 0);

        Assert.Equal(800.0, video.Width, precision: 6);
        Assert.Equal(450.0, video.Height, precision: 6);
    }

    [Fact]
    public void VideoRectCustomTraslacionEnConvenioMasYHaciaArriba()
    {
        var canvas = GeometryLayout.CanvasRect(1920, 1080);   // pxPerUnit == 1
        var quieto = GeometryLayout.VideoRect(canvas, custom: true, fullScalePct: 100,
            preserveAspect: true, scaleX: 1.0, tx: 0, ty: 0);
        // ty positivo (convenio "+Y hacia arriba") debe SUBIR el vídeo en
        // pantalla: coordenada Y de pantalla más pequeña.
        var arriba = GeometryLayout.VideoRect(canvas, custom: true, fullScalePct: 100,
            preserveAspect: true, scaleX: 1.0, tx: 0, ty: 100);

        Assert.True(arriba.Top < quieto.Top);
        // tx positivo mueve a la derecha (misma dirección que la pantalla).
        var derecha = GeometryLayout.VideoRect(canvas, custom: true, fullScalePct: 100,
            preserveAspect: true, scaleX: 1.0, tx: 100, ty: 0);
        Assert.True(derecha.Left > quieto.Left);
    }

    [Fact]
    public void VideoRectCustomEscalaMultiplicaElTamano()
    {
        var canvas = new GeoRect(0, 0, 1600, 900);
        var v1 = GeometryLayout.VideoRect(canvas, custom: true, fullScalePct: 100,
            preserveAspect: true, scaleX: 1.0, tx: 0, ty: 0);
        var v2 = GeometryLayout.VideoRect(canvas, custom: true, fullScalePct: 100,
            preserveAspect: true, scaleX: 2.0, tx: 0, ty: 0);

        Assert.Equal(v1.Width * 2, v2.Width, precision: 6);
        Assert.Equal(v1.Height * 2, v2.Height, precision: 6);
    }

    // ═════════════════════════ Esquinas rotadas y tiradores ═════════════════

    [Fact]
    public void HandlePointsSinRotacionSonLasCuatroEsquinasTalCual()
    {
        var v = new GeoRect(10, 20, 100, 50);
        var pts = GeometryLayout.HandlePoints(v, rotationDeg: 0);

        Assert.Equal(new GeoPoint(10, 20), pts[0]);    // TopLeft
        Assert.Equal(new GeoPoint(110, 20), pts[1]);   // TopRight
        Assert.Equal(new GeoPoint(110, 70), pts[2]);   // BottomRight
        Assert.Equal(new GeoPoint(10, 70), pts[3]);    // BottomLeft
    }

    [Fact]
    public void HandlePointsA180GradosInvierteLasEsquinasRespectoAlCentro()
    {
        var v = new GeoRect(0, 0, 100, 50);   // centro (50, 25)
        var pts = GeometryLayout.HandlePoints(v, rotationDeg: 180);

        // TopLeft (0,0) rotado 180° sobre (50,25) cae en (100,50) = BottomRight original.
        Assert.Equal(100.0, pts[0].X, precision: 6);
        Assert.Equal(50.0, pts[0].Y, precision: 6);
    }

    [Fact]
    public void HandlePointsA90GradosRotaEnElSentidoDeLasAgujas()
    {
        // Rect cuadrado centrado en el origen para que la rotación sea fácil
        // de predecir a mano: TopLeft (-10,-10) → (10,-10) a 90°.
        var v = new GeoRect(-10, -10, 20, 20);
        var pts = GeometryLayout.HandlePoints(v, rotationDeg: 90);
        Assert.Equal(10.0, pts[0].X, precision: 6);
        Assert.Equal(-10.0, pts[0].Y, precision: 6);
    }

    [Fact]
    public void HitHandleSoloRespondeEnModoCustom()
    {
        var v = new GeoRect(0, 0, 100, 50);
        Assert.Equal(-1, GeometryLayout.HitHandle(new GeoPoint(0, 0), custom: false, v, rotationDeg: 0));
    }

    [Fact]
    public void HitHandleAciertaLaEsquinaMasCercanaDentroDelRadio()
    {
        var v = new GeoRect(0, 0, 100, 50);
        int hit = GeometryLayout.HitHandle(new GeoPoint(2, 2), custom: true, v, rotationDeg: 0);
        Assert.Equal((int)GeoHandle.TopLeft, hit);
    }

    [Fact]
    public void HitHandleFueraDelRadioNoAcierta()
    {
        var v = new GeoRect(0, 0, 100, 50);
        int hit = GeometryLayout.HitHandle(new GeoPoint(50, 25), custom: true, v, rotationDeg: 0);
        Assert.Equal(-1, hit);
    }

    [Fact]
    public void DistEsLaDistanciaEuclidea()
    {
        Assert.Equal(5.0, GeometryLayout.Dist(new GeoPoint(0, 0), new GeoPoint(3, 4)), precision: 9);
    }

    // ═════════════════════════ Arrastre: traslación y escala ════════════════

    [Fact]
    public void TranslateFromDragConvierteElDeltaDePantallaAUnidadesDeSalida()
    {
        // pxPerUnit = 0.5 → 10 px de pantalla = 20 unidades de salida.
        var (tx, ty) = GeometryLayout.TranslateFromDrag(
            txStart: 0, tyStart: 0, dragStart: new GeoPoint(0, 0), pos: new GeoPoint(10, 10), pxPerUnit: 0.5);

        Assert.Equal(20.0, tx, precision: 6);
        // Y de pantalla crece hacia abajo; ty (convenio "+Y hacia arriba" del
        // cue) crece hacia arriba — de ahí el signo opuesto.
        Assert.Equal(-20.0, ty, precision: 6);
    }

    [Fact]
    public void TranslateFromDragSinEscalaNoMueveNada()
    {
        var (tx, ty) = GeometryLayout.TranslateFromDrag(
            txStart: 5, tyStart: 7, dragStart: new GeoPoint(0, 0), pos: new GeoPoint(50, 50), pxPerUnit: 0);
        Assert.Equal(5.0, tx);
        Assert.Equal(7.0, ty);
    }

    [Fact]
    public void ScaleFromDragEsProporcionalALaDistanciaAlCentro()
    {
        var centro = new GeoPoint(0, 0);
        // Al agarrar, el ratón estaba a 10 px del centro; ahora está a 20:
        // la escala se duplica.
        double s = GeometryLayout.ScaleFromDrag(scaleStart: 1.0, grabDist: 10,
            pos: new GeoPoint(20, 0), center: centro);
        Assert.Equal(2.0, s, precision: 6);
    }

    [Fact]
    public void ScaleFromDragSeAcotaAlRangoPermitido()
    {
        var centro = new GeoPoint(0, 0);
        double sMax = GeometryLayout.ScaleFromDrag(scaleStart: 1.0, grabDist: 1,
            pos: new GeoPoint(1000, 0), center: centro);
        Assert.Equal(GeometryLayout.MaxScale, sMax);

        double sMin = GeometryLayout.ScaleFromDrag(scaleStart: 1.0, grabDist: 1000,
            pos: new GeoPoint(1, 0), center: centro);
        Assert.Equal(GeometryLayout.MinScale, sMin);
    }

    // ═════════════════════════ Imán a bordes/centro ═════════════════════════

    [Fact]
    public void SnapDeltaPegaAlBordeIzquierdoDentroDelUmbral()
    {
        var canvas = new GeoRect(0, 0, 400, 300);
        var video = new GeoRect(5, 100, 100, 50);   // a 5 px del borde izq. (< umbral 8)
        var (dx, dy, snapX, _) = GeometryLayout.SnapDelta(canvas, video, rotationDeg: 0, shiftPressed: false);

        Assert.True(snapX);
        Assert.Equal(-5.0, dx, precision: 6);   // hay que mover -5 para tocar x=0
        Assert.Equal(0.0, dy);
    }

    [Fact]
    public void SnapDeltaFueraDeUmbralNoPega()
    {
        var canvas = new GeoRect(0, 0, 400, 300);
        var video = new GeoRect(50, 100, 100, 50);   // lejos de cualquier borde/centro
        var (dx, dy, snapX, snapY) = GeometryLayout.SnapDelta(canvas, video, rotationDeg: 0, shiftPressed: false);

        Assert.False(snapX);
        Assert.False(snapY);
        Assert.Equal(0.0, dx);
        Assert.Equal(0.0, dy);
    }

    [Fact]
    public void SnapDeltaConShiftNuncaPega()
    {
        var canvas = new GeoRect(0, 0, 400, 300);
        var video = new GeoRect(0, 0, 100, 50);   // exactamente en el borde/esquina
        var (dx, dy, snapX, snapY) = GeometryLayout.SnapDelta(canvas, video, rotationDeg: 0, shiftPressed: true);

        Assert.False(snapX);
        Assert.False(snapY);
        Assert.Equal(0.0, dx);
        Assert.Equal(0.0, dy);
    }

    [Fact]
    public void SnapBoundsIntercambiaAnchoYAltoA90Y270Grados()
    {
        var video = new GeoRect(0, 0, 100, 50);   // centro (50, 25)
        var giro90 = GeometryLayout.SnapBounds(video, rotationDeg: 90);

        Assert.Equal(50.0, giro90.Width, precision: 6);
        Assert.Equal(100.0, giro90.Height, precision: 6);
        Assert.Equal(50.0, giro90.Center.X, precision: 6);
        Assert.Equal(25.0, giro90.Center.Y, precision: 6);
    }

    [Fact]
    public void SnapBoundsNoCambiaA0Y180Grados()
    {
        var video = new GeoRect(0, 0, 100, 50);
        Assert.Equal(video, GeometryLayout.SnapBounds(video, rotationDeg: 0));
        Assert.Equal(video, GeometryLayout.SnapBounds(video, rotationDeg: 180));
    }
}
