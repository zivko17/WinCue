// =============================================================================
// IconFont.cs — la FUENTE con la que WPF dibuja los glifos de WinCue.Presentation.
// =============================================================================
// Los nombres y los puntos de código viven en WinCue.Presentation.Icons, que
// no sabe de frameworks. Esto es la mitad que sí: el FontFamily de WPF.
//
// OJO — LA FUENTE NO SE APLICA SUELTA. Phosphor mapea también 'a'..'z', ' ' y
// '-' (los usa como entrada de sus ligaduras), así que ponerla como FontFamily
// de un control convertiría toda la minúscula de la interfaz en iconos. Por eso
// apunta a la fuente COMPUESTA WinCue.CompositeFont, que enruta por rango: el
// PUA a Phosphor y el resto a Segoe UI.
// =============================================================================

using System.Windows.Media;

namespace WinCue.App;

/// <summary>La fuente compuesta con la que se pintan los iconos en WPF.</summary>
public static class IconFont
{
    /// <summary>Tipografía con la que se dibujan estos glifos. Es la fuente
    /// COMPUESTA, no Phosphor a pelo: mezcla icono + texto en la misma cadena
    /// sin romper el texto. Se aplica como fuente por defecto de la app en
    /// App.OnStartup, y a mano en los FormattedText que pintan iconos.</summary>
    public static readonly FontFamily Font =
        new(new Uri("pack://application:,,,/"), "./assets/#WinCue UI");

    /// <summary>Comprueba que la fuente de iconos se puede cargar. Se llama una
    /// vez en App.OnStartup, ANTES de construir la ventana, para que un fallo
    /// salga como excepción con nombre y no como una interfaz llena de
    /// cuadritos.
    ///
    /// La fuente se APLICA en el XAML de cada ventana (FontFamily en la raíz),
    /// no aquí. Se intentó cambiar el valor por defecto de la propiedad, que
    /// habría cubierto la app entera de una vez, pero WPF no deja:
    /// Control.FontFamily ya trae metadata propia y OverrideMetadata revienta
    /// con «ya está registrado para el tipo Control». Como FontFamily se hereda,
    /// fijarla en la raíz de la ventana llega igual a todo lo que cuelga —
    /// incluido lo que se crea en C#.</summary>
    public static void InstallFont()
    {
        // Un URI de fuente equivocado NO da error en WPF: devuelve una familia
        // sin resolver y la app se dibuja entera con la tipografía del sistema
        // — es decir, 185 cuadritos y ni una sola pista de por qué. FamilyNames
        // solo trae el nombre declarado si el .CompositeFont se llegó a leer,
        // así que sirve de acuse de recibo.
        if (!Font.FamilyNames.Values.Contains("WinCue UI", StringComparer.Ordinal))
            throw new InvalidOperationException(
                "No se pudo cargar la fuente de iconos (assets/WinCue.CompositeFont). " +
                "Comprueba que sigue declarada como <Resource> en WinCue.App.csproj.");

    }
}
