// =============================================================================
// Theme.cs — Paleta central de WinCue (ÚNICA fuente de verdad del color)
// =============================================================================
// Antes de este fichero el color vivía en ~425 sitios: 84 tripletas RGB en C#,
// 41 hex en XAML y 81 campos "static readonly Brush" repartidos por 8 ficheros.
// Nada de eso se podía cambiar en un solo sitio, y ya había DERIVADO (el color
// de tipo de cue del timeline y el de la tabla dejaron de coincidir).
//
// Los 94 NÚMEROS viven ahora en WinCue.Presentation.Palette (sin WPF, para que
// Secuence los comparta): este fichero solo los convierte en Color y en Brush.
// Cada campo pasó de literal (Hex(0x2ecc71)) a derivado (Hex(Palette.Go)), sin
// tocar nombres ni tipos — el XAML y el resto de C# no se enteran del cambio.
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
// NOTA — grises casi duplicados: este fichero (y Palette, de donde vienen los
// números) preserva los valores EXACTOS que había, sin cambiar un píxel. Por
// eso conviven tonos casi idénticos (WindowBg #1e1e1e vs TimelineLayerA
// #1e1e21, etc.). Están agrupados y marcados con «≈» para que colapsarlos sea
// después una decisión consciente: ahora es cambiar una línea, no rastrear 400.
// =============================================================================

using System.Windows;
using System.Windows.Media;
using WinCue.Core;
using WinCue.Presentation;

namespace WinCue.App;

/// <summary>Paleta central: todos los colores de la interfaz, en un sitio.</summary>
public static class Theme
{
    // ═════════════════════════ ACENTOS DE ESTADO ═════════════════════════
    // El vocabulario de color de la consola. Estos SÍ significan algo y no
    // deben tocarse sin pensar: el operador los lee de reojo en directo.

    /// <summary>Verde: reproduciendo / OK / marca de GO.</summary>
    public static readonly Color Go = Hex(Palette.Go);
    /// <summary>Verde oscuro: fondo del botón GO.</summary>
    public static readonly Color GoButton = Hex(Palette.GoButton);
    /// <summary>Verde medio (barra de audición).</summary>
    public static readonly Color GoDeep = Hex(Palette.GoDeep);

    /// <summary>Rojo: programa en aire / playhead / corte duro.</summary>
    public static readonly Color Pgm = Hex(Palette.Pgm);
    /// <summary>Rojo oscuro: fondo del botón PANIC.</summary>
    public static readonly Color PanicButton = Hex(Palette.PanicButton);

    /// <summary>Ámbar brillante: cursor de edición, pre-wait, hover, reloj.</summary>
    public static readonly Color Standby = Hex(Palette.Standby);
    /// <summary>Ámbar: aviso, fundido, caret de texto.</summary>
    public static readonly Color Warn = Hex(Palette.Warn);
    /// <summary>Ámbar quemado: reducción de ganancia del compresor.</summary>
    public static readonly Color WarnDeep = Hex(Palette.WarnDeep);

    /// <summary>Oro: modo EDIT y cabecera de grupo.</summary>
    public static readonly Color Gold = Hex(Palette.Gold);
    /// <summary>Oro claro (variante de realce).</summary>
    public static readonly Color GoldSoft = Hex(Palette.GoldSoft);

    /// <summary>Rojo saturado: clip de nivel.</summary>
    public static readonly Color Clip = Hex(Palette.Clip);
    /// <summary>Salmón: texto de error sobre fondo oscuro.</summary>
    public static readonly Color Danger = Hex(Palette.Danger);

    /// <summary>Azul medio: marco de proyección y banda de agudos del EQ.
    /// Declarado AQUÍ (y no junto a los acentos auxiliares) porque los
    /// inicializadores static readonly corren en orden textual: GeometryFrame
    /// lo referencia más abajo y se quedaría en negro transparente.</summary>
    public static readonly Color Blue = Hex(Palette.Blue);

