// =============================================================================
// GeometryLayout.cs — el CÁLCULO del lienzo de geometría de vídeo, sin
// framework.
// =============================================================================
// Traslado a Win2D (ver el encargo, hitos-0-1): entra el mapeo de coordenadas
// entre la SALIDA (el canvas de 1920×1080 que de verdad sale por el proyector)
// y el LIENZO (el rectángulo, más pequeño y de proporción variable, donde el
// control lo dibuja), el encaje del vídeo dentro de ese canvas según el modo
// (FULL/CUSTOM), las cuatro esquinas ya rotadas para los tiradores de escala,
// su hit-test, el arrastre (traslación y escala) y el imán a bordes/centro.
// NO entra el pintado (DrawingContext/CanvasDrawingSession, pinceles, texto de
// la etiqueta) ni la captura de fotograma fijo del visor (ShowPaused/
// TryGrabStillAsync) — eso sigue siendo cosa de cada interfaz.
//
// Los valores son LOS DE HOY: SurfW/SurfH, CanvasMargin, HandleSize, Min/
// MaxScale, SnapThreshold y el propio orden de operaciones (tirador antes que
// arrastre, imán DESPUÉS de mover y sobre el valor ya calculado…) proceden
// literalmente de GeometryCanvasControl (WinCue.App). Un cambio aquí se nota
// en pantalla en los DOS productos y es un fallo, no un ajuste.
//
// OJO CON LAS DOS ESCALAS (misma trampa que ya costó una desincronía real):
// «Escala (%)» del modo FULL (fullScalePct, parámetro de VideoRect) NO vive en
// VideoGeometry — vive en VideoPlayParams.Scale — y hay que pasarla también a
// IVideoEngine.ApplyGeometry por separado (ver su cabecera en
// MediaEngineContracts.cs). Este archivo solo calcula PÍXELES de pantalla; no
// decide a qué campo del motor va cada número — eso es cosa de quien lo llama
// (SyncGeometryUi/Commit en cada inspector).
//
// GeoPoint/GeoRect — por qué no System.Windows.Point/Rect
// —————————————————————————————————————————————————————————————————
// Point y Rect (System.Windows, WPF) no existen en WinUI 3, y Windows.
// Foundation.Point/Rect (WinUI 3) no existen en WPF. Un tipo propio, con un
// solo campo de datos por eje, es lo mismo que TimelineRect ya hace en este
// proyecto: cada interfaz convierte en el borde, no en medio del cálculo.
// =============================================================================

namespace WinCue.Presentation;

/// <summary>Punto neutral (sin framework) en píxeles de pantalla.</summary>
public readonly record struct GeoPoint(double X, double Y);

/// <summary>Rectángulo neutral (sin framework) en píxeles de pantalla.</summary>
public readonly record struct GeoRect(double X, double Y, double Width, double Height)
{
    public double Left => X;
    public double Top => Y;
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public GeoPoint Center => new(X + Width / 2, Y + Height / 2);
}

/// <summary>Qué tirador de esquina se agarra o arrastra (o ninguno).</summary>
public enum GeoHandle { TopLeft = 0, TopRight = 1, BottomRight = 2, BottomLeft = 3 }

/// <summary>Geometría y cálculo puro del lienzo de encuadre de vídeo. Ni un
/// tipo de framework de interfaz — cada producto dibuja estos GeoRect/GeoPoint
/// con lo que sepa (DrawingContext.DrawRectangle, CanvasDrawingSession.
/// DrawRectangle…).</summary>
public static class GeometryLayout
{
    /// <summary>Dimensiones por defecto del canvas de salida. Mismo valor fijo
    /// que SurfW/SurfH tenían en GeometryCanvasControl — el tamaño REAL del
    /// canvas (Settings → Vídeo, ver VideoGeometry.SurfW/SurfH) no entra en
    /// esta primera pasada del lienzo, igual que no entraba antes.</summary>
    public const double DefaultSurfW = 1920.0, DefaultSurfH = 1080.0;

