// =============================================================================
// SettingsDialogMetrics.cs — qué tamaño tiene el cuadro de Ajustes.
// =============================================================================
// El diálogo de Ajustes de Secuence CAMBIABA DE TAMAÑO al cambiar de pestaña:
// un ContentDialog de WinUI 3 «is sized to its content by default» (doc de
// ContentDialog.FullSizeDesired), y el alto del contenido estaba libre hasta
// un MaxHeight — así que la pestaña de Audio (la más alta) daba un cuadro y la
// de Rutas (tres campos) daba otro. El marco saltaba y los botones Guardar /
// Cancelar se movían BAJO EL RATÓN, que es la parte de verdad molesta: en
// medio de una función se pulsa lo que no era.
//
// El arreglo es fijar el alto del contenido. Pero clavarlo a un número no
// vale: uno de los dos monitores de Andrés es de 1024x600 (ver el traspaso),
// donde un contenido de 560 px MÁS el título y la fila de botones no cabe en
// pantalla. Así que el alto se deduce del alto disponible de la ventana, con
// topes — y esa cuenta vive AQUÍ, y no en Secuence.App, por una razón muy
// concreta: en este proyecto sí tiene pruebas. Secuence.App es WinUI 3 y no
// se compila ni se ejecuta fuera de Windows; una resta mal puesta se
// descubriría en pantalla, en el monitor pequeño, y probablemente el día
// menos oportuno.
//
// Secuence es hoy el único consumidor: la ventana de Ajustes de WinCue es una
// Window de WPF con su propio tamaño y NO se toca (regla 1 — WinCue no cambia
// ni de comportamiento ni de aspecto).
// =============================================================================

namespace WinCue.Presentation;

/// <summary>Tamaño del cuadro de Ajustes: fijo, para que no baile al cambiar
/// de pestaña, y adaptado a la ventana para que quepa también en un monitor
/// pequeño.</summary>
public static class SettingsDialogMetrics
{
    /// <summary>Ancho del contenido, en píxeles. Fijo: lo marcan las filas de
    /// la pestaña más ancha (etiqueta de 260 px + su control).</summary>
    public const double ContentWidth = 620;

    /// <summary>Alto que se lleva el propio diálogo alrededor del contenido:
    /// título, fila de botones y los márgenes internos del ContentDialog.
    /// Estimado por exceso a propósito — quedarse corto significa un diálogo
    /// que no cabe en pantalla; pasarse, uno un poco más bajo de lo que
    /// podría.</summary>
    public const double ChromeHeight = 200;

    /// <summary>Alto mínimo del contenido. Por debajo, el cuadro deja de ser
    /// usable aunque quepa: se ve una fila y media y todo lo demás va a
    /// scroll.</summary>
    public const double MinContentHeight = 300;

    /// <summary>Alto máximo del contenido: lo que ocupa la pestaña MÁS ALTA
    /// (Audio) con todos sus grupos desplegados. Subir de aquí solo añadiría
    /// hueco vacío en todas las pestañas.</summary>
    public const double MaxContentHeight = 560;

    /// <summary>Alto FIJO del contenido para una ventana de
    /// <paramref name="availableHeight"/> píxeles de alto (XamlRoot.Size.Height).
    /// El mismo valor para todas las pestañas — de eso se trata: lo que no
    /// quepa lo desplaza el ScrollViewer de cada pestaña, no el marco.
    ///
    /// Con un alto disponible desconocido o absurdo (0 o negativo: la ventana
    /// todavía no está medida) se devuelve el máximo, que es lo que se veía
    /// antes en una pantalla normal.</summary>
    public static double ContentHeight(double availableHeight)
    {
        if (availableHeight <= 0 || double.IsNaN(availableHeight))
            return MaxContentHeight;
        return Math.Clamp(availableHeight - ChromeHeight,
            MinContentHeight, MaxContentHeight);
    }
}
