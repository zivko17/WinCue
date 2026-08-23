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
// CORRECCIÓN (verificación en pantalla de Andrés, salieron cuadritos): la
// primera versión de este archivo usaba "file:///<ruta absoluta>#Phosphor" con
// el argumento —falso— de que "ms-appx:///" no resuelve en una app sin
// identidad de paquete. Es al revés: "Microsoft.UI.Xaml.Media.FontFamily" de
// WinUI 3 NO admite el esquema "file:///" (lo ignora en silencio y cae a la
// fuente de sistema — de ahí los cuadritos, no una excepción), y SÍ resuelve
// "ms-appx:///" en apps desempaquetadas, mapeándolo al directorio del
// ejecutable — es el Windows App SDK quien lo resuelve así para XAML, no el
// mecanismo clásico de UWP (Windows.ApplicationModel.Package), que ese sí
// exige identidad. Confirmado en esta máquina: ver Comprobar() más abajo y el
// informe (secuence-shell-report.md, apéndice de este arreglo).
// =============================================================================

using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinCue.Presentation;

namespace Secuence.App;

/// <summary>La fuente con la que se pintan los iconos de Phosphor en WinUI 3.</summary>
public static class IconFont
{
    // Ruta RELATIVA a la carpeta de salida, en minúscula (convención de
    // WinCue: "assets/", no "Assets/"). El .csproj la copia ahí como Content.
    private const string RutaEnPaquete = "assets/Phosphor.ttf";

    /// <summary>Tipografía de los glifos. "Phosphor" es el nombre interno de la
    /// familia dentro del .ttf (ver assets/WinCue.CompositeFont en WinCue.App,
    /// que la referencia igual como Target="./#Phosphor"; y confirmado leyendo
    /// la tabla `name` del propio archivo: Family, Full name y PostScript
    /// coinciden en "Phosphor").</summary>
    public static readonly FontFamily Font = new($"ms-appx:///{RutaEnPaquete}#Phosphor");

    /// <summary>Comprueba que la fuente de iconos se aplicó DE VERDAD, no solo
    /// que el archivo exista en disco — que es justo la distinción que se nos
    /// escapó la primera vez: el .ttf estaba desde el principio (488.636
    /// bytes, copiado por el .csproj) y aun así no se pintaba ningún icono,
    /// porque el esquema de URI era el que WinUI 3 no admite. Un
    /// <c>File.Exists</c> no habría cazado ese fallo.
    ///
    /// La comprobación mide el MISMO glifo con la fuente de iconos y con la
    /// fuente por defecto del sistema: si una familia de iconos (glifos
    /// dibujados a medida) se aplicó de verdad, el ancho medido tiene que
    /// diferir del que da Segoe UI para ese mismo carácter del Área de Uso
    /// Privado (que, si no reconoce el glifo, dibuja su propio "tofu" —de un
    /// ancho fijo y distinto—). Dos anchos IDÉNTICOS son la huella de que las
    /// dos cayeron al mismo sustituto: exactamente lo que vio Andrés en
    /// pantalla como "cuadritos".
    ///
    /// Se llama una vez al construir la ventana principal (ver
    /// MainWindow.xaml.cs), ANTES de mostrar la tabla, para que un fallo
    /// salga como excepción con nombre y no como una interfaz llena de
    /// cuadritos.</summary>
    public static void Comprobar()
    {
        string glifo = Icons.Get(nameof(Icons.Audio));

        var conIcono = new TextBlock { FontFamily = Font, Text = glifo, FontSize = 16 };
        var conDefecto = new TextBlock { Text = glifo, FontSize = 16 };

        var infinito = new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity);
        conIcono.Measure(infinito);
        conDefecto.Measure(infinito);

        double anchoIcono = conIcono.DesiredSize.Width;
        double anchoDefecto = conDefecto.DesiredSize.Width;

        if (anchoIcono <= 0)
            throw new InvalidOperationException(
                $"La fuente de iconos (ms-appx:///{RutaEnPaquete}#Phosphor) midió ancho 0 para el " +
                "glifo de prueba: no se aplicó ninguna fuente. Comprueba que assets/Phosphor.ttf " +
                "sigue como <Content> en Secuence.App.csproj.");

        if (Math.Abs(anchoIcono - anchoDefecto) < 0.5)
            throw new InvalidOperationException(
                $"La fuente de iconos (ms-appx:///{RutaEnPaquete}#Phosphor) no parece haberse " +
                $"aplicado: el glifo mide {anchoIcono:0.0}px igual con ella que con la fuente por " +
                "defecto del sistema (ambas cayeron al mismo sustituto). Comprueba el esquema de " +
                "URI — WinUI 3 no admite \"file:///\" en FontFamily — y que \"ms-appx:///\" resuelve " +
                "en esta app desempaquetada.");
    }
}