    /// <summary>Margen entre el borde del control y el canvas dibujado.</summary>
    public const double CanvasMargin = 14;

    /// <summary>Lado del tirador de esquina, en píxeles de pantalla.</summary>
    public const double HandleSize = 9;

    /// <summary>Límites de la escala por ratón. Coinciden a propósito con los
    /// del deslizador de «Escala (×)» del inspector: si el ratón pudiera pasar
    /// de ahí, soltar el tirador dejaría el deslizador tope y el valor real
    /// fuera de rango, y los dos controles dirían cosas distintas.</summary>
    public const double MinScale = 0.1, MaxScale = 4.0;

    /// <summary>Umbral del imán a bordes/centro, en píxeles de pantalla.</summary>
    public const double SnapThreshold = 8;

    // ------------------------------------------------------------------
    // Mapeo de coordenadas: SALIDA (canvas 1920×1080) ↔ LIENZO (control)
    // ------------------------------------------------------------------

    /// <summary>Rect del canvas de salida ajustado ("contain") al tamaño del
    /// control, con un margen. Mismo cálculo que CanvasRect() tenía en línea:
    /// letterbox si el control es más ancho que 16:9, pillarbox si es más
    /// estrecho — el canvas SIEMPRE conserva su proporción.</summary>
    public static GeoRect CanvasRect(double actualWidth, double actualHeight,
        double surfW = DefaultSurfW, double surfH = DefaultSurfH, double margin = CanvasMargin)
    {
        double w = Math.Max(40, actualWidth - margin * 2);
        double h = Math.Max(24, actualHeight - margin * 2);
        double s = Math.Min(w / surfW, h / surfH);
        double cw = surfW * s, ch = surfH * s;
        return new GeoRect((actualWidth - cw) / 2, (actualHeight - ch) / 2, cw, ch);
    }

    /// <summary>Escala del canvas dibujado: píxeles de LIENZO por unidad de
    /// SALIDA. Es el mismo factor en X y en Y (CanvasRect conserva el
    /// aspecto), así que basta un número.</summary>
    public static double PxPerUnit(GeoRect canvasRect, double surfW = DefaultSurfW) =>
        canvasRect.Width / surfW;

    /// <summary>Un punto de la SALIDA (origen arriba-izquierda, 0..surfW /
    /// 0..surfH) → su punto en el LIENZO. Inverso exacto de <see
    /// cref="CanvasToOutput"/>.</summary>
    public static GeoPoint OutputToCanvas(double outX, double outY, GeoRect canvasRect,
        double surfW = DefaultSurfW, double surfH = DefaultSurfH)
    {
        double s = PxPerUnit(canvasRect, surfW);
        return new GeoPoint(canvasRect.Left + outX * s, canvasRect.Top + outY * s);
    }

    /// <summary>Un punto del LIENZO → su punto en la SALIDA. Inverso exacto de
    /// <see cref="OutputToCanvas"/>. (0,0) si el canvas no tiene ancho — no
    /// hay escala que invertir.</summary>
    public static GeoPoint CanvasToOutput(double canvasX, double canvasY, GeoRect canvasRect,
        double surfW = DefaultSurfW, double surfH = DefaultSurfH)
    {
        double s = PxPerUnit(canvasRect, surfW);
        if (s <= 0)
            return new GeoPoint(0, 0);
        return new GeoPoint((canvasX - canvasRect.Left) / s, (canvasY - canvasRect.Top) / s);
    }

    // ------------------------------------------------------------------
    // Encaje del vídeo dentro del canvas
    // ------------------------------------------------------------------

