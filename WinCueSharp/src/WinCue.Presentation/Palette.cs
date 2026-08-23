// =============================================================================
// Palette.cs — los VALORES de la paleta de WinCue, sin framework.
// =============================================================================
// Salieron de Theme.cs cuando apareció el segundo producto: el número
// 0x2ecc71 es el mismo en WPF y en WinUI 3, pero System.Windows.Media.Color
// solo existe en WPF. Aquí vive el número; cada interfaz lo convierte en lo
// que sepa pintar (Theme.cs en WPF).
//
// REGLA: aquí NO entra ningún tipo de interfaz. Ni Color, ni Brush, ni nada
// que venga de un framework. Si hace falta uno, el sitio es Theme.
//
// Se preservan los valores EXACTOS y los grises casi duplicados marcados «≈»,
// por el mismo motivo que los preservaba Theme: colapsarlos debe ser una
// decisión consciente y no el efecto colateral de una mudanza.
//
// Los alias de Theme (TypeFade = Warn, TimelineLayerA = WindowBg…) se
// mantienen como alias aquí, NO como el número resuelto: si mañana cambia
// Warn, TypeFade tiene que arrastrarlo. Resolverlos al copiar sería repetir
// la misma deriva que dejó descoordinados el color de tipo de cue del
// timeline y el de la tabla (ver cabecera de Theme).
// =============================================================================

namespace WinCue.Presentation;

/// <summary>Valores de color de WinCue, en 0xRRGGBB.</summary>
public static class Palette
{
    // ═════════════════════════ ACENTOS DE ESTADO ═════════════════════════
    // El vocabulario de color de la consola. Estos SÍ significan algo y no
    // deben tocarse sin pensar: el operador los lee de reojo en directo.

    /// <summary>Verde: reproduciendo / OK / marca de GO.</summary>
    public const uint Go = 0x2ecc71;
    /// <summary>Verde oscuro: fondo del botón GO.</summary>
    public const uint GoButton = 0x1e7e34;
    /// <summary>Verde medio (barra de audición).</summary>
    public const uint GoDeep = 0x27865a;

    /// <summary>Rojo: programa en aire / playhead / corte duro.</summary>
    public const uint Pgm = 0xc0392b;
    /// <summary>Rojo oscuro: fondo del botón PANIC.</summary>
    public const uint PanicButton = 0x7e1e1e;

    /// <summary>Ámbar brillante: cursor de edición, pre-wait, hover, reloj.</summary>
    public const uint Standby = 0xe8c000;
    /// <summary>Ámbar: aviso, fundido, caret de texto.</summary>
    public const uint Warn = 0xe8a200;
    /// <summary>Ámbar quemado: reducción de ganancia del compresor.</summary>
    public const uint WarnDeep = 0xe87a00;

    /// <summary>Oro: modo EDIT y cabecera de grupo.</summary>
    public const uint Gold = 0xc9a227;
    /// <summary>Oro claro (variante de realce).</summary>
    public const uint GoldSoft = 0xc9a84a;

    /// <summary>Rojo saturado: clip de nivel.</summary>
    public const uint Clip = 0xff2020;
    /// <summary>Salmón: texto de error sobre fondo oscuro.</summary>
    public const uint Danger = 0xff8a7a;

    /// <summary>Azul medio: marco de proyección y banda de agudos del EQ.</summary>
    public const uint Blue = 0x2a6fb0;

    // ═════════════════════════ TIPOS DE CUE ═════════════════════════
    // Espejo entre la columna «Tipo» de la tabla y los bloques del timeline.
    // Vivían duplicados en CueRowViewModel y TimelinePanel, y habían derivado.

    public const uint TypeAudio = 0x4a86b8;
    public const uint TypeVideo = 0x9b59b6;
    public const uint TypeWait = 0x888888;
    public const uint TypeFade = Warn;
    public const uint TypeStart = Go;
    public const uint TypeStop = Pgm;
    public const uint TypeScript = 0x16a085;
    public const uint TypeMemo = 0x777777;
    public const uint TypeGroup = Standby;

    // ═════════════════════════ TEXTO ═════════════════════════

