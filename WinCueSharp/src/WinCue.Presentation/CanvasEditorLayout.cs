// =============================================================================
// CanvasEditorLayout.cs — El calculo del editor de canvas (Ajustes -> Video)
// =============================================================================
// Estilo Watchout/Resolume: un lienzo global y encima los recortes de las
// cuatro salidas, que se arrastran y se redimensionan con imanes. Aqui esta el
// CALCULO; el dibujo lo pone cada producto (WPF en WinCue.App, WinUI 3 en
// Secuence.App), que es la regla 3 del proyecto: primero se extrae la cuenta y
// se prueba, despues se pinta, y los dos usan la misma.
//
// EL DETALLE QUE SE PIERDE AL COPIAR A OJO: el umbral del iman son 10 px de
// PANTALLA, no de canvas. Se divide por la escala, asi que al alejar el zoom
// el iman agarra desde mas lejos en pixeles del lienzo.
//
// El texto "x,y,w,h" solo se FORMATEA aqui: parsearlo ya lo hace
// WinCue.Controller.CanvasLayout.ParseRegion, que es quien lo lee en el motor.
// Dos parsers distintos del mismo formato es como divergen los productos.
// =============================================================================

using System.Globalization;

namespace WinCue.Presentation;

/// <summary>Recorte de una salida sobre el lienzo, en pixeles de CANVAS.</summary>
public readonly record struct CanvasRegion(int X, int Y, int W, int H);

/// <summary>Rectangulo ya en coordenadas de la VISTA (lo que se pinta).</summary>
public readonly record struct ViewRect(double X, double Y, double W, double H);

public static class CanvasEditorLayout
{
    /// <summary>Lado minimo de un recorte, en pixeles de canvas.</summary>
    public const int MinSize = 16;

    /// <summary>Umbral del iman, en pixeles de PANTALLA. Ver la cabecera.</summary>
    public const double SnapScreenPx = 10.0;

    public const int Outputs = 4;

    /// <summary>Colores de identidad de las cuatro salidas. Los comparten el
    /// editor, el overlay de "Identificar" y el menu del boton de salidas —
    /// estaban duplicados en los dos productos.</summary>
    public static IReadOnlyList<uint> OutputColors { get; } = new[]
    {
        Palette.Go,        // S1 verde
        Palette.Info,      // S2 azul
        Palette.Standby,   // S3 ambar
        Palette.Magenta,   // S4 magenta
    };

    public static IReadOnlyList<(int W, int H, string Label)> SizePresets { get; } = new[]
    {
        (1920, 1080, "1920 × 1080 (FHD)"),
        (3840, 1080, "3840 × 1080 (2×FHD horizontal)"),
        (5760, 1080, "5760 × 1080 (3×FHD horizontal)"),
        (1920, 2160, "1920 × 2160 (2×FHD vertical)"),
        (3840, 2160, "3840 × 2160 (4K)"),
    };

    // ------------------------------------------------------------------
    // Encaje del lienzo en la vista
    // ------------------------------------------------------------------

    /// <summary>Pixeles de vista por pixel de canvas. Deja 4 px de respiro
    /// para que el borde del lienzo no se coma el del control.</summary>
    public static double Scale(int cw, int ch, double viewW, double viewH) =>
        Math.Min((viewW - 4) / Math.Max(1, cw), (viewH - 4) / Math.Max(1, ch));

    /// <summary>Esquina superior izquierda del lienzo dentro de la vista: el
    /// lienzo va CENTRADO, sea cual sea su proporcion.</summary>
    public static (double X, double Y) Origin(int cw, int ch, double viewW, double viewH)
    {
        double s = Scale(cw, ch, viewW, viewH);
        return ((viewW - cw * s) / 2.0, (viewH - ch * s) / 2.0);
    }

    public static ViewRect ToView(CanvasRegion r, int cw, int ch, double viewW, double viewH)
    {
        double s = Scale(cw, ch, viewW, viewH);
        var (ox, oy) = Origin(cw, ch, viewW, viewH);
        return new ViewRect(ox + r.X * s, oy + r.Y * s, r.W * s, r.H * s);
    }

    /// <summary>Un desplazamiento del raton (pixeles de vista) en pixeles de
    /// canvas.</summary>
    public static int ToCanvasDelta(double screenDelta, double scale) =>
        (int)Math.Round(screenDelta / (scale <= 0 ? 1 : scale));

    public static CanvasRegion Clamp(CanvasRegion r, int cw, int ch)
    {
        int x = Math.Clamp(r.X, 0, Math.Max(0, cw - MinSize));
        int y = Math.Clamp(r.Y, 0, Math.Max(0, ch - MinSize));
        int w = Math.Clamp(r.W, MinSize, Math.Max(MinSize, cw - x));
        int h = Math.Clamp(r.H, MinSize, Math.Max(MinSize, ch - y));
        return new CanvasRegion(x, y, w, h);
    }