    /// <summary>Rect del vídeo dentro del canvas dibujado. FULL ajusta a
    /// 16:9 (o estira si no se preserva el aspecto); CUSTOM parte también de
    /// ese ajuste 16:9 y lo traslada/escala. Mismo cálculo que VideoRect()
    /// tenía en línea, con <paramref name="tx"/>/<paramref name="ty"/> en el
    /// convenio del cue: +Y hacia ARRIBA (de ahí el signo al pasar a píxeles
    /// de pantalla, que crecen hacia abajo).</summary>
    public static GeoRect VideoRect(GeoRect canvas, bool custom, double fullScalePct,
        bool preserveAspect, double scaleX, double tx, double ty, double surfW = DefaultSurfW)
    {
        double scale = custom ? scaleX : fullScalePct / 100.0;
        double vw, vh;
        if (!custom && !preserveAspect)
        {
            vw = canvas.Width; vh = canvas.Height;   // FULL sin aspecto = estirar
        }
        else
        {
            double s = Math.Min(canvas.Width / 16.0, canvas.Height / 9.0);
            vw = 16.0 * s; vh = 9.0 * s;
        }
        vw *= scale; vh *= scale;

        double pxPerUnit = PxPerUnit(canvas, surfW);
        double cx = canvas.Left + canvas.Width / 2 + (custom ? tx * pxPerUnit : 0);
        double cy = canvas.Top + canvas.Height / 2 - (custom ? ty * pxPerUnit : 0);
        return new GeoRect(cx - vw / 2, cy - vh / 2, vw, vh);
    }

    /// <summary>Centro del vídeo en pantalla.</summary>
    public static GeoPoint VideoCenter(GeoRect v) => v.Center;

    /// <summary>Las cuatro esquinas del vídeo, YA ROTADAS, en el orden
    /// TopLeft/TopRight/BottomRight/BottomLeft (ver <see cref="GeoHandle"/>).
    /// Se rota aquí y no en el pintado porque estos puntos se usan también
    /// para acertar con el ratón: si se dibujaran con una transformación pero
    /// se comprobaran sin rotar, con el vídeo a 90° los tiradores se verían en
    /// un sitio y responderían en otro.</summary>
    public static GeoPoint[] HandlePoints(GeoRect v, double rotationDeg)
    {
        var c = VideoCenter(v);
        var corners = new[]
        {
            new GeoPoint(v.Left,  v.Top),
            new GeoPoint(v.Right, v.Top),
            new GeoPoint(v.Right, v.Bottom),
            new GeoPoint(v.Left,  v.Bottom),
        };
        if (rotationDeg == 0)
            return corners;

        double rad = rotationDeg * Math.PI / 180.0;
        double cos = Math.Cos(rad), sin = Math.Sin(rad);
        for (int i = 0; i < corners.Length; i++)
        {
            double dx = corners[i].X - c.X, dy = corners[i].Y - c.Y;
            corners[i] = new GeoPoint(c.X + dx * cos - dy * sin,
                                      c.Y + dx * sin + dy * cos);
        }
        return corners;
    }

    /// <summary>Índice del tirador bajo el punto dado, o -1. Solo tiene
    /// sentido en modo CUSTOM (llamar con <paramref name="custom"/> = false
    /// siempre da -1, igual que HitHandle hacía en línea).</summary>
    public static int HitHandle(GeoPoint p, bool custom, GeoRect video, double rotationDeg,
        double grabRadius = HandleSize)
    {
        if (!custom)
            return -1;
        var pts = HandlePoints(video, rotationDeg);
        for (int i = 0; i < pts.Length; i++)
            if (Math.Abs(p.X - pts[i].X) <= grabRadius && Math.Abs(p.Y - pts[i].Y) <= grabRadius)
                return i;
        return -1;
    }

