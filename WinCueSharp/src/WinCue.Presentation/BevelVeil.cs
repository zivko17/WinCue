// =============================================================================
// BevelVeil.cs — el CÁLCULO del relieve de los controles, sin framework.
// =============================================================================
// Sube a WinCue.Presentation el velo que hace que un botón deje de ser un
// rectángulo plano y se lea como una tecla de mesa: luz arriba, sombra abajo,
// y al pulsar se hunde. Es el mismo lenguaje que ya vive en
// WinCue.App/Theme.cs (Veil, privado) — aquí solo las PARADAS del degradado,
// como datos puros, para que Secuence (WinUI 3 / Win2D) construya su propio
// LinearGradientBrush con los MISMOS números en vez de inventar un relieve
// aproximado. Ver el encargo: "relieve = lo tocas · hundido = lo contiene ·
// plano = lo lees".
//
// El corte en el centro (dos paradas a alfa 0 en 0.45/0.55) es intencional:
// así el brillo NO se degrada hasta la sombra pasando por un gris muerto —
// hay filo arriba, cara neutra en medio y sombra abajo, que es como se
// comporta una tecla real. Con «inverted» la sombra va arriba, que es el
// estado pulsado.
//
// REGLA (como en Palette.cs): aquí no entra ningún tipo de framework — ni
// Color de WPF ni Windows.UI.Color de WinUI. Solo bytes y doubles; cada
// interfaz los convierte en lo que sepa pintar.
// =============================================================================

namespace WinCue.Presentation;

/// <summary>Una parada del degradado vertical del velo: posición [0, 1] de
/// arriba a abajo, y su color con alfa (0..255 cada canal). El canal de color
/// es siempre blanco puro o negro puro — nunca un gris intermedio — porque el
/// velo va ENCIMA del Background del control, no en su lugar.</summary>
public readonly record struct VeilStop(double Offset, byte A, byte R, byte G, byte B);

/// <summary>Cálculo puro del velo de relieve: dadas las opacidades de brillo y
/// sombra, las cuatro paradas del degradado vertical blanco→negro (o
/// negro→blanco si <paramref name="inverted"/>) que va ENCIMA del Background
/// del control. Puerto exacto de Theme.Veil (WinCue.App) — mismo algoritmo,
/// sin System.Windows.Media.</summary>
public static class BevelVeil
{
    /// <summary>Paradas del velo: <paramref name="light"/> es la opacidad del
    /// brillo (arriba si no está invertido) y <paramref name="dark"/> la de la
    /// sombra (abajo si no está invertido). Con <paramref name="inverted"/> la
    /// sombra va arriba y el brillo abajo — el estado pulsado.</summary>
    public static IReadOnlyList<VeilStop> Stops(byte light, byte dark, bool inverted = false)
    {
        byte topAlpha = inverted ? dark : light;
        byte topChannel = (byte)(inverted ? 0 : 255);
        byte bottomAlpha = inverted ? light : dark;
        byte bottomChannel = (byte)(inverted ? 255 : 0);

        return new[]
        {
            new VeilStop(0.0, topAlpha, topChannel, topChannel, topChannel),
            new VeilStop(0.45, 0, topChannel, topChannel, topChannel),
            new VeilStop(0.55, 0, bottomChannel, bottomChannel, bottomChannel),
            new VeilStop(1.0, bottomAlpha, bottomChannel, bottomChannel, bottomChannel),
        };
    }
}

/// <summary>Los juegos concretos de velo que usa HOY WinCue (Theme.cs /
/// App.xaml), para que Secuence parta de los MISMOS números y no de una
/// aproximación. Cuatro velos en total: tres degradados (Face, FacePressed,
/// Edge) y uno plano (Hover, que en WinCue no pasa por Veil()).</summary>
public static class BevelPalette
{
    /// <summary>Cara en reposo: brillo arriba, sombra abajo.
    /// = Theme.BevelFace = Veil(0x2A, 0x30).</summary>
    public static readonly IReadOnlyList<VeilStop> Face = BevelVeil.Stops(0x2A, 0x30);

    /// <summary>Cara pulsada: el degradado invertido es lo que hace que la
    /// tecla parezca hundida (la sombra pasa arriba).
    /// = Theme.BevelFacePressed = Veil(0x30, 0x2A, inverted: true).</summary>
    public static readonly IReadOnlyList<VeilStop> FacePressed =
        BevelVeil.Stops(0x30, 0x2A, inverted: true);

    /// <summary>Canto: filo claro arriba que se apaga hacia abajo. Es lo que
    /// da el grosor; sin él el degradado de la cara se queda en un manchón.
    /// = Theme.BevelEdge = Veil(0x40, 0x58).</summary>
    public static readonly IReadOnlyList<VeilStop> Edge = BevelVeil.Stops(0x40, 0x58);

    /// <summary>Velo de «ratón encima»: blanco UNIFORME (misma opacidad de
    /// arriba a abajo, NO un degradado — por eso no pasa por BevelVeil.Stops),
    /// aclara la cara entera ~8 %.
    /// = Theme.BevelHover = Solid(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)).</summary>
    public const byte HoverAlpha = 0x14;
}
