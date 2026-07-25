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
}