    // ═════════════════════════ TIPOS DE CUE ═════════════════════════
    // Espejo entre la columna «Tipo» de la tabla y los bloques del timeline.
    // Vivían duplicados en CueRowViewModel y TimelinePanel, y habían derivado.

    public static readonly Color TypeAudio = Hex(Palette.TypeAudio);
    public static readonly Color TypeVideo = Hex(Palette.TypeVideo);
    public static readonly Color TypeWait = Hex(Palette.TypeWait);
    public static readonly Color TypeFade = Warn;
    public static readonly Color TypeStart = Go;
    public static readonly Color TypeStop = Pgm;
    public static readonly Color TypeScript = Hex(Palette.TypeScript);
    public static readonly Color TypeMemo = Hex(Palette.TypeMemo);
    public static readonly Color TypeGroup = Standby;

    /// <summary>Color de un tipo de cue. ÚNICA definición del SWITCH —
    /// TimelinePalette.ForCueType (WinCue.Presentation, hitos-0-1, timeline) —
    /// la comparten la columna «Tipo» de la tabla (CueRowViewModel), los
    /// bloques del timeline (TimelinePanel) Y Secuence. Antes estaban
    /// duplicadas y habían DERIVADO: Wait era #666666 en el timeline y
    /// #888888 en la tabla; Group, #c9a227 y #e8c000. Aquí solo se traduce el
    /// token de Palette (uint) a Color de WPF.</summary>
    public static Color ForCueType(CueType type) => Hex(TimelinePalette.ForCueType(type));

    // ═════════════════════════ TEXTO ═════════════════════════

    /// <summary>Etiquetas sobre lienzo oscuro (timeline).</summary>
    public static readonly Color TextBright = Hex(Palette.TextBright);
    /// <summary>Cabeceras de Configuración. Era #e6e6e6: colapsado.</summary>
    public static readonly Color TextBrightAlt = TextBright;
    /// <summary>Texto principal de la interfaz.</summary>
    public static readonly Color TextPrimary = Hex(Palette.TextPrimary);
    /// <summary>Texto de hover.</summary>
    public static readonly Color TextSecondary = Hex(Palette.TextSecondary);
    /// <summary>Cabeceras de columna.</summary>
    public static readonly Color TextTertiary = Hex(Palette.TextTertiary);
    /// <summary>Etiquetas atenuadas.</summary>
    public static readonly Color TextMuted = Hex(Palette.TextMuted);
    /// <summary>Era #9a9a9a: colapsado.</summary>
    public static readonly Color TextMutedAlt = TextMuted;
    /// <summary>Texto secundario/rótulos pequeños.</summary>
    public static readonly Color TextDim = Hex(Palette.TextDim);
    /// <summary>Rejilla y unidades.</summary>
    public static readonly Color TextDimmer = Hex(Palette.TextDimmer);
    /// <summary>Mensajes de «vacío» y marcadores de posición.</summary>
    public static readonly Color TextFaint = Hex(Palette.TextFaint);
    /// <summary>Texto oscuro SOBRE un acento claro (caja de duración).</summary>
    public static readonly Color TextOnAccent = Hex(Palette.TextOnAccent);
    /// <summary>Notas en cursiva y marcadores «sin valor». Es el gris del
    /// sistema (Brushes.Gray), que estaba suelto por el inspector.</summary>
    public static readonly Color TextNote = Hex(Palette.TextNote);

    public static readonly Color White = Hex(Palette.White);
    public static readonly Color Black = Hex(Palette.Black);

    // ═════════════════════════ FONDOS ═════════════════════════

