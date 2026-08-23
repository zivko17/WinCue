// =============================================================================
// Icons.cs — Catálogo central de iconos de la interfaz
// =============================================================================
// Los 47 iconos de WinCue estaban escritos como emoji SUELTOS en 185 sitios,
// muchos incrustados dentro de cadenas traducidas — Loc.T("🎧 AUDICIÓN", …) —
// lo que hacía imposible cambiarlos sin tocar también la traducción.
//
// Aquí cada icono tiene NOMBRE, y su valor es el punto de código de Phosphor
// (MIT, assets/Phosphor.ttf). Cambiar de fuente = cambiar ESTE fichero; ni un
// solo sitio de llamada se entera.
//
// POR QUÉ UNA FUENTE Y NO UN CONTROL (tipo PackIcon): el 27 % de la interfaz
// se pinta con DrawingContext (timeline, EQ). Ahí no cabe un Control, pero
// FormattedText sí sabe dibujar un glifo. Una fuente sirve a los dos mundos;
// un control, solo al XAML.
//
// La fuente COMPUESTA (el FontFamily de WPF y el comprobador de carga que
// corre en App.OnStartup) vive en WinCue.App.IconFont, no aquí: esta clase no
// sabe de frameworks. Ver ese fichero para el porqué del pack:// y de la
// mezcla con Segoe UI.
//
// USO
//   · C#   → Icons.Open, $"{Icons.Audio} AUDIO"
//   · XAML → Content="{local:Loc Icon=Open, Es=Abrir, En=Open}"
//            o, sin texto, Content="{x:Static icons:Icons.Search}"
//            (xmlns:icons="clr-namespace:WinCue.Presentation;assembly=WinCue.Presentation")
// =============================================================================

namespace WinCue.Presentation;

/// <summary>Catálogo de iconos: nombre → glifo. Única fuente de verdad.</summary>
public static class Icons
{
    // ── Tipos de cue (espejo de la columna «Tipo» y del timeline) ──────
    public const string Audio = "";      // music-notes
    public const string Video = "";      // monitor-play
    public const string Wait = "";       // timer
    public const string Fade = "";       // wave-sawtooth (la rampa del fundido)
    public const string Start = "";      // play-circle
    public const string Stop = "";       // stop
    public const string Script = "";     // code
    public const string Memo = "";       // note
    public const string Group = "";      // folder

    // ── Transporte ─────────────────────────────────────────────────────
    public const string Play = "";       // play (Start es el mismo en círculo)
    public const string Pause = "";      // pause
    public const string Warning = "";    // warning
    public const string Hourglass = "";  // hourglass

    // ── Barra de herramientas ──────────────────────────────────────────
    public const string Open = "";       // folder-open
    public const string Save = "";       // floppy-disk
    public const string Delete = "";     // trash
    public const string Search = "";     // magnifying-glass
    public const string Add = "";        // plus
    public const string Import = "";     // download-simple
    public const string Relocate = "";   // link

    // ── Paneles y pestañas ─────────────────────────────────────────────
    public const string Inspector = "";  // wrench
    public const string Mixer = "";      // faders
    public const string Log = "";        // book-open
    public const string VideoOutput = "";// monitor
    public const string Settings = "";   // gear
    public const string Timeline = "";   // rows (las pistas del timeline)

    // ── Audio ──────────────────────────────────────────────────────────
    public const string Headphones = ""; // headphones
    public const string Volume = "";     // speaker-high
    public const string Clef = "";       // music-note
    public const string Midi = "";       // piano-keys

    // ── Vídeo ──────────────────────────────────────────────────────────
    public const string Film = "";       // film-strip
    public const string Clapper = "";    // film-slate
    public const string BlackFrame = ""; // square

    // ── Marcas y estados ───────────────────────────────────────────────
    public const string Check = "";      // check
    public const string Cross = "";      // x
    public const string Flag = "";       // flag
    public const string FlagOff = "";    // flag-pennant
    public const string Star = "";       // star
    public const string Info = "";       // info
    public const string Pin = "";        // push-pin
    public const string Crossfade = "";  // shuffle

    // ── Navegación y acciones ──────────────────────────────────────────
    public const string Expanded = "";   // caret-down
    public const string Collapsed = "";  // caret-right
    /// <summary>Marcador de disparo manual en el timeline («GO»). Antes
    /// compartía glifo con Group; ahora tiene el suyo.</summary>
    public const string Go = "";         // cursor-click
    public const string ZoomIn = "";     // magnifying-glass-plus
    public const string ZoomOut = "";    // magnifying-glass-minus
    /// <summary>Bucle infinito en la etiqueta de repeticiones.</summary>
    public const string Loop = "";       // infinity
    public const string Reset = "";      // arrow-counter-clockwise
    public const string Refresh = "";    // arrows-clockwise
    public const string Target = "";     // crosshair
    public const string FollowInto = ""; // arrow-elbow-down-right
    public const string Jump = "";       // arrow-up-right
    public const string Bar = "";        // line-vertical

    // ── Ficheros ───────────────────────────────────────────────────────
    public const string Package = "";    // package
    public const string Document = "";   // file

    /// <summary>«Sin color» en la fila de muestras del inspector.</summary>
    public const string NoColor = "";    // prohibit

    /// <summary>Renumerar (menú contextual de la lista).</summary>
    public const string Number = "";     // hash

    /// <summary>Editar / renombrar. Antes compartía glifo con Memo; ahora es
    /// un lápiz, que es lo que significa aquí.</summary>
    public const string Edit = "";       // pencil-simple

    /// <summary>Botón «examinar».</summary>
    public const string Browse = "";     // dots-three

    /// <summary>Nombre → glifo, para {local:Loc Icon=…} desde XAML. Se arma por
    /// reflexión sobre las constantes de arriba: añadir un icono es una línea.</summary>
    private static readonly Dictionary<string, string> ByName =
        typeof(Icons).GetFields(System.Reflection.BindingFlags.Public |
                                System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .ToDictionary(f => f.Name, f => (string)f.GetRawConstantValue()!,
                          StringComparer.Ordinal);

    /// <summary>Glifo por nombre. Lanza si el nombre no existe: un icono mal
    /// escrito en XAML debe cantar al abrir la ventana, no salir en blanco.</summary>
    public static string Get(string name) =>
        ByName.TryGetValue(name, out var glyph)
            ? glyph
            : throw new ArgumentException($"Icono desconocido: «{name}».", nameof(name));
}
