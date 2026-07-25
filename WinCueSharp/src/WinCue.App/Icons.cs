// =============================================================================
// Icons.cs — Catálogo central de iconos de la interfaz
// =============================================================================
// Los 47 iconos de WinCue estaban escritos como emoji SUELTOS en 185 sitios,
// muchos incrustados dentro de cadenas traducidas — Loc.T("🎧 AUDICIÓN", …) —
// lo que hacía imposible cambiarlos sin tocar también la traducción.
//
// Aquí cada icono tiene NOMBRE. Hoy su valor sigue siendo el emoji de siempre
// (nada cambia en pantalla); cuando entre la fuente de iconos OFL bastará con
// sustituir los valores de ESTE fichero por los puntos de código de la fuente,
// y añadir Icons.Font. Ni un solo sitio de llamada se entera.
//
// POR QUÉ UNA FUENTE Y NO UN CONTROL (tipo PackIcon): el 27 % de la interfaz
// se pinta con DrawingContext (timeline, EQ). Ahí no cabe un Control, pero
// FormattedText sí sabe dibujar un glifo. Una fuente sirve a los dos mundos;
// un control, solo al XAML.
//
// USO
//   · C#   → Icons.Open, $"{Icons.Audio} AUDIO"
//   · XAML → Content="{local:Loc Icon=Open, Es=Abrir, En=Open}"
//            o, sin texto, Content="{x:Static local:Icons.Search}"
// =============================================================================

namespace WinCue.App;

/// <summary>Catálogo de iconos: nombre → glifo. Única fuente de verdad.</summary>
public static class Icons
{
    // ── Tipos de cue (espejo de la columna «Tipo» y del timeline) ──────
    public const string Audio = "♪";
    public const string Video = "▣";
    public const string Wait = "⏱";
    public const string Fade = "◢";
    public const string Start = "▶";
    public const string Stop = "■";
    public const string Script = "⚙";
    public const string Memo = "✎";
    public const string Group = "▸";

    // ── Transporte ─────────────────────────────────────────────────────
    public const string Play = Start;          // mismo glifo, otro papel
    public const string Pause = "⏸";
    public const string Warning = "⚠";
    public const string Hourglass = "⏳";

    // ── Barra de herramientas ──────────────────────────────────────────
    public const string Open = "📂";
    public const string Save = "💾";
    public const string Delete = "🗑";
    public const string Search = "🔍";
    public const string Add = "➕";
    public const string Import = "＋";
    public const string Relocate = "🔗";

    // ── Paneles y pestañas ─────────────────────────────────────────────
    public const string Inspector = "🛠";
    public const string Mixer = "🎚";
    public const string Log = "🕮";
    public const string VideoOutput = "🖵";
    public const string Settings = Script;     // mismo glifo, otro papel
    public const string Timeline = Wait;       // mismo glifo, otro papel

    // ── Audio ──────────────────────────────────────────────────────────
    public const string Headphones = "🎧";
    public const string Volume = "🔊";
    public const string Clef = "𝄞";
    public const string Midi = "🎹";

    // ── Vídeo ──────────────────────────────────────────────────────────
    public const string Film = "🎞";
    public const string Clapper = "🎬";
    public const string BlackFrame = "⬛";

    // ── Marcas y estados ───────────────────────────────────────────────
    public const string Check = "✓";
    public const string Cross = "✕";
    public const string Flag = "⚑";
    public const string FlagOff = "⚐";
    public const string Star = "★";
    public const string Info = "ⓘ";
    public const string Pin = "📌";
    public const string Crossfade = "🔀";

    // ── Navegación y acciones ──────────────────────────────────────────
    public const string Expanded = "▾";
    public const string Collapsed = Group;     // mismo glifo, otro papel
    /// <summary>Marcador de disparo manual en el timeline («▸GO»). Comparte
    /// glifo con Group pero NO es lo mismo: al entrar la fuente cada uno irá a
    /// su icono.</summary>
    public const string Go = Group;
    public const string ZoomIn = "＋";          // mismo glifo que Import, otro papel
    public const string ZoomOut = "−";
    /// <summary>Bucle infinito en la etiqueta de repeticiones.</summary>
    public const string Loop = "∞";
    public const string Reset = "↺";
    public const string Refresh = "↻";
    public const string Target = "⌖";
    public const string FollowInto = "⤵";
    public const string Jump = "↗";
    public const string Bar = "▌";

    // ── Ficheros ───────────────────────────────────────────────────────
    public const string Package = "📦";
    public const string Document = "📄";

    /// <summary>«Sin color» en la fila de muestras del inspector.</summary>
    public const string NoColor = "∅";

    /// <summary>Renumerar (menú contextual de la lista).</summary>
    public const string Number = "№";

    /// <summary>Editar / renombrar. Comparte glifo con Memo pero el papel es
    /// otro: aquí es «cambiar el nombre», no «cue de tipo nota».</summary>
    public const string Edit = Memo;

    /// <summary>Botón «examinar». Hoy son puntos suspensivos tipográficos;
    /// cuando entre la fuente, aquí es donde se cambia por un glifo de carpeta
    /// sin tocar los sitios que lo usan.</summary>
    public const string Browse = "…";

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