    /// <summary>Etiquetas sobre lienzo oscuro (timeline).</summary>
    public const uint TextBright = 0xeeeeee;
    /// <summary>Cabeceras de Configuración. Era #e6e6e6: colapsado.</summary>
    public const uint TextBrightAlt = TextBright;
    /// <summary>Texto principal de la interfaz.</summary>
    public const uint TextPrimary = 0xdddddd;
    /// <summary>Texto de hover.</summary>
    public const uint TextSecondary = 0xcccccc;
    /// <summary>Cabeceras de columna.</summary>
    public const uint TextTertiary = 0xbbbbbb;
    /// <summary>Etiquetas atenuadas.</summary>
    public const uint TextMuted = 0x999999;
    /// <summary>Era #9a9a9a: colapsado.</summary>
    public const uint TextMutedAlt = TextMuted;
    /// <summary>Texto secundario/rótulos pequeños.</summary>
    public const uint TextDim = 0x888888;
    /// <summary>Rejilla y unidades.</summary>
    public const uint TextDimmer = 0x777777;
    /// <summary>Mensajes de «vacío» y marcadores de posición.</summary>
    public const uint TextFaint = 0x666666;
    /// <summary>Texto oscuro SOBRE un acento claro (caja de duración).</summary>
    public const uint TextOnAccent = 0x101010;
    /// <summary>Notas en cursiva y marcadores «sin valor». Es el gris del
    /// sistema (Brushes.Gray), que estaba suelto por el inspector.</summary>
    public const uint TextNote = 0x808080;

    public const uint White = 0xffffff;
    public const uint Black = 0x000000;

    // ═════════════════════════ FONDOS ═════════════════════════

    /// <summary>Fondo de ventana.</summary>
    public const uint WindowBg = 0x1e1e1e;
    /// <summary>Paneles laterales y barras.</summary>
    public const uint PanelBg = 0x252526;
    /// <summary>Fondo de control (TextBox, ComboBox).</summary>
    public const uint ControlBg = 0x2a2a2a;
    /// <summary>Barra de herramientas. Era #2a2a2b: colapsado.</summary>
    public const uint ToolbarBg = ControlBg;
    /// <summary>Cabecera de la tabla de cues.</summary>
    public const uint HeaderBg = 0x2d2d30;
    /// <summary>Fondo de botón.</summary>
    public const uint ButtonBg = 0x3c3c3c;
    /// <summary>Separador y borde de strip del mezclador. Era #3a3a3a: colapsado.</summary>
    public const uint ButtonBgAlt = ButtonBg;
    /// <summary>Tarjeta de «en reproducción».</summary>
    public const uint CardBg = 0x333333;
    /// <summary>Campo hundido (búsqueda).</summary>
    public const uint SunkenBg = 0x1a1a1a;
    /// <summary>Caja del reloj del show.</summary>
    public const uint DeepBg = 0x151515;
    /// <summary>Pestañas no seleccionadas.</summary>
    public const uint DeeperBg = 0x111111;
    /// <summary>Fondo de lista (desplegables).</summary>
    public const uint ListBg = 0x262626;
    /// <summary>Carril vacío de una barra de progreso.</summary>
    public const uint TrackBg = 0x222222;
    /// <summary>Fondo de medidor (VU, GR).</summary>
    public const uint MeterBg = 0x202020;

    // ═════════════════════════ BORDES Y SEPARADORES ═════════════════════════

    public const uint Border = 0x555555;
    /// <summary>≈ Border — marco de medidor.</summary>
    public const uint BorderMeter = 0x4a4a4a;
    /// <summary>≈ Border — borde de control.</summary>
    public const uint BorderControl = 0x444444;
    /// <summary>Separadores y splitters.</summary>
    public const uint Divider = 0x333333;

    // ═════════════════════════ SELECCIÓN ═════════════════════════

    /// <summary>Fila del playhead.</summary>
    public const uint PlayheadRow = 0x264f78;
    /// <summary>Celda seleccionada.</summary>
    public const uint SelectionBg = 0x37527a;
    /// <summary>Borde de celda seleccionada.</summary>
    public const uint SelectionBorder = 0x5a80c0;

    // ═════════════════════════ TIMELINE ═════════════════════════
    // Los grises del lienzo llevaban un punto de azul propio (…21, …25, …29,
    // …58) imperceptible al ojo. Colapsados a los tonos generales: lo que sí
    // importa —el contraste ENTRE pista par e impar— se conserva (1e vs 22).

    public const uint TimelineBg = 0x18181a;
    /// <summary>Pista par. Era #1e1e21: colapsado.</summary>
    public const uint TimelineLayerA = WindowBg;
    /// <summary>Pista impar. Era #222225: colapsado.</summary>
    public const uint TimelineLayerB = TrackBg;
    /// <summary>Fondo de la regla de tiempo. Era #262629: colapsado.</summary>
    public const uint TimelineRulerBg = ListBg;
    /// <summary>Marca mayor de la regla. Era #555558: colapsado.</summary>
    public const uint TimelineTick = Border;
    /// <summary>Marca menor de la regla.</summary>
    public const uint TimelineTickMinor = 0x303033;

