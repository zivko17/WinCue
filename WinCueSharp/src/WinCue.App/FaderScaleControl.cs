// =============================================================================
// FaderScaleControl.cs — La escala en dB que acompaña a un fader vertical
// =============================================================================
//
// Vivía dentro de AudioMixerPanel. La usan ya DOS sitios —la mesa y el VOL del
// inspector— y un fader con escala y otro sin ella, midiendo el mismo volumen
// en la misma app, se lee como si midieran cosas distintas.
//
// Las marcas se colocan con la MISMA ley que mueve el fader (AudioLevels), así
// que el rótulo «0» cae exactamente donde el fader da 0 dB. Si se cambia la ley,
// la escala se entera sola — no hay una segunda tabla que mantener a mano.
// =============================================================================

using System.Globalization;
using System.Windows;
using System.Windows.Media;
using WinCue.Core;

namespace WinCue.App;

/// <summary>Escala en dB al costado de un fader vertical.
///
/// El recorrido útil de un Slider vertical de WPF no es todo su alto: el pomo se
/// centra en el valor, así que arriba y abajo sobra medio pomo que hay que
/// descontar o la escala queda corrida respecto al fader.</summary>
public sealed class FaderScaleControl : FrameworkElement
{
    /// <summary>Alto del pomo del fader vertical (App.xaml, FaderThumb).</summary>
    private const double ThumbH = 13.0;

    private static Brush Frozen(Brush b) { b.Freeze(); return b; }
    private static Pen FrozenPen(Pen p) { p.Freeze(); return p; }

    private static readonly Brush TickBrush = Frozen(new SolidColorBrush(Theme.TextDimmer));
    private static readonly Pen TickPen = FrozenPen(new Pen(TickBrush, 1.0));
    private static readonly Pen UnityPen =
        FrozenPen(new Pen(Frozen(new SolidColorBrush(Theme.TextSecondary)), 1.0));

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0)
            return;
        double top = ThumbH / 2.0, travel = h - ThumbH;
        if (travel <= 0)
            return;

        foreach (double db in AudioLevels.ScaleTicks)
        {
            double y = top + (1.0 - AudioLevels.DbToPosition(db)) * travel;
            // La unidad se marca más clara: es la referencia que se busca.
            bool unity = Math.Abs(db) < 0.001;
            dc.DrawLine(unity ? UnityPen : TickPen, new Point(w - 5, y), new Point(w, y));
            var label = Label(db > 0 ? $"+{db:0}" : $"{db:0}",
                              unity ? UnityPen.Brush : TickBrush);
            dc.DrawText(label, new Point(w - 7 - label.Width, y - label.Height / 2.0));
        }

        // AQUÍ IBA un rótulo «dB» de unidades al fondo, y se PISABA con el −60:
        // la ley coloca ese último punto al 97 % del recorrido, o sea en el mismo
        // píxel. Se quita en vez de recolocarlo — la lectura que hay junto al
        // fader ya dice «dB», así que no aportaba nada que no estuviera escrito
        // a dos centímetros.
    }

    private static FormattedText Label(string text, Brush brush) => new(
        text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
        new Typeface("Consolas"), 9, brush, 1.0);
}
