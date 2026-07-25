// =============================================================================
// Theme.cs — Paleta central de WinCue (ÚNICA fuente de verdad del color)
// =============================================================================
// Antes de este fichero el color vivía en ~425 sitios: 84 tripletas RGB en C#,
// 41 hex en XAML y 81 campos "static readonly Brush" repartidos por 8 ficheros.
// Nada de eso se podía cambiar en un solo sitio, y ya había DERIVADO (el color
// de tipo de cue del timeline y el de la tabla dejaron de coincidir).
//
// Reglas de uso:
//   · C#   → Theme.Go, Theme.Pgm…  (Color) y Theme.GoBrush… (Brush congelado)
//   · XAML → {DynamicResource Go}, {DynamicResource PanelBg}…
//            Los pinceles se registran en Application.Resources desde
//            App.OnStartup vía Theme.Install(), ANTES de crear ninguna ventana.
//            Es DynamicResource (no StaticResource) porque el diccionario se
//            rellena en código, después de parsear App.xaml.
//
// Los Pen siguen construyéndose en cada control (llevan grosor y guiones
// propios), pero tomando el COLOR de aquí: FrozenPen(Theme.Playhead, 2).
//
// NOTA — grises casi duplicados: este fichero preserva los valores EXACTOS que
// había, sin cambiar un píxel. Por eso conviven tonos casi idénticos
// (WindowBg #1e1e1e vs TimelineLayerA #1e1e21, etc.). Están agrupados y
// marcados con «≈» para que colapsarlos sea después una decisión consciente:
// ahora es cambiar una línea, no rastrear 400.
// =============================================================================

using System.Windows;
using System.Windows.Media;
using WinCue.Core;

namespace WinCue.App;

/// <summary>Paleta central: todos los colores de la interfaz, en un sitio.</summary>
public static class Theme
{
    // ═════════════════════════ ACENTOS DE ESTADO ═════════════════════════
    // El vocabulario de color de la consola. Estos SÍ significan algo y no
    // deben tocarse sin pensar: el operador los lee de reojo en directo.

    /// <summary>Verde: reproduciendo / OK / marca de GO.</summary>
    public static readonly Color Go = Hex(0x2ecc71);
    /// <summary>Verde oscuro: fondo del botón GO.</summary>
    public static readonly Color GoButton = Hex(0x1e7e34);
    /// <summary>Verde medio (barra de audición).</summary>
    public static readonly Color GoDeep = Hex(0x27865a);

    /// <summary>Rojo: programa en aire / playhead / corte duro.</summary>
    public static readonly Color Pgm = Hex(0xc0392b);
    /// <summary>Rojo oscuro: fondo del botón PANIC.</summary>
    public static readonly Color PanicButton = Hex(0x7e1e1e);

    /// <summary>Ámbar brillante: cursor de edición, pre-wait, hover, reloj.</summary>
    public static readonly Color Standby = Hex(0xe8c000);
    /// <summary>Ámbar: aviso, fundido, caret de texto.</summary>
    public static readonly Color Warn = Hex(0xe8a200);
    /// <summary>Ámbar quemado: reducción de ganancia del compresor.</summary>
    public static readonly Color WarnDeep = Hex(0xe87a00);

    /// <summary>Oro: modo EDIT y cabecera de grupo.</summary>
    public static readonly Color Gold = Hex(0xc9a227);
    /// <summary>Oro claro (variante de realce).</summary>
    public static readonly Color GoldSoft = Hex(0xc9a84a);

    /// <summary>Rojo saturado: clip de nivel.</summary>
    public static readonly Color Clip = Hex(0xff2020);
    /// <summary>Salmón: texto de error sobre fondo oscuro.</summary>
    public static readonly Color Danger = Hex(0xff8a7a);

    /// <summary>Azul medio: marco de proyección y banda de agudos del EQ.
    /// Declarado AQUÍ (y no junto a los acentos auxiliares) porque los
    /// inicializadores static readonly corren en orden textual: GeometryFrame
    /// lo referencia más abajo y se quedaría en negro transparente.</summary>
    public static readonly Color Blue = Hex(0x2a6fb0);

