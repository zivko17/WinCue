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

    /// <summary>Alto de TODOS los faders, sin excepción: VOL del inspector,
    /// GAIN del EQ y los strips del mixer — MASTER incluido.</summary>
    public const double FaderHeight = 170;

    /// <summary>Alto de la barra de trim (regla incluida).
    ///
    /// Es FIJO porque la barra solo informa a lo ANCHO: dónde entra y dónde
    /// sale el cue. Dándole el alto sobrante de la pestaña se convertía en una
    /// plancha lisa de lado a lado que no decía nada más que a 72 px, y se
    /// comía el sitio del visor, que sí sabe qué hacer con el alto.</summary>
    public const double TrimBarHeight = 72;

    /// <summary>Alto del rótulo de un strip del mixer.
    ///
    /// Es FIJO por lo mismo que la lectura en dB es de ancho fijo: unos rótulos
    /// llevan icono («🎧 AUDICIÓN») y otros no, y el glifo de la fuente de
    /// iconos pide más línea que la letra normal. Con la línea al gusto de cada
    /// texto, el rótulo con icono bajaba todo lo de debajo y los faders de la
    /// fila quedaban a distinta altura sin que nada lo justificase.</summary>
    public const double StripTitleLineHeight = 18;

    /// <summary>Ancho de la columna de la escala en dB, al costado del fader.</summary>
    public const double FaderScaleWidth = 30;

    /// <summary>Ancho de un strip del mixer: escala + fader + dos VU + aire.</summary>
    public const double StripWidth = 140;

    /// <summary>Alto RESERVADO bajo la lectura en dB de un strip.
    ///
    /// Ahí solo escribe el MASTER (la línea de GR del limitador), pero el hueco
    /// se reserva en TODOS. Motivo: en WPF un TextBlock vacío NO mide cero —
    /// reserva una línea—, así que el MASTER tenía 14 px de fila que los demás
    /// no tenían. Como esa fila es Auto y la del medidor es Star, esos 14 px
    /// salían del fader y le subían la lectura respecto a sus vecinos. Con el
    /// hueco reservado en todos, escriba quien escriba, la fila no se mueve.</summary>
    public const double StripExtrasHeight = 16;

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
