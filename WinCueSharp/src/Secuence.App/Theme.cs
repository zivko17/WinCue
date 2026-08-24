// =============================================================================
// Theme.cs — la paleta de WinCue.Presentation.Palette, convertida a WinUI 3.
// =============================================================================
// Equivalente de src/WinCue.App/Theme.cs pero para este framework: los 94
// NÚMEROS siguen viviendo en WinCue.Presentation.Palette (sin WPF ni WinUI, así
// los comparten los dos productos); este fichero SOLO los convierte en
// Windows.UI.Color y en Brush de Microsoft.UI.Xaml — el mismo reparto de
// responsabilidad, con el tipo de la otra interfaz.
//
// No se traen aquí los 94: solo los que esta primera versión de la tabla de
// cues necesita. Añadir el resto es mecánico (Hex(Palette.Nombre)) el día que
// haga falta un panel más — no hay que inventar nada nuevo.
// =============================================================================

using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;
using WinCue.Core;
using WinCue.Presentation;

namespace Secuence.App;

public static class Theme
{
    // ── Fondos ──────────────────────────────────────────────────────────
    public static readonly Color WindowBg = Hex(Palette.WindowBg);
    public static readonly Color PanelBg = Hex(Palette.PanelBg);
    public static readonly Color HeaderBg = Hex(Palette.HeaderBg);
    public static readonly Color ControlBg = Hex(Palette.ControlBg);
    // Sumados para la reorganización del hueco (MainWindow.xaml, secuence-
    // shell-report.md): caja del reloj del show (DeepBg, igual que WinCue),
    // pestaña NO seleccionada de Timeline/Inspector/Mixer (DeeperBg) y
    // tarjeta de «en reproducción» (CardBg) — mismos tokens que WinCue.App
    // para los mismos elementos.
    public static readonly Color DeepBg = Hex(Palette.DeepBg);
    public static readonly Color DeeperBg = Hex(Palette.DeeperBg);
    public static readonly Color CardBg = Hex(Palette.CardBg);

    // ── Texto ───────────────────────────────────────────────────────────
    public static readonly Color TextPrimary = Hex(Palette.TextPrimary);
    public static readonly Color TextTertiary = Hex(Palette.TextTertiary);
    public static readonly Color TextDim = Hex(Palette.TextDim);
    // Sumado para el mixer (MixerPanel.cs, hitos-0-1): el gris de la
    // botonera en reposo y de la información secundaria (dispositivo,
    // tooltips) — un tono más apagado que TextDim.
    public static readonly Color TextDimmer = Hex(Palette.TextDimmer);
    public static readonly Color TextFaint = Hex(Palette.TextFaint);
    public static readonly Color TextOnAccent = Hex(Palette.TextOnAccent);

    // ── Bordes ──────────────────────────────────────────────────────────
    public static readonly Color Border = Hex(Palette.Border);
    public static readonly Color Divider = Hex(Palette.Divider);

    // ── Controles / mixer (sumado para MixerPanel.cs, hitos-0-1) ───────
    public static readonly Color ButtonBg = Hex(Palette.ButtonBg);
    public static readonly Color SunkenBg = Hex(Palette.SunkenBg);
    public static readonly Color ToolbarBg = Hex(Palette.ToolbarBg);
    public static readonly Color InfoAlt = Hex(Palette.InfoAlt);
    public static readonly Color MagentaAlt = Hex(Palette.MagentaAlt);

    // ── Acentos de estado (reutilizados en la columna de duración) ─────
    public static readonly Color Go = Hex(Palette.Go);
    public static readonly Color GoButton = Hex(Palette.GoButton);
    // Sumado para el borde/etiqueta del previo PVW (MainWindow.xaml): mismo
    // token que WinCue.App._pvw = new LiveMonitorControl("PVW", Theme.GoDeep).
    public static readonly Color GoDeep = Hex(Palette.GoDeep);
    public static readonly Color Pgm = Hex(Palette.Pgm);
    public static readonly Color PanicButton = Hex(Palette.PanicButton);
    public static readonly Color Standby = Hex(Palette.Standby);
    public static readonly Color Warn = Hex(Palette.Warn);
    // Sumado para el editor de EQ (InspectorPanel.cs, hitos-0-1): el punto
    // de color de la 4ª banda paramétrica (agudos).
    public static readonly Color Blue = Hex(Palette.Blue);
    // Sumado para el interruptor «Modo: EDIT» de la barra de transporte
    // (deshabilitado en esta pasada, ver el informe): mismo token que
    // WinCue.App.MainWindow.xaml, ModeToggle Background="{DynamicResource Gold}".
    public static readonly Color Gold = Hex(Palette.Gold);

    // ── Fila del playhead (tabla de cues) ──────────────────────────────
    public static readonly Color PlayheadRow = Hex(Palette.PlayheadRow);