    // ═════════════════════════ TIPOS DE CUE ═════════════════════════
    // Espejo entre la columna «Tipo» de la tabla y los bloques del timeline.
    // Vivían duplicados en CueRowViewModel y TimelinePanel, y habían derivado.

    public static readonly Color TypeAudio = Hex(0x4a86b8);
    public static readonly Color TypeVideo = Hex(0x9b59b6);
    public static readonly Color TypeWait = Hex(0x888888);
    public static readonly Color TypeFade = Warn;
    public static readonly Color TypeStart = Go;
    public static readonly Color TypeStop = Pgm;
    public static readonly Color TypeScript = Hex(0x16a085);
    public static readonly Color TypeMemo = Hex(0x777777);
    public static readonly Color TypeGroup = Standby;

    /// <summary>Color de un tipo de cue. ÚNICA definición: la comparten la
    /// columna «Tipo» de la tabla (CueRowViewModel) y los bloques del timeline
    /// (TimelinePanel). Antes estaban duplicadas y habían DERIVADO: Wait era
    /// #666666 en el timeline y #888888 en la tabla; Group, #c9a227 y #e8c000.
    /// Manda la tabla, que es lo que el timeline decía estar reflejando.</summary>
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

    // ═════════════════════════ TEXTO ═════════════════════════

    /// <summary>Etiquetas sobre lienzo oscuro (timeline).</summary>
    public static readonly Color TextBright = Hex(0xeeeeee);
    /// <summary>Cabeceras de Configuración. Era #e6e6e6: colapsado.</summary>
    public static readonly Color TextBrightAlt = TextBright;
    /// <summary>Texto principal de la interfaz.</summary>
    public static readonly Color TextPrimary = Hex(0xdddddd);
    /// <summary>Texto de hover.</summary>
    public static readonly Color TextSecondary = Hex(0xcccccc);
    /// <summary>Cabeceras de columna.</summary>
    public static readonly Color TextTertiary = Hex(0xbbbbbb);
    /// <summary>Etiquetas atenuadas.</summary>
    public static readonly Color TextMuted = Hex(0x999999);
    /// <summary>Era #9a9a9a: colapsado.</summary>
    public static readonly Color TextMutedAlt = TextMuted;
    /// <summary>Texto secundario/rótulos pequeños.</summary>
    public static readonly Color TextDim = Hex(0x888888);
    /// <summary>Rejilla y unidades.</summary>
    public static readonly Color TextDimmer = Hex(0x777777);
    /// <summary>Mensajes de «vacío» y marcadores de posición.</summary>
    public static readonly Color TextFaint = Hex(0x666666);
    /// <summary>Texto oscuro SOBRE un acento claro (caja de duración).</summary>
    public static readonly Color TextOnAccent = Hex(0x101010);
    /// <summary>Notas en cursiva y marcadores «sin valor». Es el gris del
    /// sistema (Brushes.Gray), que estaba suelto por el inspector.</summary>
    public static readonly Color TextNote = Hex(0x808080);

    public static readonly Color White = Hex(0xffffff);
    public static readonly Color Black = Hex(0x000000);

    // ═════════════════════════ FONDOS ═════════════════════════

