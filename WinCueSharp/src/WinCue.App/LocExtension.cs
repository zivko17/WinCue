// =============================================================================
// LocExtension.cs — Extensión de marcado {local:Loc} para traducir XAML
// =============================================================================
// Uso:  Content="{local:Loc Es=Abrir, En=Open}"  (o posicional 'Abrir','Open')
// Con icono: Content="{local:Loc Icon=Open, Es=Abrir, En=Open}"  →  "📂 Abrir"
// Evalúa Loc.T al cargar la ventana (el idioma ya se fijó en App.OnStartup),
// así el XAML estático queda bilingüe sin nombrar cada control.
//
// El icono va APARTE del texto a propósito: antes vivía dentro de la cadena
// traducida ("📂 Abrir" / "📂 Open"), así que cambiarlo obligaba a editar las
// dos traducciones de cada sitio. Ahora el glifo lo pone Icons y la traducción
// solo lleva palabras.
// =============================================================================

using System.Windows.Markup;
using WinCue.Core;

namespace WinCue.App;

[MarkupExtensionReturnType(typeof(string))]
public sealed class LocExtension : MarkupExtension
{
    public string Es { get; set; } = "";
    public string En { get; set; } = "";

    /// <summary>Nombre de un icono de <see cref="Icons"/> (p. ej. "Open"). Se
    /// antepone al texto separado por un espacio. Vacío = sin icono.</summary>
    public string Icon { get; set; } = "";

    /// <summary>Igual que <see cref="Icon"/> pero DETRÁS del texto: "GO ▶",
    /// "SHOW · RESTANTE ▾". Sin esto, los iconos-sufijo tendrían que seguir
    /// escritos a mano dentro de las dos traducciones.</summary>
    public string IconAfter { get; set; } = "";

    public LocExtension() { }
    public LocExtension(string es, string en) { Es = es; En = en; }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        string text = Loc.T(Es, En);
        if (Icon.Length > 0)
        {
            string glyph = Icons.Get(Icon);          // lanza si el nombre no existe
            text = text.Length == 0 ? glyph : $"{glyph} {text}";
        }
        if (IconAfter.Length > 0)
        {
            string glyph = Icons.Get(IconAfter);
            text = text.Length == 0 ? glyph : $"{text} {glyph}";
        }
        return text;
    }
}