    // ═════════════════════════ EDITOR DE EQ / COMPRESOR ═════════════════════════

    public const uint EqBg = 0x141416;
    public const uint EqGrid = 0x2c2c30;
    /// <summary>Línea de 0 dB. Era #4a4a50: colapsado.</summary>
    public const uint EqZero = BorderMeter;
    /// <summary>Fondo del medidor de reducción de ganancia. Era #202024: colapsado.</summary>
    public const uint EqGrBg = MeterBg;
    /// <summary>Centro oscuro del manejador de banda. NO se colapsa con EqBg:
    /// es lo que da cuerpo al punto sobre el fondo del lienzo.</summary>
    public const uint EqHandleCore = 0x161619;
    /// <summary>Curva de EQ desactivada.</summary>
    public const uint EqCurveOff = 0x665e30;
    /// <summary>Curva de compresor desactivada.</summary>
    public const uint CompCurveOff = 0x6e5828;
    /// <summary>Relleno del analizador (RTA), verde claro.</summary>
    public const uint RtaLine = 0x5ff29e;
    /// <summary>Pico del analizador, casi blanco verdoso.</summary>
    public const uint RtaPeak = 0xd9ffe9;

    // ═════════════════════════ FORMA DE ONDA ═════════════════════════

    public const uint WaveBg = 0x0e0e0e;
    /// <summary>Onda (= TypeAudio: es el color del cue de audio).</summary>
    public const uint Wave = TypeAudio;
    /// <summary>Onda fuera de la región activa.</summary>
    public const uint WaveDim = 0x243038;
    /// <summary>≈ WaveDim — barra de vídeo (degradado).</summary>
    public const uint VideoBarA = 0x2a3540;
    /// <summary>≈ WaveDim — barra de vídeo (degradado).</summary>
    public const uint VideoBarB = 0x1a222a;

    // ═════════════════════════ MEDIDOR VU / MEZCLADOR ═════════════════════════

    /// <summary>Fondo del panel del timeline. Era #202022: colapsado con
    /// MeterBg (sección FONDOS).</summary>
    public const uint MeterBgAlt = MeterBg;

    // ═════════════════════════ LIENZO DE GEOMETRÍA ═════════════════════════

    /// <summary>Fondo del lienzo de geometría. Era #141414: colapsado con EqBg.</summary>
    public const uint GeometryBg = EqBg;
    /// <summary>Marco del área de proyección (= Blue).</summary>
    public const uint GeometryFrame = Blue;
    public const uint GeometryGrid = 0x666666;
    public const uint GeometryCross = 0x888888;

    // ═════════════════════════ ACENTOS AUXILIARES ═════════════════════════

    public const uint Info = 0x3598db;
    /// <summary>≈ Info.</summary>
    public const uint InfoAlt = 0x2e86de;
    public const uint Magenta = 0xc95ed6;
    /// <summary>≈ Magenta.</summary>
    public const uint MagentaAlt = 0xbe2ede;
    public const uint Teal = 0x2f6f4f;

    // ═════════════════════════ PANEL DE NOTAS ═════════════════════════

    public const uint NotesBg = 0x2d2a1e;
    public const uint NotesLabel = 0xa89968;
    public const uint NotesText = 0xe8dcb8;

    // ═════════════════════════ BOTONES DE ACCIÓN ═════════════════════════

    /// <summary>Botón «Relocalizar» (medios no encontrados).</summary>
    public const uint RelocateBg = 0x7e5a1e;
    /// <summary>Botón «detener este cue» de las tarjetas de EN REPRODUCCIÓN.
    /// Era un granate (#5a2a2a) que sobre la tarjeta (#333) se leía como un
    /// trozo muerto: ahora es el ROJO de programa, con un cuadrado blanco
    /// dentro (el símbolo universal de parada).</summary>
    public const uint StopSmallBg = Pgm;
    public const uint StopSmallBorder = 0xe05a4a;

    /// <summary>Fondo de los desplegables de flecha pegados a «Abrir» y
    /// «Guardar». Es EL MISMO gris del botón principal a propósito: hundirlo
    /// (estuvo en 0x232323) los separaba, sí, pero cantaba como un parche de
    /// otro color al lado del botón. Se leen como dos controles por el hueco,
    /// el borde propio y el caret ámbar — no por ser de otro gris.</summary>
    public const uint SplitArrowBg = ButtonBg;
}
