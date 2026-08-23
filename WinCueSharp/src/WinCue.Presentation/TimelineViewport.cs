// =============================================================================
// TimelineViewport.cs — zoom y scroll del timeline, sin framework.
// =============================================================================
// Segunda pasada del traslado del timeline (la primera fue TimelineLayout:
// proyección y geometría). Aquí vive el ESTADO de la vista — píxeles por
// segundo y desplazamiento horizontal — y las operaciones que lo mueven:
// zoom anclado al puntero (Ctrl+rueda), zoom a ajustar, scroll con la rueda
// y el auto-desplazamiento al seleccionar. Los números son LOS DE HOY, de
// TimelineCanvas (WinCue.App): factor 1.25 por muesca de rueda, 0.6 px por
// unidad de delta, +10 s de holgura al final del scroll, el 30 %/95 % del
// ancho en EnsureVisible… Un cambio aquí se nota en pantalla en los DOS
// productos y es un fallo, no un ajuste.
//
// Por qué es una CLASE con estado y no funciones puras como TimelineLayout:
// pxPerSec y scrollX son un par acoplado (el zoom anclado reescribe los dos a
// la vez) y cada producto tenía los suyos como campos sueltos. Compartir solo
// las fórmulas habría dejado el acoplamiento (el orden de las asignaciones)
// duplicado en cada canvas — la clase se lleva el par y su disciplina juntos.
// =============================================================================

namespace WinCue.Presentation;

/// <summary>Vista del timeline: zoom (px/s) y desplazamiento (px). La
/// mantienen TimelineCanvas (WPF, WinCue.App) y TimelineCanvas (Win2D,
/// Secuence.App), con las MISMAS operaciones.</summary>
public sealed class TimelineViewport
{
    /// <summary>Factor de zoom por muesca de Ctrl+rueda (delta de ±120).</summary>
    public const double WheelZoomFactor = 1.25;

    /// <summary>Píxeles de scroll por unidad de delta de rueda.</summary>
    public const double WheelScrollPxPerDelta = 0.6;

    /// <summary>Píxeles por segundo (zoom). Arranca en el valor de siempre.</summary>
    public double PxPerSec { get; private set; } = 20;

    /// <summary>Desplazamiento horizontal, en píxeles (0 = show al principio).</summary>
    public double ScrollX { get; private set; }

    public double SecToX(double sec) => TimelineLayout.SecToX(sec, PxPerSec, ScrollX);

    public double XToSec(double x) => TimelineLayout.XToSec(x, PxPerSec, ScrollX);

    /// <summary>Zoom ANCLADO: el instante bajo <paramref name="anchorX"/> se
    /// queda bajo ese mismo píxel tras el cambio de escala.</summary>
    public void ZoomAt(double factor, double anchorX)
    {
        double sec = XToSec(anchorX);
        PxPerSec = Math.Clamp(PxPerSec * factor,
            TimelineLayout.MinPxPerSec, TimelineLayout.MaxPxPerSec);
        ScrollX = Math.Max(0, sec * PxPerSec - anchorX);
    }

    /// <summary>Ajusta el zoom para que quepa el show entero (con 2 s de
    /// holgura y un mínimo de 10 s para no reventar con un show vacío) y
    /// vuelve al principio. Es el "fit" automático de la primera pintada.</summary>
    public void Fit(double totalSeconds, double width)
    {
        double total = Math.Max(10, totalSeconds + 2);
        PxPerSec = Math.Clamp((width - 20) / total,
            TimelineLayout.MinPxPerSec, TimelineLayout.MaxPxPerSec);
        ScrollX = 0;
    }

    /// <summary>«Ajustar a la ventana» del usuario: como Fit, pero con un
    /// ancho de reserva (200/900) por si el control aún no está medido.</summary>
    public void ZoomToFit(double totalSeconds, double actualWidth) =>
        Fit(totalSeconds, Math.Max(200, actualWidth > 0 ? actualWidth : 900));

    /// <summary>Scroll con la rueda (sin Ctrl): delta positivo = hacia el
    /// principio. Tope al final del show más 10 s de holgura.</summary>
    public void ScrollByWheel(double delta, double totalSeconds, double width)
    {
        double maxScroll = Math.Max(0, (totalSeconds + 10) * PxPerSec - width);
        ScrollX = Math.Clamp(ScrollX - delta * WheelScrollPxPerDelta, 0, maxScroll);
    }

    /// <summary>La rueda completa: con Ctrl, zoom anclado al puntero; sin él,
    /// scroll. delta en unidades de rueda (±120 por muesca).</summary>
    public void Wheel(double delta, bool ctrl, double anchorX, double totalSeconds, double width)
    {
        if (ctrl)
            ZoomAt(delta > 0 ? WheelZoomFactor : 1 / WheelZoomFactor, anchorX);
        else
            ScrollByWheel(delta, totalSeconds, width);
    }

    /// <summary>Trae un instante a la vista si quedó fuera (o casi: pasado el
    /// 95 % del ancho), dejándolo al 30 % del borde izquierdo. Es el
    /// auto-desplazamiento al seleccionar en la tabla. Devuelve si movió.</summary>
    public bool EnsureVisible(double startSec, double width)
    {
        if (width <= 0)
            return false;
        double x = SecToX(startSec);
        if (x >= 0 && x <= width * 0.95)
            return false;
        ScrollX = Math.Max(0, startSec * PxPerSec - width * 0.3);
        return true;
    }
}