    /// <summary>Fondo de ventana.</summary>
    public static readonly Color WindowBg = Hex(0x1e1e1e);
    /// <summary>Paneles laterales y barras.</summary>
    public static readonly Color PanelBg = Hex(0x252526);
    /// <summary>Fondo de control (TextBox, ComboBox).</summary>
    public static readonly Color ControlBg = Hex(0x2a2a2a);
    /// <summary>Barra de herramientas. Era #2a2a2b: colapsado.</summary>
    public static readonly Color ToolbarBg = ControlBg;
    /// <summary>Cabecera de la tabla de cues.</summary>
    public static readonly Color HeaderBg = Hex(0x2d2d30);
    /// <summary>Fondo de botón.</summary>
    public static readonly Color ButtonBg = Hex(0x3c3c3c);
    /// <summary>Separador y borde de strip del mezclador. Era #3a3a3a: colapsado.</summary>
    public static readonly Color ButtonBgAlt = ButtonBg;
    /// <summary>Tarjeta de «en reproducción».</summary>
    public static readonly Color CardBg = Hex(0x333333);
    /// <summary>Campo hundido (búsqueda).</summary>
    public static readonly Color SunkenBg = Hex(0x1a1a1a);
    /// <summary>Caja del reloj del show.</summary>
    public static readonly Color DeepBg = Hex(0x151515);
    /// <summary>Pestañas no seleccionadas.</summary>
    public static readonly Color DeeperBg = Hex(0x111111);
    /// <summary>Fondo de lista (desplegables).</summary>
    public static readonly Color ListBg = Hex(0x262626);
    /// <summary>Carril vacío de una barra de progreso.</summary>
    public static readonly Color TrackBg = Hex(0x222222);
    /// <summary>Fondo de medidor (VU, GR). Vive aquí y no en la sección del
    /// mezclador porque el EQ lo aliasa y los static readonly se inicializan
    /// en orden textual.</summary>
    public static readonly Color MeterBg = Hex(0x202020);

    // ═════════════════════════ BORDES Y SEPARADORES ═════════════════════════

    public static readonly Color Border = Hex(0x555555);
    /// <summary>≈ Border — marco de medidor.</summary>
    public static readonly Color BorderMeter = Hex(0x4a4a4a);
    /// <summary>≈ Border — borde de control.</summary>
    public static readonly Color BorderControl = Hex(0x444444);
    /// <summary>Separadores y splitters.</summary>
    public static readonly Color Divider = Hex(0x333333);

    // ═════════════════════════ SELECCIÓN ═════════════════════════

    /// <summary>Fila del playhead.</summary>
    public static readonly Color PlayheadRow = Hex(0x264f78);
    /// <summary>Celda seleccionada.</summary>
    public static readonly Color SelectionBg = Hex(0x37527a);
    /// <summary>Borde de celda seleccionada.</summary>
    public static readonly Color SelectionBorder = Hex(0x5a80c0);

    // ═════════════════════════ TIMELINE ═════════════════════════
    // Los grises del lienzo llevaban un punto de azul propio (…21, …25, …29,
    // …58) imperceptible al ojo. Colapsados a los tonos generales: lo que sí
    // importa —el contraste ENTRE pista par e impar— se conserva (1e vs 22).

    public static readonly Color TimelineBg = Hex(0x18181a);
    /// <summary>Pista par. Era #1e1e21: colapsado.</summary>
    public static readonly Color TimelineLayerA = WindowBg;
    /// <summary>Pista impar. Era #222225: colapsado.</summary>
    public static readonly Color TimelineLayerB = TrackBg;
    /// <summary>Fondo de la regla de tiempo. Era #262629: colapsado.</summary>
    public static readonly Color TimelineRulerBg = ListBg;
    /// <summary>Marca mayor de la regla. Era #555558: colapsado.</summary>
    public static readonly Color TimelineTick = Border;
    /// <summary>Marca menor de la regla.</summary>
    public static readonly Color TimelineTickMinor = Hex(0x303033);

    // ═════════════════════════ EDITOR DE EQ / COMPRESOR ═════════════════════════

