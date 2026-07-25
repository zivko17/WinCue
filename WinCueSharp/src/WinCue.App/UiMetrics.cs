// =============================================================================
// UiMetrics.cs — Medidas compartidas de la interfaz
// =============================================================================
// Los faders se dibujaban con medidas puestas a mano en cada sitio: el VOL del
// inspector con Height=170, el GAIN del EQ estirándose a lo que quedara y los
// del mixer con MinHeight=110 y otro ancho. Resultado: tres faders de tres
// tamaños distintos en la misma aplicación, y la lectura en dB unas veces al
// lado y otras debajo.
//
// Aquí viven esas medidas, por el mismo motivo por el que el color vive en
// Theme y los iconos en Icons: para poder cambiarlas en un sitio y que la
// consola siga pareciendo UNA consola.
//
// NO metas colores aquí — esos son de Theme.
// =============================================================================

namespace WinCue.App;

/// <summary>Medidas compartidas de los controles de la interfaz.</summary>
public static class UiMetrics
{
    /// <summary>Ancho de la caja del fader (el pomo se centra en él).</summary>
    public const double FaderWidth = 26;

    /// <summary>Alto de TODOS los faders: VOL del inspector, GAIN del EQ y los
    /// strips del mixer.</summary>
    public const double FaderHeight = 170;

    /// <summary>El MASTER va algo más alto a propósito: es el único que actúa
    /// sobre todo lo que suena, y ese medio centímetro de más basta para
    /// encontrarlo sin leer la etiqueta.</summary>
    public const double MasterFaderHeight = 200;

    /// <summary>Ancho FIJO de la lectura en dB.
    ///
    /// Es fijo porque el texto cambia de ancho al usarlo («+0,0 dB» → «+12,0 dB»)
    /// y, centrado dentro de una pila, ese píxel de más movía la columna entera:
    /// al subir un fader se meneaba medio panel. Reservando el ancho del caso más
    /// largo (signo + dos dígitos + decimal + « dB»), la cifra cambia y nada más
    /// se mueve.</summary>
    public const double DbReadoutWidth = 74;

    /// <summary>Igual que <see cref="DbReadoutWidth"/> pero para la lectura del
    /// inspector, que además del dB lleva el porcentaje («+12,0 dB (400%)»).
    /// Con el ancho corto se cortaría el texto, y un dato recortado es peor que
    /// uno que se mueve.</summary>
    public const double DbReadoutWideWidth = 124;
}
