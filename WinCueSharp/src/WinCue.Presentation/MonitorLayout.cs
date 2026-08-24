// =============================================================================
// MonitorLayout.cs — el alto de los monitores PGM/PVW, sin framework.
// =============================================================================
// Los mini-monitores de PROGRAMA y AUDICIÓN son elásticos: su alto sale de su
// propio ANCHO para que el hueco del vídeo quede en 16:9. Al ensanchar el
// panel izquierdo (separador arrastrable) crecen con él sin deformar la
// imagen.
//
// El 16:9 es del HUECO DEL VÍDEO, no del control: hay que descontar el borde
// y la barra del distintivo (PGM/PVW). Es justo el detalle que ya se pagó una
// vez en WinCue — antes se sumaba un 18 fijo por la barra y el borde no se
// contaba en absoluto, así que el hueco no era 16:9 y la imagen se salía un
// poco del marco: invisible a 280 px de ancho, evidente cuando el visor del
// inspector pasó a ocupar el alto del panel.
//
// Sube aquí porque los DOS productos lo necesitan: WinCue.App lo tenía en
// línea dentro de LiveMonitorControl.SizeChanged, y Secuence.App no lo tenía
// en absoluto (sus dos cajas llevaban un Height fijo en el XAML, así que la
// caja negra no guardaba proporción ninguna). Repetir la fórmula en el
// segundo producto era exactamente la copia que la regla 2 prohíbe.
// =============================================================================

namespace WinCue.Presentation;

/// <summary>Geometría de los mini-monitores PGM/PVW — ÚNICA definición, la
/// comparten WinCue.App (WPF) y Secuence.App (WinUI 3).</summary>
public static class MonitorLayout
{
    /// <summary>Proporción del hueco de vídeo: 16:9.</summary>
    public const double AspectWidth = 16.0;
    public const double AspectHeight = 9.0;

    /// <summary>Alto mínimo del control, en píxeles. Por debajo, el monitor
    /// deja de decir nada útil aunque el panel se estreche.</summary>
    public const double MinHeight = 120;

    /// <summary>Alto de reserva de la barra del distintivo cuando todavía no
    /// se ha medido (primer layout). Se prefiere MEDIRLA: darla por supuesta
    /// fue el fallo original en WinCue.</summary>
    public const double DefaultBarHeight = 18.0;

    /// <summary>Alto que debe tener el control para que su hueco de vídeo
    /// quede en 16:9.</summary>
    /// <param name="outerWidth">Ancho EXTERIOR del control (borde incluido).</param>
    /// <param name="horizontalBorder">Borde izquierdo + derecho.</param>
    /// <param name="chromeHeight">Todo lo que en vertical NO es vídeo: borde
    /// superior + inferior + el alto MEDIDO de la barra del distintivo.</param>
    public static double HeightForWidth(
        double outerWidth, double horizontalBorder, double chromeHeight)
    {
        double innerWidth = Math.Max(0.0, outerWidth - horizontalBorder);
        return Math.Max(MinHeight,
            innerWidth * AspectHeight / AspectWidth + chromeHeight);
    }
}