    public static readonly Color EqBg = Hex(0x141416);
    public static readonly Color EqGrid = Hex(0x2c2c30);
    /// <summary>Línea de 0 dB. Era #4a4a50: colapsado.</summary>
    public static readonly Color EqZero = BorderMeter;
    /// <summary>Fondo del medidor de reducción de ganancia. Era #202024: colapsado.</summary>
    public static readonly Color EqGrBg = MeterBg;
    /// <summary>Centro oscuro del manejador de banda. NO se colapsa con EqBg:
    /// es lo que da cuerpo al punto sobre el fondo del lienzo.</summary>
    public static readonly Color EqHandleCore = Hex(0x161619);
    /// <summary>Curva de EQ desactivada.</summary>
    public static readonly Color EqCurveOff = Hex(0x665e30);
    /// <summary>Curva de compresor desactivada.</summary>
    public static readonly Color CompCurveOff = Hex(0x6e5828);
    /// <summary>Relleno del analizador (RTA), verde claro.</summary>
    public static readonly Color RtaLine = Hex(0x5ff29e);
    /// <summary>Pico del analizador, casi blanco verdoso.</summary>
    public static readonly Color RtaPeak = Hex(0xd9ffe9);

    // ═════════════════════════ FORMA DE ONDA ═════════════════════════

    public static readonly Color WaveBg = Hex(0x0e0e0e);
    /// <summary>Onda (= TypeAudio: es el color del cue de audio).</summary>
    public static readonly Color Wave = TypeAudio;
    /// <summary>Onda fuera de la región activa.</summary>
    public static readonly Color WaveDim = Hex(0x243038);
    /// <summary>≈ WaveDim — barra de vídeo (degradado).</summary>
    public static readonly Color VideoBarA = Hex(0x2a3540);
    /// <summary>≈ WaveDim — barra de vídeo (degradado).</summary>
    public static readonly Color VideoBarB = Hex(0x1a222a);

    // ═════════════════════════ MEDIDOR VU / MEZCLADOR ═════════════════════════

    /// <summary>Fondo del panel del timeline. Era #202022: colapsado.
    /// (MeterBg vive en la sección de FONDOS por orden de inicialización.)</summary>
    public static readonly Color MeterBgAlt = MeterBg;

    // ═════════════════════════ LIENZO DE GEOMETRÍA ═════════════════════════

    /// <summary>Fondo del lienzo de geometría. Era #141414: colapsado con EqBg.</summary>
    public static readonly Color GeometryBg = EqBg;
    /// <summary>Marco del área de proyección (= Blue).</summary>
    public static readonly Color GeometryFrame = Blue;
    public static readonly Color GeometryGrid = Hex(0x666666);
    public static readonly Color GeometryCross = Hex(0x888888);

    // ═════════════════════════ ACENTOS AUXILIARES ═════════════════════════

    public static readonly Color Info = Hex(0x3598db);
    /// <summary>≈ Info.</summary>
    public static readonly Color InfoAlt = Hex(0x2e86de);
    public static readonly Color Magenta = Hex(0xc95ed6);
    /// <summary>≈ Magenta.</summary>
    public static readonly Color MagentaAlt = Hex(0xbe2ede);
    public static readonly Color Teal = Hex(0x2f6f4f);

    // ═════════════════════════ PANEL DE NOTAS ═════════════════════════

    public static readonly Color NotesBg = Hex(0x2d2a1e);
    public static readonly Color NotesLabel = Hex(0xa89968);
    public static readonly Color NotesText = Hex(0xe8dcb8);

    // ═════════════════════════ BOTONES DE ACCIÓN ═════════════════════════

    /// <summary>Botón «Relocalizar» (medios no encontrados).</summary>
    public static readonly Color RelocateBg = Hex(0x7e5a1e);
    /// <summary>Botón «detener este cue».</summary>
    public static readonly Color StopSmallBg = Hex(0x5a2a2a);
    public static readonly Color StopSmallBorder = Hex(0x703030);

    // ═════════════════════════ PINCELES ═════════════════════════
    // Congelados (Freeze) → compartibles entre hilos y sin coste de render.
    // Solo los que se asignan directamente; para Pen se usa el Color.

    public static readonly Brush GoBrush = Solid(Go);
    public static readonly Brush PgmBrush = Solid(Pgm);
    public static readonly Brush StandbyBrush = Solid(Standby);
    public static readonly Brush WarnBrush = Solid(Warn);
    public static readonly Brush GoldBrush = Solid(Gold);