    // ------------------------------------------------------------------
    // Imanes
    // ------------------------------------------------------------------

    public static IReadOnlyList<int> SnapTargetsX(
        IReadOnlyList<CanvasRegion> regions, IReadOnlyList<bool> enabled, int self, int cw)
    {
        ArgumentNullException.ThrowIfNull(regions);
        ArgumentNullException.ThrowIfNull(enabled);
        var t = new List<int> { 0, cw / 2, cw };
        for (int i = 0; i < regions.Count && i < Outputs; i++)
        {
            if (i == self || !IsEnabled(enabled, i))
                continue;
            t.Add(regions[i].X);
            t.Add(regions[i].X + regions[i].W);
        }
        return t;
    }

    public static IReadOnlyList<int> SnapTargetsY(
        IReadOnlyList<CanvasRegion> regions, IReadOnlyList<bool> enabled, int self, int ch)
    {
        ArgumentNullException.ThrowIfNull(regions);
        ArgumentNullException.ThrowIfNull(enabled);
        var t = new List<int> { 0, ch / 2, ch };
        for (int i = 0; i < regions.Count && i < Outputs; i++)
        {
            if (i == self || !IsEnabled(enabled, i))
                continue;
            t.Add(regions[i].Y);
            t.Add(regions[i].Y + regions[i].H);
        }
        return t;
    }

    /// <summary>La Salida 1 SIEMPRE cuenta como habilitada (no se puede
    /// apagar), igual que en el motor.</summary>
    private static bool IsEnabled(IReadOnlyList<bool> enabled, int i) =>
        i == 0 || (i < enabled.Count && enabled[i]);

    private static int SnapThreshold(double scale) =>
        (int)Math.Max(1, SnapScreenPx / (scale <= 0 ? 1 : scale));

    // ------------------------------------------------------------------
    // Mover y redimensionar
    // ------------------------------------------------------------------

    /// <summary>Mueve el recorte <paramref name="orig"/> el desplazamiento
    /// dado (ya en pixeles de canvas), enganchando bordes y aplicando los
    /// topes DESPUES del iman.</summary>
    public static CanvasRegion Move(CanvasRegion orig, int dx, int dy,
        IReadOnlyList<CanvasRegion> regions, IReadOnlyList<bool> enabled,
        int self, int cw, int ch, double scale)
    {
        int x = Math.Clamp(orig.X + dx, 0, Math.Max(0, cw - orig.W));
        int y = Math.Clamp(orig.Y + dy, 0, Math.Max(0, ch - orig.H));
        int snap = SnapThreshold(scale);

        foreach (int t in SnapTargetsX(regions, enabled, self, cw))
        {
            if (Math.Abs(x - t) <= snap) { x = t; break; }
            if (Math.Abs(x + orig.W - t) <= snap) { x = t - orig.W; break; }
        }
        foreach (int t in SnapTargetsY(regions, enabled, self, ch))
        {
            if (Math.Abs(y - t) <= snap) { y = t; break; }
            if (Math.Abs(y + orig.H - t) <= snap) { y = t - orig.H; break; }
        }

        return new CanvasRegion(
            Math.Clamp(x, 0, Math.Max(0, cw - orig.W)),
            Math.Clamp(y, 0, Math.Max(0, ch - orig.H)),
            orig.W, orig.H);
    }

    /// <summary>Redimensiona por la esquina inferior derecha: solo enganchan
    /// el borde derecho y el inferior, y solo si el resultado no baja del
    /// minimo.</summary>
    public static CanvasRegion Resize(CanvasRegion orig, int dx, int dy,
        IReadOnlyList<CanvasRegion> regions, IReadOnlyList<bool> enabled,
        int self, int cw, int ch, double scale)
    {
        int w = Math.Clamp(orig.W + dx, MinSize, Math.Max(MinSize, cw - orig.X));
        int h = Math.Clamp(orig.H + dy, MinSize, Math.Max(MinSize, ch - orig.Y));
        int snap = SnapThreshold(scale);

        foreach (int t in SnapTargetsX(regions, enabled, self, cw))
            if (Math.Abs(orig.X + w - t) <= snap && t - orig.X >= MinSize)
            { w = t - orig.X; break; }
        foreach (int t in SnapTargetsY(regions, enabled, self, ch))
            if (Math.Abs(orig.Y + h - t) <= snap && t - orig.Y >= MinSize)
            { h = t - orig.Y; break; }

        return new CanvasRegion(orig.X, orig.Y,
            Math.Min(w, Math.Max(MinSize, cw - orig.X)),
            Math.Min(h, Math.Max(MinSize, ch - orig.Y)));
    }

    /// <summary>El texto que va a settings. Lo LEE CanvasLayout.ParseRegion.</summary>
    public static string Format(CanvasRegion r) =>
        string.Create(CultureInfo.InvariantCulture, $"{r.X},{r.Y},{r.W},{r.H}");
}