    // ── Relieve de los botones (velo de relieve) ───────────────────────
    // Mismo lenguaje que WinCue.App.Theme (ver su sección "RELIEVE DE LOS
    // BOTONES"): un LinearGradientBrush translúcido va ENCIMA del Background
    // del botón — «relieve = lo tocas · hundido = lo contiene · plano = lo
    // lees» (ver el encargo). Las PARADAS salen de
    // WinCue.Presentation.BevelPalette (el mismo cálculo que usa WinCue.App);
    // aquí solo se convierten a Microsoft.UI.Xaml.Media.
    //
    // MainWindow.xaml NO los referencia con {StaticResource}: el compilador
    // de XAML de WinUI 3 resuelve StaticResource EN TIEMPO DE COMPILACIÓN, así
    // que no puede ver un recurso que solo existiría porque C# lo mete en el
    // diccionario en caliente (eso SÍ vale en WPF con DynamicResource, no
    // aquí — ver Window.Resources en MainWindow.xaml para el porqué exacto).
    // Por eso MainWindow.xaml declara los mismos cuatro velos como recursos
    // LITERALES, y estos brushes de aquí quedan para MainWindow.VerifyBevelResources()
    // — la red de seguridad que compara los dos juegos byte a byte al
    // arrancar, para que una deriva entre BevelPalette y el XAML reviente con
    // un mensaje claro en vez de quedarse callada.

    /// <summary>Cara en reposo: brillo arriba, sombra abajo.
    /// = WinCue.App.Theme.BevelFace.</summary>
    public static readonly LinearGradientBrush BevelFaceBrush = VeilBrush(BevelPalette.Face);
    /// <summary>Cara pulsada: el degradado invertido es lo que hace que la
    /// tecla parezca hundida. = WinCue.App.Theme.BevelFacePressed.</summary>
    public static readonly LinearGradientBrush BevelFacePressedBrush = VeilBrush(BevelPalette.FacePressed);
    /// <summary>Canto: filo claro arriba que se apaga hacia abajo.
    /// = WinCue.App.Theme.BevelEdge.</summary>
    public static readonly LinearGradientBrush BevelEdgeBrush = VeilBrush(BevelPalette.Edge);
    /// <summary>Velo de «ratón encima»: blanco uniforme (NO degradado), aclara
    /// la cara entera ~8 %. = WinCue.App.Theme.BevelHover.</summary>
    public static readonly SolidColorBrush BevelHoverBrush =
        Solid(Color.FromArgb(BevelPalette.HoverAlpha, 0xFF, 0xFF, 0xFF));

    // ── Tipos de cue ────────────────────────────────────────────────────
    public static readonly Color TypeAudio = Hex(Palette.TypeAudio);
    public static readonly Color TypeVideo = Hex(Palette.TypeVideo);
    public static readonly Color TypeWait = Hex(Palette.TypeWait);
    public static readonly Color TypeFade = Hex(Palette.TypeFade);
    public static readonly Color TypeStart = Hex(Palette.TypeStart);
    public static readonly Color TypeStop = Hex(Palette.TypeStop);
    public static readonly Color TypeScript = Hex(Palette.TypeScript);
    public static readonly Color TypeMemo = Hex(Palette.TypeMemo);
    public static readonly Color TypeGroup = Hex(Palette.TypeGroup);

    /// <summary>Color de un tipo de cue. Mismo reparto que Theme.ForCueType en
    /// WPF: la columna «Tipo» de la tabla manda, el resto de la interfaz de
    /// Secuence lo reflejará desde aquí cuando exista.</summary>
    public static Color ForCueType(CueType type) => type switch
    {
        CueType.Audio => TypeAudio,
        CueType.Video => TypeVideo,
        CueType.Wait => TypeWait,
        CueType.Fade => TypeFade,
        CueType.Start => TypeStart,
        CueType.Stop => TypeStop,
        CueType.Script => TypeScript,
        CueType.Memo => TypeMemo,
        CueType.Group => TypeGroup,
        _ => TextPrimary,
    };

