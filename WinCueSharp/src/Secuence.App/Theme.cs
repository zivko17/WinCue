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

    // ── Texto ───────────────────────────────────────────────────────────
    public static readonly Color TextPrimary = Hex(Palette.TextPrimary);
    public static readonly Color TextTertiary = Hex(Palette.TextTertiary);
    public static readonly Color TextDim = Hex(Palette.TextDim);
    public static readonly Color TextFaint = Hex(Palette.TextFaint);
    public static readonly Color TextOnAccent = Hex(Palette.TextOnAccent);

    // ── Bordes ──────────────────────────────────────────────────────────
    public static readonly Color Border = Hex(Palette.Border);
    public static readonly Color Divider = Hex(Palette.Divider);

    // ── Acentos de estado (reutilizados en la columna de duración) ─────
    public static readonly Color Go = Hex(Palette.Go);
    public static readonly Color GoButton = Hex(Palette.GoButton);
    public static readonly Color Pgm = Hex(Palette.Pgm);
    public static readonly Color PanicButton = Hex(Palette.PanicButton);
    public static readonly Color Standby = Hex(Palette.Standby);
    public static readonly Color Warn = Hex(Palette.Warn);

    // ── Fila del playhead (tabla de cues) ──────────────────────────────
    public static readonly Color PlayheadRow = Hex(Palette.PlayheadRow);

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
    public static readonly SolidColorBrush TextPrimaryBrush = Solid(TextPrimary);
    public static readonly SolidColorBrush TextTertiaryBrush = Solid(TextTertiary);
    public static readonly SolidColorBrush TextDimBrush = Solid(TextDim);
    public static readonly SolidColorBrush TextFaintBrush = Solid(TextFaint);
    public static readonly SolidColorBrush TextOnAccentBrush = Solid(TextOnAccent);
    public static readonly SolidColorBrush BorderBrush = Solid(Border);
    public static readonly SolidColorBrush DividerBrush = Solid(Divider);
    public static readonly SolidColorBrush GoBrush = Solid(Go);
    public static readonly SolidColorBrush GoButtonBrush = Solid(GoButton);
    public static readonly SolidColorBrush PgmBrush = Solid(Pgm);
    public static readonly SolidColorBrush PanicButtonBrush = Solid(PanicButton);
    public static readonly SolidColorBrush StandbyBrush = Solid(Standby);
    public static readonly SolidColorBrush WarnBrush = Solid(Warn);
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

    /// <summary>0xRRGGBB (de Palette) → Color opaco.</summary>
    private static Color Hex(uint rgb) => Color.FromArgb(
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
}
