// =============================================================================
// IconFont.cs — la FUENTE con la que WinUI 3 dibuja los glifos de
// WinCue.Presentation.Icons.
// =============================================================================
// Los nombres y los puntos de código viven en WinCue.Presentation.Icons, que no
// sabe de frameworks. Esto es la mitad que sí: el FontFamily de WinUI 3.
// Equivalente de src/WinCue.App/IconFont.cs, pero un archivo de fuente NO se
// carga igual en los dos mundos:
//
//   · WPF necesita una fuente COMPUESTA (WinCue.CompositeFont) porque Phosphor
//     también mapea 'a'..'z' para sus ligaduras web, y aplicarla suelta como
//     FontFamily de un control convertiría toda la minúscula en iconos.
//   · Aquí no hace falta: la tabla de cues NUNCA mete el glifo del icono en el
//     MISMO TextBlock que texto normal (ver CueRow.cs — el icono va en su
//     propia celda). Sin mezcla, no hay ligadura que evitar, y Phosphor se
//     puede aplicar DIRECTA en el TextBlock del icono.
//
// Y "ms-appx:///" no resuelve aquí: WindowsPackageType=None es una app SIN
// identidad de paquete (no hay MSIX, no hay PRI), así que la fuente se carga
// por RUTA DE ARCHIVO absoluta (file:///…) — el .csproj la copia a la carpeta
// de salida como Content, al lado del ejecutable.
// =============================================================================

using Microsoft.UI.Xaml.Media;

namespace Secuence.App;

/// <summary>La fuente con la que se pintan los iconos de Phosphor en WinUI 3.</summary>
public static class IconFont
{
    /// <summary>Tipografía de los glifos. "Phosphor" es el nombre interno de la
    /// familia dentro del .ttf (ver assets/WinCue.CompositeFont en WinCue.App,
    /// que la referencia igual como Target="./#Phosphor").</summary>
    public static readonly FontFamily Font = CargarDesdeArchivo();

    private static FontFamily CargarDesdeArchivo()
    {
        string ruta = Path.Combine(AppContext.BaseDirectory, "assets", "Phosphor.ttf");
        string uri = "file:///" + ruta.Replace('\\', '/') + "#Phosphor";
        return new FontFamily(uri);
    }
}