    /// <summary>Fondo de ventana.</summary>
    public static readonly Color WindowBg = Hex(Palette.WindowBg);
    /// <summary>Paneles laterales y barras.</summary>
    public static readonly Color PanelBg = Hex(Palette.PanelBg);
    /// <summary>Fondo de control (TextBox, ComboBox).</summary>
    public static readonly Color ControlBg = Hex(Palette.ControlBg);
    /// <summary>Barra de herramientas. Era #2a2a2b: colapsado.</summary>
    public static readonly Color ToolbarBg = ControlBg;
    /// <summary>Cabecera de la tabla de cues.</summary>
    public static readonly Color HeaderBg = Hex(Palette.HeaderBg);
    /// <summary>Fondo de botón.</summary>
    public static readonly Color ButtonBg = Hex(Palette.ButtonBg);
    /// <summary>Separador y borde de strip del mezclador. Era #3a3a3a: colapsado.</summary>
    public static readonly Color ButtonBgAlt = ButtonBg;
    /// <summary>Tarjeta de «en reproducción».</summary>
    public static readonly Color CardBg = Hex(Palette.CardBg);
    /// <summary>Campo hundido (búsqueda).</summary>
    public static readonly Color SunkenBg = Hex(Palette.SunkenBg);
    /// <summary>Caja del reloj del show.</summary>
    public static readonly Color DeepBg = Hex(Palette.DeepBg);
    /// <summary>Pestañas no seleccionadas.</summary>
    public static readonly Color DeeperBg = Hex(Palette.DeeperBg);
    /// <summary>Fondo de lista (desplegables).</summary>
    public static readonly Color ListBg = Hex(Palette.ListBg);
    /// <summary>Carril vacío de una barra de progreso.</summary>
    public static readonly Color TrackBg = Hex(Palette.TrackBg);
    /// <summary>Fondo de medidor (VU, GR). Vive aquí y no en la sección del
    /// mezclador porque el EQ lo aliasa y los static readonly se inicializan
    /// en orden textual.</summary>
    public static readonly Color MeterBg = Hex(Palette.MeterBg);

    // ═════════════════════════ BORDES Y SEPARADORES ═════════════════════════

    public static readonly Color Border = Hex(Palette.Border);
    /// <summary>≈ Border — marco de medidor.</summary>
    public static readonly Color BorderMeter = Hex(Palette.BorderMeter);
    /// <summary>≈ Border — borde de control.</summary>
    public static readonly Color BorderControl = Hex(Palette.BorderControl);
    /// <summary>Separadores y splitters.</summary>
    public static readonly Color Divider = Hex(Palette.Divider);

    // ═════════════════════════ SELECCIÓN ═════════════════════════

    /// <summary>Fila del playhead.</summary>
    public static readonly Color PlayheadRow = Hex(Palette.PlayheadRow);
    /// <summary>Celda seleccionada.</summary>
    public static readonly Color SelectionBg = Hex(Palette.SelectionBg);
    /// <summary>Borde de celda seleccionada.</summary>
    public static readonly Color SelectionBorder = Hex(Palette.SelectionBorder);

    // ═════════════════════════ TIMELINE ═════════════════════════
    // Los grises del lienzo llevaban un punto de azul propio (…21, …25, …29,
    // …58) imperceptible al ojo. Colapsados a los tonos generales: lo que sí
    // importa —el contraste ENTRE pista par e impar— se conserva (1e vs 22).

    public static readonly Color TimelineBg = Hex(Palette.TimelineBg);
    /// <summary>Pista par. Era #1e1e21: colapsado.</summary>
    public static readonly Color TimelineLayerA = WindowBg;
    /// <summary>Pista impar. Era #222225: colapsado.</summary>
    public static readonly Color TimelineLayerB = TrackBg;
    /// <summary>Fondo de la regla de tiempo. Era #262629: colapsado.</summary>
    public static readonly Color TimelineRulerBg = ListBg;
    /// <summary>Marca mayor de la regla. Era #555558: colapsado.</summary>
    public static readonly Color TimelineTick = Border;
    /// <summary>Marca menor de la regla.</summary>
    public static readonly Color TimelineTickMinor = Hex(Palette.TimelineTickMinor);

    // ═════════════════════════ EDITOR DE EQ / COMPRESOR ═════════════════════════

