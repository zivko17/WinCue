// =============================================================================
// LocExtension.cs — Extensión de marcado {local:Loc} para traducir XAML
// =============================================================================
// Uso:  Content="{local:Loc Es=Abrir, En=Open}"  (o posicional 'Abrir','Open')
// Evalúa Loc.T al cargar la ventana (el idioma ya se fijó en App.OnStartup),
// así el XAML estático queda bilingüe sin nombrar cada control.
// =============================================================================

using System.Windows.Markup;
using WinCue.Core;

namespace WinCue.App;

[MarkupExtensionReturnType(typeof(string))]
public sealed class LocExtension : MarkupExtension
{
    public string Es { get; set; } = "";
    public string En { get; set; } = "";

    public LocExtension() { }
    public LocExtension(string es, string en) { Es = es; En = en; }

    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.T(Es, En);
}