    // ── Pinceles precalculados de fondo/texto/borde ────────────────────
    public static readonly SolidColorBrush WindowBgBrush = Solid(WindowBg);
    public static readonly SolidColorBrush PanelBgBrush = Solid(PanelBg);
    public static readonly SolidColorBrush HeaderBgBrush = Solid(HeaderBg);
    public static readonly SolidColorBrush ControlBgBrush = Solid(ControlBg);
    public static readonly SolidColorBrush DeepBgBrush = Solid(DeepBg);
    public static readonly SolidColorBrush DeeperBgBrush = Solid(DeeperBg);
    public static readonly SolidColorBrush CardBgBrush = Solid(CardBg);
    public static readonly SolidColorBrush TextPrimaryBrush = Solid(TextPrimary);
    public static readonly SolidColorBrush TextTertiaryBrush = Solid(TextTertiary);
    public static readonly SolidColorBrush TextDimBrush = Solid(TextDim);
    public static readonly SolidColorBrush TextDimmerBrush = Solid(TextDimmer);
    public static readonly SolidColorBrush TextFaintBrush = Solid(TextFaint);
    public static readonly SolidColorBrush TextOnAccentBrush = Solid(TextOnAccent);
    public static readonly SolidColorBrush BorderBrush = Solid(Border);
    public static readonly SolidColorBrush DividerBrush = Solid(Divider);
    public static readonly SolidColorBrush ButtonBgBrush = Solid(ButtonBg);
    public static readonly SolidColorBrush SunkenBgBrush = Solid(SunkenBg);
    public static readonly SolidColorBrush ToolbarBgBrush = Solid(ToolbarBg);
    public static readonly SolidColorBrush InfoAltBrush = Solid(InfoAlt);
    public static readonly SolidColorBrush MagentaAltBrush = Solid(MagentaAlt);
    public static readonly SolidColorBrush GoBrush = Solid(Go);
    public static readonly SolidColorBrush GoButtonBrush = Solid(GoButton);
    public static readonly SolidColorBrush GoDeepBrush = Solid(GoDeep);
    public static readonly SolidColorBrush PgmBrush = Solid(Pgm);
    public static readonly SolidColorBrush PanicButtonBrush = Solid(PanicButton);
    public static readonly SolidColorBrush StandbyBrush = Solid(Standby);
    public static readonly SolidColorBrush WarnBrush = Solid(Warn);
    public static readonly SolidColorBrush BlueBrush = Solid(Blue);
    public static readonly SolidColorBrush GoldBrush = Solid(Gold);
    public static readonly SolidColorBrush PlayheadRowBrush = Solid(PlayheadRow);
    public static readonly SolidColorBrush TransparentBrush = Solid(Colors.Transparent);

    /// <summary>Pincel por tipo de cue, precalculado (mismo motivo que en WPF:
    /// evitar un SolidColorBrush nuevo por celda repintada).</summary>
    private static readonly Dictionary<CueType, SolidColorBrush> CueTypeBrushes =
        Enum.GetValues<CueType>().ToDictionary(t => t, t => Solid(ForCueType(t)));

    public static SolidColorBrush BrushForCueType(CueType type) =>
        CueTypeBrushes.TryGetValue(type, out var b) ? b : Solid(TextPrimary);

    /// <summary>Pincel por color de etiqueta (paleta de nombres de
    /// WinCue.Core.CueConstants.CueColors: "red", "orange"… "" = sin color).</summary>
    public static SolidColorBrush BrushForLabelName(string colorName) =>
        colorName.Length > 0
        && CueConstants.CueColors.TryGetValue(colorName, out var hex)
        && hex.Length > 0
            ? Solid(HexArgb(hex))
            : TransparentBrush;

    /// <summary>Color del punto de estado (mismos hex que
    /// WinCue.Core.CueConstants.StateColors, sin equivalente en Palette porque
    /// no son parte del vocabulario visual compartido, son datos de Core).</summary>
    public static SolidColorBrush BrushForState(CueState state) =>
        CueConstants.StateColors.TryGetValue(state, out var hex)
            ? Solid(HexArgb(hex))
            : TextDimBrush;

    // ── Herramientas ────────────────────────────────────────────────────

    /// <summary>0xRRGGBB (de Palette) → Color opaco.
    ///
    /// Pública (no solo de uso interno de este fichero): el inspector la usa
    /// para las muestras de color de cue, cuyos tokens uint vienen ya
    /// resueltos de WinCue.Presentation.CueChoices — el cálculo compartido con
    /// WinCue— sin duplicar aquí la misma cuenta de bits. Mismo motivo por el
    /// que WinCue.App.Theme.Hex también es pública.</summary>
    public static Color Hex(uint rgb) => Color.FromArgb(
        0xff, (byte)((rgb >> 16) & 0xff), (byte)((rgb >> 8) & 0xff), (byte)(rgb & 0xff));

    /// <summary>"#RRGGBB" (de WinCue.Core.CueConstants) → Color opaco.</summary>
    private static Color HexArgb(string hex)
    {
        string h = hex.TrimStart('#');
        byte r = Convert.ToByte(h[..2], 16);
        byte g = Convert.ToByte(h[2..4], 16);
        byte b = Convert.ToByte(h[4..6], 16);
        return Color.FromArgb(0xff, r, g, b);
    }

    public static SolidColorBrush Solid(Color c) => new(c);

    /// <summary>Convierte las paradas de WinCue.Presentation.BevelVeil en un
    /// LinearGradientBrush vertical de WinUI 3 — mismo mecanismo que
    /// WinCue.App.Theme.VeilBrush (WPF), con los tipos de este framework
    /// (Windows.Foundation.Point en vez de System.Windows.Point; sin Freeze,
    /// que en WinUI 3 no existe — los Brush no son Freezable aquí).</summary>
    private static LinearGradientBrush VeilBrush(IReadOnlyList<VeilStop> stops)
    {
        var b = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        foreach (var s in stops)
            b.GradientStops.Add(new GradientStop { Color = Color.FromArgb(s.A, s.R, s.G, s.B), Offset = s.Offset });
        return b;
    }
}