    public static readonly Color EqBg = Hex(Palette.EqBg);
    public static readonly Color EqGrid = Hex(Palette.EqGrid);
    /// <summary>Línea de 0 dB. Era #4a4a50: colapsado.</summary>
    public static readonly Color EqZero = BorderMeter;
    /// <summary>Fondo del medidor de reducción de ganancia. Era #202024: colapsado.</summary>
    public static readonly Color EqGrBg = MeterBg;
    /// <summary>Centro oscuro del manejador de banda. NO se colapsa con EqBg:
    /// es lo que da cuerpo al punto sobre el fondo del lienzo.</summary>
    public static readonly Color EqHandleCore = Hex(Palette.EqHandleCore);
    /// <summary>Curva de EQ desactivada.</summary>
    public static readonly Color EqCurveOff = Hex(Palette.EqCurveOff);
    /// <summary>Curva de compresor desactivada.</summary>
    public static readonly Color CompCurveOff = Hex(Palette.CompCurveOff);
    /// <summary>Relleno del analizador (RTA), verde claro.</summary>
    public static readonly Color RtaLine = Hex(Palette.RtaLine);
    /// <summary>Pico del analizador, casi blanco verdoso.</summary>
    public static readonly Color RtaPeak = Hex(Palette.RtaPeak);

    // ═════════════════════════ FORMA DE ONDA ═════════════════════════

    public static readonly Color WaveBg = Hex(Palette.WaveBg);
    /// <summary>Onda (= TypeAudio: es el color del cue de audio).</summary>
    public static readonly Color Wave = TypeAudio;
    /// <summary>Onda fuera de la región activa.</summary>
    public static readonly Color WaveDim = Hex(Palette.WaveDim);
    /// <summary>≈ WaveDim — barra de vídeo (degradado).</summary>
    public static readonly Color VideoBarA = Hex(Palette.VideoBarA);
    /// <summary>≈ WaveDim — barra de vídeo (degradado).</summary>
    public static readonly Color VideoBarB = Hex(Palette.VideoBarB);

    // ═════════════════════════ MEDIDOR VU / MEZCLADOR ═════════════════════════

    /// <summary>Fondo del panel del timeline. Era #202022: colapsado.
    /// (MeterBg vive en la sección de FONDOS por orden de inicialización.)</summary>
    public static readonly Color MeterBgAlt = MeterBg;

    // ═════════════════════════ LIENZO DE GEOMETRÍA ═════════════════════════

    /// <summary>Fondo del lienzo de geometría. Era #141414: colapsado con EqBg.</summary>
    public static readonly Color GeometryBg = EqBg;
    /// <summary>Marco del área de proyección (= Blue).</summary>
    public static readonly Color GeometryFrame = Blue;
    public static readonly Color GeometryGrid = Hex(Palette.GeometryGrid);
    public static readonly Color GeometryCross = Hex(Palette.GeometryCross);

    // ═════════════════════════ ACENTOS AUXILIARES ═════════════════════════

    public static readonly Color Info = Hex(Palette.Info);
    /// <summary>≈ Info.</summary>
    public static readonly Color InfoAlt = Hex(Palette.InfoAlt);
    public static readonly Color Magenta = Hex(Palette.Magenta);
    /// <summary>≈ Magenta.</summary>
    public static readonly Color MagentaAlt = Hex(Palette.MagentaAlt);
    public static readonly Color Teal = Hex(Palette.Teal);

    // ═════════════════════════ PANEL DE NOTAS ═════════════════════════

    public static readonly Color NotesBg = Hex(Palette.NotesBg);
    public static readonly Color NotesLabel = Hex(Palette.NotesLabel);
    public static readonly Color NotesText = Hex(Palette.NotesText);

    // ═════════════════════════ BOTONES DE ACCIÓN ═════════════════════════

    /// <summary>Botón «Relocalizar» (medios no encontrados).</summary>
    public static readonly Color RelocateBg = Hex(Palette.RelocateBg);
    /// <summary>Botón «detener este cue» de las tarjetas de EN REPRODUCCIÓN.
    /// Era un granate (#5a2a2a) que sobre la tarjeta (#333) se leía como un
    /// trozo muerto: ahora es el ROJO de programa, con un cuadrado blanco
    /// dentro (el símbolo universal de parada).</summary>
    public static readonly Color StopSmallBg = Pgm;
    public static readonly Color StopSmallBorder = Hex(Palette.StopSmallBorder);