    public static double Dist(GeoPoint a, GeoPoint b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    // ------------------------------------------------------------------
    // Arrastre: traslación y escala (modo CUSTOM)
    // ------------------------------------------------------------------

    /// <summary>Nuevo (tx, ty) al arrastrar el vídeo, en el convenio del cue
    /// (unidades de SALIDA, +Y hacia arriba) — SIN imán todavía (ver <see
    /// cref="SnapDelta"/>, que se aplica DESPUÉS y sobre el valor ya
    /// calculado). Mismo cálculo que OnMouseMove tenía en línea.</summary>
    public static (double Tx, double Ty) TranslateFromDrag(
        double txStart, double tyStart, GeoPoint dragStart, GeoPoint pos, double pxPerUnit)
    {
        if (pxPerUnit <= 0)
            return (txStart, tyStart);
        double tx = txStart + (pos.X - dragStart.X) / pxPerUnit;
        double ty = tyStart - (pos.Y - dragStart.Y) / pxPerUnit;   // +Y hacia arriba
        return (tx, ty);
    }

    /// <summary>Nueva escala (×) al arrastrar un tirador de esquina: la razón
    /// entre la distancia actual al centro del vídeo y la que había al
    /// agarrarlo, acotada a [<paramref name="minScale"/>, <paramref
    /// name="maxScale"/>]. Escalar respecto al CENTRO (no a la esquina
    /// opuesta) es lo que evita que el vídeo se desplace mientras se
    /// redimensiona.</summary>
    public static double ScaleFromDrag(double scaleStart, double grabDist, GeoPoint pos, GeoPoint center,
        double minScale = MinScale, double maxScale = MaxScale)
    {
        double factor = Dist(pos, center) / grabDist;
        return Math.Clamp(scaleStart * factor, minScale, maxScale);
    }

    /// <summary>Contorno del vídeo ALINEADO CON LOS EJES, para el imán. A 90°
    /// y 270° el rectángulo ocupa el ancho por el alto y al revés; sin esto el
    /// imán pegaría por un borde que no es el que se ve.</summary>
    public static GeoRect SnapBounds(GeoRect video, double rotationDeg)
    {
        if (rotationDeg != 90 && rotationDeg != 270)
            return video;
        var c = VideoCenter(video);
        double w = video.Height, h = video.Width;
        return new GeoRect(c.X - w / 2, c.Y - h / 2, w, h);
    }

    /// <summary>Pega el vídeo a los bordes y al centro del canvas si anda
    /// cerca. Devuelve el desplazamiento a aplicar, en píxeles de pantalla.
    ///
    /// Con Shift NO imanta (<paramref name="shiftPressed"/> = true): encuadrar
    /// a dos píxeles del borde es una petición legítima, y un imán sin escape
    /// la vuelve imposible.</summary>
    public static (double Dx, double Dy, bool SnapX, bool SnapY) SnapDelta(
        GeoRect canvas, GeoRect video, double rotationDeg, bool shiftPressed,
        double threshold = SnapThreshold)
    {
        if (shiftPressed)
            return (0, 0, false, false);

        var v = SnapBounds(video, rotationDeg);

        double bestX = 0, bestY = 0;
        bool gotX = false, gotY = false;

        void TryX(double delta)
        {
            if (Math.Abs(delta) <= threshold && (!gotX || Math.Abs(delta) < Math.Abs(bestX)))
            {
                bestX = delta;
                gotX = true;
            }
        }
        void TryY(double delta)
        {
            if (Math.Abs(delta) <= threshold && (!gotY || Math.Abs(delta) < Math.Abs(bestY)))
            {
                bestY = delta;
                gotY = true;
            }
        }

        TryX(canvas.Left - v.Left);                                       // borde izquierdo
        TryX(canvas.Right - v.Right);                                     // borde derecho
        TryX((canvas.Left + canvas.Width / 2) - (v.Left + v.Width / 2));   // centrado
        TryY(canvas.Top - v.Top);
        TryY(canvas.Bottom - v.Bottom);
        TryY((canvas.Top + canvas.Height / 2) - (v.Top + v.Height / 2));

        return (gotX ? bestX : 0, gotY ? bestY : 0, gotX, gotY);
    }
}