    public static readonly Brush TextBrightBrush = Solid(TextBright);
    public static readonly Brush TextPrimaryBrush = Solid(TextPrimary);
    public static readonly Brush TextTertiaryBrush = Solid(TextTertiary);
    public static readonly Brush TextMutedBrush = Solid(TextMuted);
    public static readonly Brush TextDimBrush = Solid(TextDim);
    public static readonly Brush TextDimmerBrush = Solid(TextDimmer);
    public static readonly Brush TextFaintBrush = Solid(TextFaint);
    public static readonly Brush TextOnAccentBrush = Solid(TextOnAccent);
    public static readonly Brush TextNoteBrush = Solid(TextNote);

    public static readonly Brush WindowBgBrush = Solid(WindowBg);
    public static readonly Brush PanelBgBrush = Solid(PanelBg);
    public static readonly Brush ControlBgBrush = Solid(ControlBg);
    public static readonly Brush ButtonBgBrush = Solid(ButtonBg);
    public static readonly Brush CardBgBrush = Solid(CardBg);
    public static readonly Brush BorderBrush = Solid(Border);

    /// <summary>Pincel por tipo de cue, precalculado: la tabla lo pide en cada
    /// refresco de fila y antes se creaba un SolidColorBrush nuevo por llamada.
    /// Se construye tras los tokens Type* (orden textual de inicialización).</summary>
    private static readonly Dictionary<CueType, SolidColorBrush> CueTypeBrushes =
        Enum.GetValues<CueType>().ToDictionary(t => t, t => Solid(ForCueType(t)));

    /// <summary>Pincel del color de un tipo de cue.</summary>
    public static SolidColorBrush BrushForCueType(CueType type) =>
        CueTypeBrushes.TryGetValue(type, out var b) ? b : Solid(TextPrimary);

    // ═════════════════════════ HERRAMIENTAS ═════════════════════════

    /// <summary>0xRRGGBB → Color opaco. Se escribe el hex tal cual, que es como
    /// se piensa un color, en vez de tres bytes sueltos.</summary>
    private static Color Hex(uint rgb) => Color.FromRgb(
        (byte)((rgb >> 16) & 0xff), (byte)((rgb >> 8) & 0xff), (byte)(rgb & 0xff));

    /// <summary>Mismo color con otra opacidad (para velos y realces).</summary>
    public static Color Alpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    /// <summary>Pincel sólido congelado.</summary>
    public static SolidColorBrush Solid(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    /// <summary>Pincel sólido congelado con opacidad.</summary>
    public static SolidColorBrush Solid(Color c, byte a) => Solid(Alpha(c, a));

    /// <summary>Publica la paleta como recursos para el XAML. Se llama desde
    /// App.OnStartup ANTES de construir ninguna ventana; el XAML la consume con
    /// {DynamicResource NombreDelToken}.</summary>
    public static void Install(ResourceDictionary target)
    {
        ArgumentNullException.ThrowIfNull(target);

        foreach (var f in typeof(Theme).GetFields(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
        {
            if (f.FieldType == typeof(Color) && f.GetValue(null) is Color c)
            {
                // Red de seguridad: los inicializadores static readonly corren en
                // ORDEN TEXTUAL, así que un token que sea alias de otro declarado
                // MÁS ABAJO se queda en default(Color) = negro transparente. Todos
                // los tokens son opacos, luego A==0 solo puede ser ese fallo.
                // Mejor reventar al arrancar que pintar un hueco invisible en vivo.
                if (c.A == 0)
                    throw new InvalidOperationException(
                        $"Theme.{f.Name} está sin inicializar (alias declarado antes que su origen).");

                target[f.Name] = Solid(c);              // {DynamicResource Go}
                target[f.Name + "Color"] = c;           // por si hace falta el Color
            }
            else if (typeof(Brush).IsAssignableFrom(f.FieldType) && f.GetValue(null) is Brush b)
            {
                target[f.Name] = b;                     // {DynamicResource GoBrush}
            }
        }
    }
}