    /// <summary>Fondo de los desplegables de flecha pegados a «Abrir» y
    /// «Guardar». Es EL MISMO gris del botón principal a propósito: hundirlo
    /// (estuvo en 0x232323) los separaba, sí, pero cantaba como un parche de
    /// otro color al lado del botón. Se leen como dos controles por el hueco,
    /// el borde propio y el caret ámbar — no por ser de otro gris.</summary>
    public static readonly Color SplitArrowBg = ButtonBg;

    // ═════════════════════════ RELIEVE DE LOS BOTONES ═════════════════════════
    // El botón deja de ser un rectángulo plano y pasa a leerse como una tecla de
    // mesa: luz arriba, sombra abajo, y al pulsar se hunde.
    //
    // CLAVE: son VELOS TRANSLÚCIDOS que van ENCIMA del Background del botón, no
    // en lugar de él. Por eso el mismo relieve sirve para el gris de la barra,
    // el verde de GO y el rojo de PANIC sin declarar un degradado por color; si
    // fueran opacos habría que mantener un juego para cada uno.

    /// <summary>Cara en reposo: brillo arriba, sombra abajo.</summary>
    public static readonly Brush BevelFace = Veil(0x2A, 0x30);
    /// <summary>Cara pulsada: el degradado invertido es lo que hace que la tecla
    /// parezca hundida (la sombra pasa arriba).</summary>
    public static readonly Brush BevelFacePressed = Veil(0x30, 0x2A, inverted: true);
    /// <summary>Velo de «ratón encima»: aclara la cara entera ~8 %.</summary>
    public static readonly Brush BevelHover = Solid(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
    /// <summary>Canto: filo claro arriba que se apaga hacia abajo. Es lo que da
    /// el grosor; sin él el degradado de la cara se queda en un manchón.</summary>
    public static readonly Brush BevelEdge = Veil(0x40, 0x58);

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
    /// se piensa un color, en vez de tres bytes sueltos.
    ///
    /// Pública (no solo de uso interno de este fichero): VuMeterControl la usa
    /// para convertir los tokens uint de WinCue.Presentation.VuMeterPalette —
    /// el cálculo compartido con Secuence, piloto del traslado a Win2D— sin
    /// duplicar aquí la misma cuenta de bits.</summary>
    public static Color Hex(uint rgb) => Color.FromRgb(
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

    /// <summary>Velo vertical blanco→negro para el relieve de los botones:
    /// <paramref name="light"/> es la opacidad del brillo y <paramref name="dark"/>
    /// la de la sombra. Se corta en el centro con dos paradas a opacidad 0 para
    /// que el brillo NO se degrade hasta la sombra pasando por un gris muerto:
    /// así hay filo arriba, cara neutra en medio y sombra abajo, que es como se
    /// comporta una tecla real. Con <paramref name="inverted"/> la sombra va
    /// arriba, que es el estado pulsado.</summary>
    private static LinearGradientBrush Veil(byte light, byte dark, bool inverted = false)
    {
        var top = Color.FromArgb(inverted ? dark : light,
                                 (byte)(inverted ? 0 : 255), (byte)(inverted ? 0 : 255),
                                 (byte)(inverted ? 0 : 255));
        var bottom = Color.FromArgb(inverted ? light : dark,
                                    (byte)(inverted ? 255 : 0), (byte)(inverted ? 255 : 0),
                                    (byte)(inverted ? 255 : 0));
        var b = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        b.GradientStops.Add(new GradientStop(top, 0.0));
        b.GradientStops.Add(new GradientStop(Alpha(top, 0), 0.45));
        b.GradientStops.Add(new GradientStop(Alpha(bottom, 0), 0.55));
        b.GradientStops.Add(new GradientStop(bottom, 1.0));
        b.Freeze();
        return b;
    }

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
